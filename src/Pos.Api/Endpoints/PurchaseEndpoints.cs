using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Purchasing;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a purchase order creation.</summary>
/// <param name="SupplierId">The supplier.</param>
/// <param name="DestinationLocationId">The location receiving the goods.</param>
/// <param name="Lines">The ordered lines, in the product's base unit.</param>
/// <param name="CurrencyCode">Three-letter ISO-4217 currency code, defaulting to PHP.</param>
/// <param name="ExpectedAtUtc">Expected delivery instant, or null.</param>
public sealed record CreatePurchaseOrderBody(
    Guid SupplierId,
    Guid DestinationLocationId,
    IReadOnlyList<CreatePurchaseOrderLineBody> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null);

/// <summary>One line of a purchase order creation.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="UnitOfMeasureId">The base unit of the product.</param>
/// <param name="OrderedQuantity">The quantity to order.</param>
/// <param name="UnitCost">The cost per base unit.</param>
public sealed record CreatePurchaseOrderLineBody(
    Guid ProductId,
    Guid UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost);

/// <summary>The body of an approval decision.</summary>
/// <param name="Notes">Optional notes attached to the decision.</param>
public sealed record PurchaseOrderDecisionBody(string? Notes = null);

/// <summary>The body of a cancel or close.</summary>
/// <param name="Reason">The reason, required for non-draft cancels and set on closes.</param>
public sealed record PurchaseOrderCancellationBody(string? Reason = null);

/// <summary>The body of a discrepancy resolution.</summary>
/// <param name="Outcome">The closing decision.</param>
/// <param name="Note">Optional note attached to the resolution.</param>
public sealed record ResolveReceivingDiscrepancyBody(
    ReceivingDiscrepancyResolutionOutcome Outcome,
    string? Note = null);

/// <summary>A purchase order as listed.</summary>
public sealed record PurchaseOrderSummary(
    Guid Id,
    string? Number,
    string Status,
    Guid SupplierId,
    Guid DestinationLocationId,
    decimal GrandTotal,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? OrderedAtUtc);

/// <summary>A purchase order line.</summary>
public sealed record PurchaseOrderLineSummary(
    Guid Id,
    int LineNo,
    Guid ProductId,
    Guid UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost,
    decimal LineTotal);

/// <summary>An approval decision recorded against a purchase order.</summary>
public sealed record PurchaseApprovalSummary(
    Guid ApproverUserId,
    string Decision,
    DateTimeOffset DecidedAtUtc,
    string? Notes,
    decimal ThresholdApplied);

/// <summary>A purchase order with its lines and decisions.</summary>
public sealed record PurchaseOrderDetail(
    Guid Id,
    string? Number,
    string Status,
    Guid SupplierId,
    Guid DestinationLocationId,
    string CurrencyCode,
    decimal Subtotal,
    decimal TaxTotal,
    decimal GrandTotal,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? OrderedAtUtc,
    DateTimeOffset? ExpectedAtUtc,
    string? CancelledReason,
    string? ClosedReason,
    IReadOnlyList<PurchaseOrderLineSummary> Lines,
    IReadOnlyList<PurchaseApprovalSummary> Approvals);

/// <summary>The body of a goods receipt creation.</summary>
/// <param name="Lines">The received lines with their disposition plans.</param>
/// <param name="DocumentsMissing">Whether the delivery arrived without the expected documents.</param>
public sealed record CreateGoodsReceiptBody(
    IReadOnlyList<CreateGoodsReceiptLineBody> Lines,
    bool DocumentsMissing = false);

/// <summary>One line of a goods receipt creation.</summary>
/// <param name="PurchaseOrderLineId">The purchase order line being received against.</param>
/// <param name="QuantityReceived">The quantity physically counted on arrival.</param>
/// <param name="QuantityDamaged">Units counted but refused as damaged.</param>
/// <param name="QuantityWrongItem">Units counted but refused as the wrong item.</param>
/// <param name="QuantityExpired">Units counted but refused as expired on arrival.</param>
/// <param name="UnitCost">The actual purchase cost per unit for this delivery.</param>
/// <param name="LotNumber">The supplier's lot number, required for batch-tracked products.</param>
/// <param name="ManufacturedOn">The manufacture date, where recorded.</param>
/// <param name="ExpiresOn">The expiry date, required for expiry-tracked products.</param>
public sealed record CreateGoodsReceiptLineBody(
    Guid PurchaseOrderLineId,
    decimal QuantityReceived,
    decimal QuantityDamaged,
    decimal QuantityWrongItem,
    decimal QuantityExpired,
    decimal UnitCost,
    string? LotNumber = null,
    DateOnly? ManufacturedOn = null,
    DateOnly? ExpiresOn = null);

