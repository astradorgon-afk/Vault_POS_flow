using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>
/// Inventory counts — full, cycle, category and product counts — and the variance
/// reports built from posted counts.
/// </summary>
public static class InventoryCountEndpoints
{
    private static readonly TimeSpan DefaultReportWindow = TimeSpan.FromDays(90);

    private static readonly Error RangeInvalid = Error.Validation(
        "inventory.report_range_invalid", "The report's 'from' must be before its 'to'.");

    /// <summary>Maps the count routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapInventoryCountEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/inventory/counts").WithTags("Inventory control");

        Map(group.MapGet(string.Empty, ListAsync), Permissions.Inventory.View, "ListInventoryCounts", "Lists counts at the caller's locations.");
        Map(group.MapGet("/variances", VariancesAsync), Permissions.Inventory.View, "ListCountVariances", "Lists variances found by posted counts.");
        Map(group.MapGet("/repeat-variances", RepeatVariancesAsync), Permissions.Inventory.View, "ListRepeatCountVariances", "Ranks products that varied on more than one posted count.");
        Map(group.MapGet("/{id:guid}", GetAsync), Permissions.Inventory.View, "GetInventoryCount", "Gets a count with its lines.");
        Map(group.MapPost(string.Empty, OpenAsync), Permissions.Inventory.Count, "OpenInventoryCount", "Opens a count and takes its sheet from the ledger.");
        Map(group.MapPost("/{id:guid}/lines", RecordAsync), Permissions.Inventory.Count, "RecordInventoryCountLines", "Records counted quantities.");
        Map(group.MapPost("/{id:guid}/submit", SubmitAsync), Permissions.Inventory.Count, "SubmitInventoryCount", "Submits a fully counted sheet for approval.");
        Map(group.MapPost("/{id:guid}/approve", ApproveAsync), Permissions.Inventory.ApproveCount, "ApproveInventoryCount", "Approves a count and posts its variance.");
        Map(group.MapPost("/{id:guid}/reject", RejectAsync), Permissions.Inventory.ApproveCount, "RejectInventoryCount", "Sends a count back for recounting.");
        Map(group.MapPost("/{id:guid}/cancel", CancelAsync), Permissions.Inventory.Count, "CancelInventoryCount", "Abandons a count without posting.");

