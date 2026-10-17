using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Handles <see cref="CreatePurchaseOrderCommand"/>.</summary>
public sealed class CreatePurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        CreatePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<PurchaseOrder> draft = await PurchaseOrderDrafting
            .DraftAsync(
                orders,
                command.SupplierId,
                command.SupplierName,
                command.DestinationLocationId,
                command.Lines,
                currentUser.UserId ?? UserId.Empty,
                clock.UtcNow,
                command.CurrencyCode,
                command.ExpectedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        if (draft.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(draft.Errors);
        }

        Result<PurchaseOrderId> added = await orders.AddAsync(draft.Value, cancellationToken).ConfigureAwait(false);

        if (added.IsSuccess)
        {
            await PurchasingAudit
                .OrderAsync(audit, AuditActions.Purchasing.OrderCreated, draft.Value,
                    PurchasingAudit.OrderFacts(draft.Value), reason: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return added;
    }
}

/// <summary>
/// Drafting a purchase order: resolving who is being bought from, checking the
/// destination can take delivery, and checking every line is priced in the
/// product's base unit.
/// </summary>
/// <remarks>
/// Shared because an order can start two ways — typed from scratch, or raised
/// against a warehouse supply request — and the rules about suppliers, units and
/// destinations are the same either way.
/// </remarks>
internal static class PurchaseOrderDrafting
{
    /// <summary>Builds a draft order, without persisting it.</summary>
    /// <param name="orders">The purchasing repository.</param>
    /// <param name="supplierId">The managed supplier, or empty to provision one.</param>
    /// <param name="supplierName">The typed name, used when no supplier is named.</param>
    /// <param name="destinationLocationId">Where the goods are to be delivered.</param>
    /// <param name="lines">The ordered lines.</param>
    /// <param name="createdBy">Who is raising the order.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="currencyCode">The currency, defaulting to PHP.</param>
    /// <param name="expectedAtUtc">The expected delivery instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft order, or validation errors.</returns>
    public static async Task<Result<PurchaseOrder>> DraftAsync(
        IPurchaseOrderRepository orders,
        SupplierId supplierId,
        string? supplierName,
        LocationId destinationLocationId,
        IReadOnlyList<PurchaseOrderLineSpec>? lines,
        UserId createdBy,
        DateTimeOffset now,
        string? currencyCode,
        DateTimeOffset? expectedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orders);

        if (lines is null || lines.Count == 0)
        {
            return Result<PurchaseOrder>.Failure(PurchasingErrors.EmptyOrder);
        }

        // The order references the managed supplier, or - when the site has no
        // record for the seller - provisions a lightweight supplier row from the
        // typed name so the order stays a normal order everywhere downstream.
        if (!supplierId.IsEmpty)
        {
            if (!await orders.IsActiveSupplierAsync(supplierId, cancellationToken).ConfigureAwait(false))
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.SupplierUnknown(supplierId));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(supplierName))
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.CustomSupplierNameRequired);
            }

            Result<Supplier> supplier = Supplier.Create(
                BuildCustomSupplierCode(), supplierName, null, 0, 0);

            if (supplier.IsFailure)
            {
                return Result<PurchaseOrder>.Failure(supplier.Errors);
            }

            Result<SupplierId> saved =
                await orders.CreateSupplierAsync(supplier.Value, cancellationToken).ConfigureAwait(false);

            if (saved.IsFailure)
            {
                return Result<PurchaseOrder>.Failure(saved.Errors);
            }

            supplierId = saved.Value;
        }

        if (!await orders.CanReceiveGoodsAsync(destinationLocationId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PurchaseOrder>.Failure(PurchasingErrors.DestinationUnknown(destinationLocationId));
        }

        // A line's unit must be the product's base unit for now. Buying in a
        // sold-by unit (boxes of twelve, say) is enabled once unit conversions
        // are wired into the purchase flow; until then ordering in a non-base
        // unit would silently mis-count the ledger.
        for (int i = 0; i < lines.Count; i++)
        {
            PurchaseOrderLineSpec spec = lines[i];
            int lineNo = i + 1;

            UnitOfMeasureId? baseUnit = await orders
                .GetActiveProductBaseUnitAsync(spec.ProductId, cancellationToken)
                .ConfigureAwait(false);

            if (baseUnit is null)
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.ProductUnknown(spec.ProductId));
            }

            if (baseUnit != spec.UnitOfMeasureId)
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.LineUnitMismatch(spec.ProductId, lineNo));
            }
        }

        return PurchaseOrder.Create(
            supplierId,
            destinationLocationId,
            lines,
            createdBy,
            now,
            currencyCode ?? "PHP",
            expectedAtUtc);
    }

    /// <summary>Builds a supremely unlikely-to-collide code for an on-the-fly
    /// supplier, staying under the sixteen-character supplier code limit.</summary>
    private static string BuildCustomSupplierCode()
        => FormattableString.Invariant($"C{Guid.NewGuid():N}")[..Supplier.CodeMaxLength];
}

