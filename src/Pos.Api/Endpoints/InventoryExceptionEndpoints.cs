using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>One refused stock draw, as listed on the exception report.</summary>
public sealed record NegativeStockAttemptView(
    Guid Id,
    DateTimeOffset AttemptedAtUtc,
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    Guid? BatchId,
    string State,
    string MovementType,
    decimal RequestedQuantity,
    decimal AvailableQuantity,
    decimal Shortfall,
    string Policy,
    string ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    string ReferenceNumber,
    Guid UserId,
    Guid? DeviceId,
    Guid CorrelationId);

/// <summary>Refused draws for one product at one location over the report window.</summary>
public sealed record NegativeStockAttemptSummaryRow(
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    int Attempts,
    decimal TotalShortfall,
    DateTimeOffset FirstAttemptAtUtc,
    DateTimeOffset LastAttemptAtUtc);

/// <summary>
/// The inventory exception report: stock draws the ledger refused. Repeated
/// attempts on one product and location are a shrinkage signal, so the summary
/// ranks them.
/// </summary>
public static class InventoryExceptionEndpoints
{
    /// <summary>The window a report covers when the caller names none.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>The most attempts a summary aggregates; aggregation runs in memory.</summary>
    private const int SummaryRowCap = 20_000;

    private static readonly Error RangeInvalid = Error.Validation(
        "inventory.report_range_invalid", "The report's 'from' must be before its 'to'.");

    /// <summary>Maps the exception report routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapInventoryExceptionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/inventory/exceptions").WithTags("Inventory");

        group.MapGet("/negative-attempts", ListAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.ViewAll) { Scope = ScopeSource.None })
            .WithName("ListNegativeStockAttempts")
            .WithSummary("Lists stock draws the ledger refused, newest first.");

        group.MapGet("/negative-attempts/summary", SummaryAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.ViewAll) { Scope = ScopeSource.None })
            .WithName("SummarizeNegativeStockAttempts")
            .WithSummary("Ranks products and locations by refused stock draws.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PosDbContext context,
        ICurrentUser currentUser,
        ISystemClock clock,
        [FromQuery] Guid? locationId,
        [FromQuery] Guid? productId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (Window(clock, from, to) is not { } window)
        {
            return ProblemDetailsMapping.ToProblem(Result.Failure(RangeInvalid), currentUser.CorrelationId.Value);
        }

        List<NegativeStockAttempt> attempts = await Filter(context, window, locationId, productId)
            .OrderByDescending(a => a.AttemptedAtUtc)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Names names = await NamesAsync(context, attempts, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(attempts.Select(a => new NegativeStockAttemptView(
            a.Id.Value,
            a.AttemptedAtUtc,
            a.LocationId.Value,
            names.LocationCode(a.LocationId),
            a.ProductId.Value,
            names.Sku(a.ProductId),
            names.ProductName(a.ProductId),
            a.BatchKey.IsEmpty ? null : a.BatchKey.Value,
            a.State.ToString(),
            a.MovementType.ToString(),
            a.RequestedQuantity,
            a.AvailableQuantity,
            a.Shortfall,
            a.Policy.ToString(),
            a.ReferenceDocumentType.ToString(),
            a.ReferenceDocumentId,
            a.ReferenceNumber,
            a.UserId.Value,
            a.DeviceId?.Value,
            a.CorrelationId.Value)).ToList());
    }

    private static async Task<IResult> SummaryAsync(
        PosDbContext context,
        ICurrentUser currentUser,
        ISystemClock clock,
        [FromQuery] Guid? locationId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken = default)
    {
        if (Window(clock, from, to) is not { } window)
        {
            return ProblemDetailsMapping.ToProblem(Result.Failure(RangeInvalid), currentUser.CorrelationId.Value);
        }

        // Quantities are summed in memory: SQLite stores decimals as text, so a
        // SQL SUM would not be exact on the offline engine.
        List<NegativeStockAttempt> attempts = await Filter(context, window, locationId, productId: null)
            .OrderByDescending(a => a.AttemptedAtUtc)
            .Take(SummaryRowCap)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Names names = await NamesAsync(context, attempts, cancellationToken).ConfigureAwait(false);

        List<NegativeStockAttemptSummaryRow> rows = [.. attempts
            .GroupBy(a => (a.LocationId, a.ProductId))
            .Select(g => new NegativeStockAttemptSummaryRow(
                g.Key.LocationId.Value,
                names.LocationCode(g.Key.LocationId),
                g.Key.ProductId.Value,
                names.Sku(g.Key.ProductId),
                names.ProductName(g.Key.ProductId),
                g.Count(),
                g.Sum(a => a.Shortfall),
                g.Min(a => a.AttemptedAtUtc),
                g.Max(a => a.AttemptedAtUtc)))
            .OrderByDescending(r => r.Attempts)
            .ThenByDescending(r => r.TotalShortfall)];

        return TypedResults.Ok(rows);
    }

    private static (DateTimeOffset From, DateTimeOffset To)? Window(
        ISystemClock clock, DateTimeOffset? from, DateTimeOffset? to)
    {
        DateTimeOffset end = to ?? clock.UtcNow;
        DateTimeOffset start = from ?? end - DefaultWindow;
        return start < end ? (start, end) : null;
    }

    private static IQueryable<NegativeStockAttempt> Filter(
        PosDbContext context,
        (DateTimeOffset From, DateTimeOffset To) window,
        Guid? locationId,
        Guid? productId)
    {
        IQueryable<NegativeStockAttempt> query = context.NegativeStockAttempts
            .AsNoTracking()
            .Where(a => a.AttemptedAtUtc >= window.From && a.AttemptedAtUtc < window.To);

        if (locationId is { } location)
        {
            LocationId filter = new(location);
            query = query.Where(a => a.LocationId == filter);
        }

        if (productId is { } product)
        {
            ProductId filter = new(product);
            query = query.Where(a => a.ProductId == filter);
        }

        return query;
    }

    private static async Task<Names> NamesAsync(
        PosDbContext context,
        IReadOnlyCollection<NegativeStockAttempt> attempts,
        CancellationToken cancellationToken)
    {
        LocationId[] locationIds = [.. attempts.Select(a => a.LocationId).Distinct()];
        ProductId[] productIds = [.. attempts.Select(a => a.ProductId).Distinct()];

        Dictionary<LocationId, string> locations = await context.Locations
            .AsNoTracking()
            .Where(l => locationIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, (string Sku, string Name)> products = (await context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, Sku = p.Sku.Value, p.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(p => p.Id, p => (p.Sku, p.Name));

        return new Names(locations, products);
    }

    private sealed record Names(
        IReadOnlyDictionary<LocationId, string> Locations,
        IReadOnlyDictionary<ProductId, (string Sku, string Name)> Products)
    {
        public string? LocationCode(LocationId id) => Locations.GetValueOrDefault(id);

        public string? Sku(ProductId id) => Products.TryGetValue(id, out (string Sku, string Name) p) ? p.Sku : null;

        public string? ProductName(ProductId id) => Products.TryGetValue(id, out (string Sku, string Name) p) ? p.Name : null;
    }
}