        return app;
    }

    private static void Map(RouteHandlerBuilder route, string permission, string name, string summary)
        => route
            .WithMetadata(new RequirePermissionAttribute(permission) { Scope = ScopeSource.None })
            .WithName(name)
            .WithSummary(summary);

    private static async Task<IResult> ListAsync(
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromQuery] Guid? locationId,
        [FromQuery] InventoryCountStatus? status,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        IQueryable<InventoryCount> query = await ScopedAsync(context, evaluator, currentUser, locationId, cancellationToken)
            .ConfigureAwait(false);

        if (status is { } requested)
        {
            query = query.Where(c => c.Status == requested);
        }

        List<InventoryCount> counts = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(counts.Select(Summary).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        [FromServices] PosDbContext context,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        InventoryCountId countId = new(id);

        InventoryCount? count = await context.InventoryCounts
            .AsNoTracking()
            .Include(c => c.Lines)
            .FirstOrDefaultAsync(c => c.Id == countId, cancellationToken)
            .ConfigureAwait(false);

        if (count is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<InventoryCountDetail>.Failure(InventoryControlErrors.CountUnknown(countId)), currentUser.CorrelationId.Value);
        }

        if (!await evaluator
                .HasPermissionAsync(currentUser.UserId ?? UserId.Empty, Permissions.Inventory.View, count.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<InventoryCountDetail>.Failure(InventoryControlErrors.OutsideScope), currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(new InventoryCountDetail(
            Summary(count),
            count.Note,
            count.SnapshotTakenAtUtc,
            count.CreatedByUserId.Value,
            count.SubmittedByUserId?.Value,
            count.ApprovedByUserId?.Value,
            count.LastRejectionReason,
            count.CancellationReason,
            [.. count.Lines.OrderBy(l => l.LineNo).Select(l => new InventoryCountLineView(
                l.LineNo, l.ProductId.Value, l.BatchId?.Value, l.SystemQuantity, l.PhysicalQuantity, l.Variance,
                l.UnitCost, l.VarianceValue, l.IsRepeatVariance, l.CountedAtUtc))]));
    }

    private static async Task<IResult> VariancesAsync(
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ISystemClock clock,
        [FromQuery] Guid? locationId,
        [FromQuery] Guid? productId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (await PostedInWindowAsync(context, evaluator, currentUser, clock, locationId, from, to, cancellationToken)
                .ConfigureAwait(false) is not { } counts)
        {
            return ProblemDetailsMapping.ToProblem(Result.Failure(RangeInvalid), currentUser.CorrelationId.Value);
        }

        List<(InventoryCount Count, InventoryCountLine Line)> varying = [.. counts
            .SelectMany(c => c.Lines.Select(l => (Count: c, Line: l)))
            .Where(x => x.Line.Variance is { } v && v != 0m
                        && (productId is null || x.Line.ProductId.Value == productId))
            .OrderByDescending(x => x.Count.PostedAtUtc)
            .ThenBy(x => x.Line.LineNo)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 500))];

        Dictionary<ProductId, (string Sku, string Name)> names = await NamesAsync(
            context, varying.Select(x => x.Line.ProductId), cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(varying.Select(x => new CountVarianceRow(
            x.Count.Id.Value,
            x.Count.Number,
            x.Count.PostedAtUtc!.Value,
            x.Count.LocationId.Value,
            x.Line.ProductId.Value,
            names.TryGetValue(x.Line.ProductId, out (string Sku, string Name) n) ? n.Sku : null,
            names.TryGetValue(x.Line.ProductId, out (string Sku, string Name) m) ? m.Name : null,
            x.Line.BatchId?.Value,
            x.Line.SystemQuantity,
            x.Line.PhysicalQuantity!.Value,
            x.Line.Variance!.Value,
            x.Line.VarianceValue!.Value,
            x.Line.IsRepeatVariance)).ToList());
    }

    private static async Task<IResult> RepeatVariancesAsync(
        [FromServices] PosDbContext context,
        [FromServices] DatabasePermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        [FromServices] ISystemClock clock,
        [FromQuery] Guid? locationId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int minOccurrences = 2,
        CancellationToken cancellationToken = default)
    {
        if (await PostedInWindowAsync(context, evaluator, currentUser, clock, locationId, from, to, cancellationToken)
                .ConfigureAwait(false) is not { } counts)
        {
            return ProblemDetailsMapping.ToProblem(Result.Failure(RangeInvalid), currentUser.CorrelationId.Value);
        }

        var groups = counts
            .SelectMany(c => c.Lines
                .Where(l => l.Variance is { } v && v != 0m)
                .Select(l => (Count: c, Line: l)))
            .GroupBy(x => (x.Count.LocationId, x.Line.ProductId))
            .Select(g => new
            {
                g.Key.LocationId,
                g.Key.ProductId,
                Occurrences = g.Select(x => x.Count.Id).Distinct().Count(),
                NetVariance = g.Sum(x => x.Line.Variance!.Value),
                TotalValue = g.Sum(x => Math.Abs(x.Line.VarianceValue!.Value)),
                Last = g.Max(x => x.Count.PostedAtUtc!.Value),
            })
            .Where(g => g.Occurrences >= Math.Max(2, minOccurrences))
            .OrderByDescending(g => g.Occurrences)
            .ThenByDescending(g => g.TotalValue)
            .ToList();

        Dictionary<ProductId, (string Sku, string Name)> names = await NamesAsync(
            context, groups.Select(g => g.ProductId), cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(groups.Select(g => new RepeatVarianceRow(
            g.LocationId.Value,
            g.ProductId.Value,
            names.TryGetValue(g.ProductId, out (string Sku, string Name) n) ? n.Sku : null,
            names.TryGetValue(g.ProductId, out (string Sku, string Name) m) ? m.Name : null,
            g.Occurrences,
            g.NetVariance,
            g.TotalValue,
            g.Last)).ToList());
    }

    private static async Task<IResult> OpenAsync(
        [FromBody] OpenInventoryCountBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<InventoryCountId> result = await dispatcher
            .SendAsync(
                new OpenInventoryCountCommand(
                    new LocationId(body.LocationId),
                    body.Kind,
                    [.. (body.CategoryIds ?? []).Select(id => new CategoryId(id))],
                    [.. (body.ProductIds ?? []).Select(id => new ProductId(id))],
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/inventory/counts/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> RecordAsync(
        Guid id, [FromBody] RecordCountLinesBody body, [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(
            dispatcher,
            currentUser,
            new RecordCountLinesCommand(
                new InventoryCountId(id),
                [.. (body.Lines ?? []).Select(l => new CountLineInput(
                    new ProductId(l.ProductId),
                    l.BatchId is { } batchId ? new BatchId(batchId) : null,
                    l.PhysicalQuantity))]),
            cancellationToken);

    private static Task<IResult> SubmitAsync(
        Guid id, [FromServices] IDispatcher dispatcher, [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new SubmitInventoryCountCommand(new InventoryCountId(id)), cancellationToken);

    private static Task<IResult> ApproveAsync(
        Guid id, [FromServices] IDispatcher dispatcher, [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new ApproveInventoryCountCommand(new InventoryCountId(id)), cancellationToken);

    private static Task<IResult> RejectAsync(
        Guid id, [FromBody] InventoryControlReasonBody body, [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new RejectInventoryCountCommand(new InventoryCountId(id), body.Reason), cancellationToken);

    private static Task<IResult> CancelAsync(
        Guid id, [FromBody] InventoryControlReasonBody body, [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new CancelInventoryCountCommand(new InventoryCountId(id), body.Reason), cancellationToken);

    private static async Task<IResult> SendAsync(
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        ICommand<InventoryCountId> command,
        CancellationToken cancellationToken)
    {
        Result<InventoryCountId> result = await dispatcher.SendAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IQueryable<InventoryCount>> ScopedAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<InventoryCount> query = context.InventoryCounts.AsNoTracking().Include(c => c.Lines);

        // Confined to the caller's locations unless they act business-wide; a filter
        // on a location outside that scope returns nothing rather than revealing it.
        if (!authorization.HasAllLocations)
        {
            query = query.Where(c => authorization.Locations.Contains(c.LocationId));
        }

        if (locationId is { } requested)
        {
            LocationId filter = new(requested);
            query = query.Where(c => c.LocationId == filter);
        }

        return query;
    }

    private static async Task<List<InventoryCount>?> PostedInWindowAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        ISystemClock clock,
        Guid? locationId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        DateTimeOffset end = to ?? clock.UtcNow;
        DateTimeOffset start = from ?? end - DefaultReportWindow;

        if (start >= end)
        {
            return null;
        }

        IQueryable<InventoryCount> query = await ScopedAsync(context, evaluator, currentUser, locationId, cancellationToken)
            .ConfigureAwait(false);

        return await query
            .Where(c => c.Status == InventoryCountStatus.Posted && c.PostedAtUtc >= start && c.PostedAtUtc < end)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<ProductId, (string Sku, string Name)>> NamesAsync(
        PosDbContext context,
        IEnumerable<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        ProductId[] ids = [.. productIds.Distinct()];

        return (await context.Products
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, Sku = p.Sku.Value, p.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(p => p.Id, p => (p.Sku, p.Name));
    }

    private static InventoryCountSummary Summary(InventoryCount count)
        => new(
            count.Id.Value,
            count.Number,
            count.LocationId.Value,
            count.Kind.ToString(),
            count.Status.ToString(),
            count.Lines.Count,
            count.Lines.Count(l => l.PhysicalQuantity is not null),
            count.TotalAbsoluteVarianceValue,
            count.CreatedAtUtc,
            count.PostedAtUtc);
}