/// <summary>A goods receipt as listed.</summary>
public sealed record GoodsReceiptSummary(
    Guid Id,
    string Number,
    string Status,
    Guid PurchaseOrderId,
    Guid SupplierId,
    Guid DestinationLocationId,
    bool DocumentsMissing,
    DateOnly BusinessDate,
    DateTimeOffset ReceivedAtUtc,
    Guid ReceivedByUserId,
    bool CostVariancePendingApproval,
    decimal CostVarianceValueAtStake);

/// <summary>A goods receipt line.</summary>
public sealed record GoodsReceiptLineSummary(
    int LineNo,
    Guid PurchaseOrderLineId,
    Guid ProductId,
    decimal QuantityExpected,
    decimal QuantityReceived,
    decimal QuantityDamaged,
    decimal QuantityWrongItem,
    decimal QuantityExpired,
    decimal OverageBeyondTolerance,
    decimal QuantityAccepted,
    string AcceptedState,
    decimal UnitCost,
    string? LotNumber,
    DateOnly? ManufacturedOn,
    DateOnly? ExpiresOn,
    decimal CostVariancePercent,
    Guid? CostVarianceApprovedByUserId,
    DateTimeOffset? CostVarianceApprovedAtUtc);

/// <summary>A receiving discrepancy recorded against a receipt.</summary>
public sealed record ReceivingDiscrepancySummary(
    Guid Id,
    Guid PurchaseOrderLineId,
    int LineNo,
    string Kind,
    decimal Quantity,
    decimal ValueImpact,
    string? ResolutionOutcome = null,
    string? ResolutionNote = null,
    Guid? ResolvedByUserId = null,
    DateTimeOffset? ResolvedAtUtc = null);

/// <summary>A goods receipt with its lines and discrepancies.</summary>
public sealed record GoodsReceiptDetail(
    Guid Id,
    string Number,
    string Status,
    Guid PurchaseOrderId,
    Guid SupplierId,
    Guid DestinationLocationId,
    bool DocumentsMissing,
    DateOnly BusinessDate,
    DateTimeOffset ReceivedAtUtc,
    Guid ReceivedByUserId,
    bool CostVariancePendingApproval,
    decimal CostVarianceValueAtStake,
    IReadOnlyList<GoodsReceiptLineSummary> Lines,
    IReadOnlyList<ReceivingDiscrepancySummary> Discrepancies);

/// <summary>Purchase order lifecycle endpoints.</summary>
public static class PurchaseEndpoints
{
    /// <summary>Maps the purchase order routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/purchasing").WithTags("Purchasing");