/// <summary>
/// Hands a dead order's claim back to the warehouse supply request it was raised
/// against.
/// </summary>
/// <remarks>
/// An order can die three ways — cancelled, withdrawn or rejected — and in every
/// one of them the goods will never arrive. A request that stays closed over an
/// order like that is a need nobody can see any more, which matters most because
/// the owner's queue only lists open requests.
/// </remarks>
internal static class SupplyRequestRelease
{
    /// <summary>Releases the request behind an order, if the order came from one.</summary>
    /// <param name="orders">The purchasing repository.</param>
    /// <param name="purchaseOrderId">The order that fell through.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the request has been put back.</returns>
    public static async Task ForDeadOrderAsync(
        IPurchaseOrderRepository orders,
        PurchaseOrderId purchaseOrderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SupplyRequest? request = await orders
            .GetSupplyRequestForOrderAsync(purchaseOrderId, cancellationToken)
            .ConfigureAwait(false);

        // Most orders are typed in from scratch and belong to no request. That
        // is the ordinary case, not a failure.
        request?.ReleasePurchaseOrder(purchaseOrderId, now);
    }
}

/// <summary>Handles <see cref="SubmitPurchaseOrderCommand"/>.</summary>
public sealed class SubmitPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IDocumentNumberGenerator numbers,
    IAuditWriter audit,
    ISystemClock clock) : ICommandHandler<SubmitPurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        SubmitPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        // The number is allocated only when the order is still a draft, so a
        // stale or repeated submit can never burn a sequence value.
        if (order.Status != PurchaseOrderStatus.Draft)
        {
            return Result<PurchaseOrderId>.Failure(
                PurchasingErrors.InvalidState(PurchaseOrderStatus.Draft, order.Status));
        }

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.PurchaseOrder, cancellationToken)
            .ConfigureAwait(false);

        Result submitted = order.Submit(number, clock.UtcNow);

        if (submitted.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(submitted.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderSubmitted, order,
                PurchasingAudit.OrderFacts(order), reason: null, cancellationToken)
            .ConfigureAwait(false);

        // The unit of work flushes the tracked order at the end of the pipeline.
        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="ApprovePurchaseOrderCommand"/>.</summary>
public sealed class ApprovePurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IApprovalGate gate,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ApprovePurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        ApprovePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        UserId approver = currentUser.UserId ?? UserId.Empty;

        Result gateResult = await gate
            .RequireAsync(
                Permissions.Purchasing.Approve,
                order.GrandTotal,
                approver,
                order.CreatedByUserId,
                order.DestinationLocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (gateResult.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(gateResult.Errors);
        }

        Result approved = order.Approve(approver, clock.UtcNow, order.GrandTotal, command.Notes);

        if (approved.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(approved.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderApproved, order,
                PurchasingAudit.OrderFacts(order), command.Notes, cancellationToken)
            .ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="RejectPurchaseOrderCommand"/>.</summary>
public sealed class RejectPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RejectPurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        RejectPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        Result rejected = order.Reject(currentUser.UserId ?? UserId.Empty, clock.UtcNow, command.Notes);

        if (rejected.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(rejected.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderRejected, order,
                PurchasingAudit.OrderFacts(order), command.Notes, cancellationToken)
            .ConfigureAwait(false);

        await SupplyRequestRelease
            .ForDeadOrderAsync(orders, order.Id, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="SendPurchaseOrderCommand"/>.</summary>
public sealed class SendPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ISystemClock clock) : ICommandHandler<SendPurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        SendPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        Result sent = order.Send(clock.UtcNow);

        if (sent.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(sent.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderSent, order,
                PurchasingAudit.OrderFacts(order), null, cancellationToken)
            .ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="CancelPurchaseOrderCommand"/>.</summary>
public sealed class CancelPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CancelPurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        CancelPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        Result cancelled = order.Cancel(currentUser.UserId ?? UserId.Empty, command.Reason, clock.UtcNow);

        if (cancelled.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(cancelled.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderCancelled, order,
                PurchasingAudit.OrderFacts(order), command.Reason, cancellationToken)
            .ConfigureAwait(false);

        await SupplyRequestRelease
            .ForDeadOrderAsync(orders, order.Id, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="WithdrawPurchaseOrderCommand"/>.</summary>
public sealed class WithdrawPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<WithdrawPurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        WithdrawPurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        Result withdrawn = order.Withdraw(currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        if (withdrawn.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(withdrawn.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderCancelled, order,
                PurchasingAudit.OrderFacts(order), null, cancellationToken)
            .ConfigureAwait(false);

        await SupplyRequestRelease
            .ForDeadOrderAsync(orders, order.Id, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="ClosePurchaseOrderCommand"/>.</summary>
public sealed class ClosePurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ClosePurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        ClosePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        PurchaseOrder? order = await orders.GetByIdAsync(command.OrderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.OrderUnknown(command.OrderId));
        }

        Result closed = order.Close(currentUser.UserId ?? UserId.Empty, command.Reason, clock.UtcNow);

        if (closed.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(closed.Errors);
        }

        await PurchasingAudit
            .OrderAsync(audit, AuditActions.Purchasing.OrderClosed, order,
                PurchasingAudit.OrderFacts(order), command.Reason, cancellationToken)
            .ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}