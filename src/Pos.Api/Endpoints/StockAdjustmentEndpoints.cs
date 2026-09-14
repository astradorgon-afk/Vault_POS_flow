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
/// Stock adjustments: damage, spoilage, loss, theft, expiry and corrections,
/// posted to the ledger only after approval by someone other than their author.
/// </summary>
public static class StockAdjustmentEndpoints
{
    /// <summary>Maps the adjustment routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapStockAdjustmentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/inventory/adjustments").WithTags("Inventory control");

        Map(group.MapGet(string.Empty, ListAsync), Permissions.Inventory.View, "ListStockAdjustments", "Lists stock adjustments at the caller's locations.");
        Map(group.MapGet("/{id:guid}", GetAsync), Permissions.Inventory.View, "GetStockAdjustment", "Gets a stock adjustment with its lines.");
        Map(group.MapPost(string.Empty, CreateAsync), Permissions.Inventory.Adjust, "CreateStockAdjustment", "Raises a draft stock adjustment.");
        Map(group.MapPost("/{id:guid}/submit", SubmitAsync), Permissions.Inventory.Adjust, "SubmitStockAdjustment", "Submits an adjustment for approval.");
        Map(group.MapPost("/{id:guid}/approve", ApproveAsync), Permissions.Inventory.ApproveAdjustment, "ApproveStockAdjustment", "Approves an adjustment and posts it to the ledger.");
        Map(group.MapPost("/{id:guid}/reject", RejectAsync), Permissions.Inventory.ApproveAdjustment, "RejectStockAdjustment", "Refuses a submitted adjustment.");
        Map(group.MapPost("/{id:guid}/reverse", ReverseAsync), Permissions.Inventory.ApproveAdjustment, "ReverseStockAdjustment", "Reverses a posted adjustment.");

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
        [FromQuery] StockAdjustmentStatus? status,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<StockAdjustment> query = context.StockAdjustments.AsNoTracking().Include(a => a.Lines);

        if (!authorization.HasAllLocations)
        {
            query = query.Where(a => authorization.Locations.Contains(a.LocationId));
        }

        if (locationId is { } requested)
        {
            LocationId filter = new(requested);
            query = query.Where(a => a.LocationId == filter);
        }

        if (status is { } requestedStatus)
        {
            query = query.Where(a => a.Status == requestedStatus);
        }

        List<StockAdjustment> adjustments = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(adjustments.Select(Summary).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        [FromServices] PosDbContext context,
        [FromServices] IPermissionEvaluator evaluator,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        StockAdjustmentId adjustmentId = new(id);

        StockAdjustment? adjustment = await context.StockAdjustments
            .AsNoTracking()
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.Id == adjustmentId, cancellationToken)
            .ConfigureAwait(false);

        if (adjustment is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result<StockAdjustmentDetail>.Failure(InventoryControlErrors.AdjustmentUnknown(adjustmentId)),
                currentUser.CorrelationId.Value);
        }

        if (!await evaluator
                .HasPermissionAsync(currentUser.UserId ?? UserId.Empty, Permissions.Inventory.View, adjustment.LocationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return ProblemDetailsMapping.ToProblem(
                Result<StockAdjustmentDetail>.Failure(InventoryControlErrors.OutsideScope),
                currentUser.CorrelationId.Value);
        }

        return TypedResults.Ok(new StockAdjustmentDetail(
            Summary(adjustment),
            adjustment.Notes,
            adjustment.SubmittedAtUtc,
            adjustment.DecidedByUserId?.Value,
            adjustment.DecidedAtUtc,
            adjustment.RejectionReason,
            adjustment.ReversedByUserId?.Value,
            adjustment.ReversedAtUtc,
            adjustment.ReversalReason,
            [.. adjustment.Lines.OrderBy(l => l.LineNo).Select(l => new StockAdjustmentLineView(
                l.LineNo,
                l.ProductId.Value,
                l.BatchId?.Value,
                l.State.ToString(),
                l.QuantityDelta,
                l.UnitCost,
                l.AbsoluteValue,
                l.MovementType.ToString()))]));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateStockAdjustmentBody body,
        [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<StockAdjustmentId> result = await dispatcher
            .SendAsync(
                new CreateStockAdjustmentCommand(
                    new LocationId(body.LocationId),
                    body.Reason,
                    body.Notes,
                    [.. (body.Lines ?? []).Select(l => new StockAdjustmentLineInput(
                        new ProductId(l.ProductId),
                        l.BatchId is { } batchId ? new BatchId(batchId) : null,
                        l.State,
                        l.QuantityDelta))]),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created(
                FormattableString.Invariant($"/api/v1/inventory/adjustments/{result.Value.Value}"),
                new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> SubmitAsync(
        Guid id, [FromServices] IDispatcher dispatcher, [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new SubmitStockAdjustmentCommand(new StockAdjustmentId(id)), cancellationToken);

    private static Task<IResult> ApproveAsync(
        Guid id, [FromServices] IDispatcher dispatcher, [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new ApproveStockAdjustmentCommand(new StockAdjustmentId(id)), cancellationToken);

    private static Task<IResult> RejectAsync(
        Guid id, [FromBody] InventoryControlReasonBody body, [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new RejectStockAdjustmentCommand(new StockAdjustmentId(id), body.Reason), cancellationToken);

    private static Task<IResult> ReverseAsync(
        Guid id, [FromBody] InventoryControlReasonBody body, [FromServices] IDispatcher dispatcher,
        [FromServices] ICurrentUser currentUser, CancellationToken cancellationToken)
        => SendAsync(dispatcher, currentUser, new ReverseStockAdjustmentCommand(new StockAdjustmentId(id), body.Reason), cancellationToken);

    private static async Task<IResult> SendAsync(
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        ICommand<StockAdjustmentId> command,
        CancellationToken cancellationToken)
    {
        Result<StockAdjustmentId> result = await dispatcher.SendAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static StockAdjustmentSummary Summary(StockAdjustment adjustment)
        => new(
            adjustment.Id.Value,
            adjustment.Number,
            adjustment.LocationId.Value,
            adjustment.Reason.ToString(),
            adjustment.Status.ToString(),
            adjustment.TotalAbsoluteValue,
            adjustment.CreatedByUserId.Value,
            adjustment.CreatedAtUtc);
}