        group.MapGet("/orders", ListPurchaseOrdersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListPurchaseOrders")
            .WithSummary("Lists purchase orders.");

        group.MapPost("/orders", CreatePurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreatePurchaseOrder")
            .WithSummary("Creates a purchase order draft.");

        group.MapGet("/orders/{id:guid}", GetPurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetPurchaseOrder")
            .WithSummary("Gets one purchase order by identifier.");

        group.MapPost("/orders/{id:guid}/submit", SubmitPurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("SubmitPurchaseOrder")
            .WithSummary("Submits a draft for approval and allocates its number.");

        group.MapPost("/orders/{id:guid}/approve", ApprovePurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ApprovePurchaseOrder")
            .WithSummary("Approves a submitted purchase order.");

        group.MapPost("/orders/{id:guid}/reject", RejectPurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RejectPurchaseOrder")
            .WithSummary("Rejects a submitted purchase order.");

        group.MapPost("/orders/{id:guid}/send", SendPurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("SendPurchaseOrder")
            .WithSummary("Marks an approved order as sent to the supplier.");

        group.MapPost("/orders/{id:guid}/cancel", CancelPurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CancelPurchaseOrder")
            .WithSummary("Cancels a submitted purchase order.");

        group.MapDelete("/orders/{id:guid}", WithdrawPurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Create)
            {
                Scope = ScopeSource.None,
            })
            .WithName("WithdrawPurchaseOrder")
            .WithSummary("Withdraws a draft that has never been submitted.");

        group.MapPost("/orders/{id:guid}/close", ClosePurchaseOrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ClosePurchaseOrder")
            .WithSummary("Closes an ordered purchase order.");

        group.MapPost("/orders/{id:guid}/receipts", CreateGoodsReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Receive)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateGoodsReceipt")
            .WithSummary("Posts a goods receipt against the order.");

        group.MapGet("/orders/{id:guid}/receipts", ListGoodsReceiptsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListGoodsReceipts")
            .WithSummary("Lists the goods receipts against the order.");

        group.MapGet("/orders/{id:guid}/receipts/{receiptId:guid}", GetGoodsReceiptAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetGoodsReceipt")
            .WithSummary("Gets one goods receipt by identifier.");

        group.MapPost("/receiving-discrepancies/{id:guid}/resolve", ResolveReceivingDiscrepancyAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.ResolveDiscrepancy)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ResolveReceivingDiscrepancy")
            .WithSummary("Closes a receiving discrepancy with a resolution decision.");

        return app;
    }

    private static async Task<IResult> ListPurchaseOrdersAsync(
        PosDbContext context,
        [FromQuery] PurchaseOrderStatus? status,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        int boundedOffset = Math.Max(0, offset);
        int boundedLimit = Math.Clamp(limit, 1, 200);

        IQueryable<PurchaseOrder> query = context.PurchaseOrders.AsNoTracking();

        if (status is { } filter)
        {
            query = query.Where(o => o.Status == filter);
        }

        List<PurchaseOrderSummary> orders = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip(boundedOffset)
            .Take(boundedLimit)
            .Select(o => new PurchaseOrderSummary(
                o.Id.Value,
                o.Number,
                o.Status.ToString(),
                o.SupplierId.Value,
                o.DestinationLocationId.Value,
                o.GrandTotal,
                o.CreatedByUserId.Value,
                o.CreatedAtUtc,
                o.OrderedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(orders);
    }

    private static async Task<IResult> CreatePurchaseOrderAsync(
        [FromBody] CreatePurchaseOrderBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<PurchaseOrderId> result = await dispatcher
            .SendAsync(
                new CreatePurchaseOrderCommand(
                    new SupplierId(body.SupplierId),
                    new LocationId(body.DestinationLocationId),
                    [.. body.Lines.Select(l => new PurchaseOrderLineSpec(
                        new ProductId(l.ProductId),
                        new UnitOfMeasureId(l.UnitOfMeasureId),
                        l.OrderedQuantity,
                        l.UnitCost))],
                    body.CurrencyCode,
                    body.ExpectedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetPurchaseOrderAsync(
        PosDbContext context,
        Guid id,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        PurchaseOrderDetail? detail = await context.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.Id == new PurchaseOrderId(id))
            .Select(o => new PurchaseOrderDetail(
                o.Id.Value,
                o.Number,
                o.Status.ToString(),
                o.SupplierId.Value,
                o.DestinationLocationId.Value,
                o.CurrencyCode,
                o.Subtotal,
                o.TaxTotal,
                o.GrandTotal,
                o.CreatedByUserId.Value,
                o.CreatedAtUtc,
                o.OrderedAtUtc,
                o.ExpectedAtUtc,
                o.CancelledReason,
                o.ClosedReason,
                o.Lines
                    .OrderBy(l => l.LineNo)
                    .Select(l => new PurchaseOrderLineSummary(
                        l.Id.Value,
                        l.LineNo,
                        l.ProductId.Value,
                        l.UnitOfMeasureId.Value,
                        l.OrderedQuantity,
                        l.UnitCost,
                        l.LineTotal))
                    .ToList(),
                o.Approvals
                    .OrderBy(a => a.DecidedAtUtc)
                    .Select(a => new PurchaseApprovalSummary(
                        a.ApproverUserId.Value,
                        a.Decision.ToString(),
                        a.DecidedAtUtc,
                        a.Notes,
                        a.ThresholdApplied))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return detail is null
            ? ProblemDetailsMapping.ToProblem(
                Result.Failure(PurchasingErrors.OrderUnknown(new PurchaseOrderId(id))),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(detail);
    }

    private static async Task<IResult> SubmitPurchaseOrderAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new SubmitPurchaseOrderCommand(new PurchaseOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ApprovePurchaseOrderAsync(
        Guid id,
        [FromBody] PurchaseOrderDecisionBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ApprovePurchaseOrderCommand(new PurchaseOrderId(id), body?.Notes),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> RejectPurchaseOrderAsync(
        Guid id,
        [FromBody] PurchaseOrderDecisionBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new RejectPurchaseOrderCommand(new PurchaseOrderId(id), body?.Notes),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> SendPurchaseOrderAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new SendPurchaseOrderCommand(new PurchaseOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> CancelPurchaseOrderAsync(
        Guid id,
        [FromBody] PurchaseOrderCancellationBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new CancelPurchaseOrderCommand(new PurchaseOrderId(id), body?.Reason),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> WithdrawPurchaseOrderAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new WithdrawPurchaseOrderCommand(new PurchaseOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ClosePurchaseOrderAsync(
        Guid id,
        [FromBody] PurchaseOrderCancellationBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ClosePurchaseOrderCommand(new PurchaseOrderId(id), body?.Reason),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DispatchAsync(
        ICommand<PurchaseOrderId> command,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<PurchaseOrderId> result = await dispatcher
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> CreateGoodsReceiptAsync(
        Guid id,
        [FromBody] CreateGoodsReceiptBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<GoodsReceiptId> result = await dispatcher
            .SendAsync(
                new CreateGoodsReceiptCommand(
                    new PurchaseOrderId(id),
                    [.. body.Lines.Select(l => new GoodsReceiptLineSpec(
                        new PurchaseOrderLineId(l.PurchaseOrderLineId),
                        l.QuantityReceived,
                        l.QuantityDamaged,
                        l.QuantityWrongItem,
                        l.QuantityExpired,
                        l.UnitCost,
                        l.LotNumber,
                        l.ManufacturedOn,
                        l.ExpiresOn))],
                    body.DocumentsMissing),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListGoodsReceiptsAsync(
        PosDbContext context,
        Guid id,
        CancellationToken cancellationToken)
    {
        List<GoodsReceiptSummary> receipts = await context.GoodsReceipts
            .AsNoTracking()
            .Where(r => r.PurchaseOrderId == new PurchaseOrderId(id))
            .OrderByDescending(r => r.ReceivedAtUtc)
            .Select(r => new GoodsReceiptSummary(
                r.Id.Value,
                r.Number,
                r.Status.ToString(),
                r.PurchaseOrderId.Value,
                r.SupplierId.Value,
                r.DestinationLocationId.Value,
                r.DocumentsMissing,
                r.BusinessDate,
                r.ReceivedAtUtc,
                r.ReceivedByUserId.Value,
                r.CostVariancePendingApproval,
                r.CostVarianceValueAtStake))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(receipts);
    }

    private static async Task<IResult> GetGoodsReceiptAsync(
        PosDbContext context,
        Guid id,
        Guid receiptId,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        GoodsReceiptDetail? detail = await context.GoodsReceipts
            .AsNoTracking()
            .Where(r => r.PurchaseOrderId == new PurchaseOrderId(id) && r.Id == new GoodsReceiptId(receiptId))
            .Select(r => new GoodsReceiptDetail(
                r.Id.Value,
                r.Number,
                r.Status.ToString(),
                r.PurchaseOrderId.Value,
                r.SupplierId.Value,
                r.DestinationLocationId.Value,
                r.DocumentsMissing,
                r.BusinessDate,
                r.ReceivedAtUtc,
                r.ReceivedByUserId.Value,
                r.CostVariancePendingApproval,
                r.CostVarianceValueAtStake,
                r.Lines
                    .OrderBy(l => l.LineNo)
                    .Select(l => new GoodsReceiptLineSummary(
                        l.LineNo,
                        l.PurchaseOrderLineId.Value,
                        l.ProductId.Value,
                        l.QuantityExpected,
                        l.QuantityReceived,
                        l.QuantityDamaged,
                        l.QuantityWrongItem,
                        l.QuantityExpired,
                        l.OverageBeyondTolerance,
                        l.QuantityAccepted,
                        l.AcceptedState.ToString(),
                        l.UnitCost,
                        l.LotNumber,
                        l.ManufacturedOn,
                        l.ExpiresOn,
                        l.CostVariancePercent,
                        l.CostVarianceApprovedByUserId != null ? l.CostVarianceApprovedByUserId.Value.Value : null,
                        l.CostVarianceApprovedAtUtc))
                    .ToList(),
                r.Discrepancies
                    .OrderBy(d => d.LineNo)
                    .ThenBy(d => d.Kind)
                    .Select(d => new ReceivingDiscrepancySummary(
                        d.Id.Value,
                        d.PurchaseOrderLineId.Value,
                        d.LineNo,
                        d.Kind.ToString(),
                        d.Quantity,
                        d.ValueImpact,
                        d.ResolutionOutcome != null ? d.ResolutionOutcome.ToString() : null,
                        d.ResolutionNote,
                        d.ResolvedByUserId != null ? d.ResolvedByUserId.Value.Value : null,
                        d.ResolvedAtUtc))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return detail is null
            ? ProblemDetailsMapping.ToProblem(
                Result.Failure(PurchasingErrors.ReceiptUnknown(new GoodsReceiptId(receiptId))),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(detail);
    }

    private static async Task<IResult> ResolveReceivingDiscrepancyAsync(
        Guid id,
        [FromBody] ResolveReceivingDiscrepancyBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<GoodsReceiptId> result = await dispatcher
            .SendAsync(
                new ResolveReceivingDiscrepancyCommand(
                    new ReceivingDiscrepancyId(id),
                    body.Outcome,
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}