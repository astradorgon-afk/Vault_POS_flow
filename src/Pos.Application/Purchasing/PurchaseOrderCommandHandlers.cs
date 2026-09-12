using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Handles <see cref="CreatePurchaseOrderCommand"/>.</summary>
public sealed class CreatePurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        CreatePurchaseOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Lines is null || command.Lines.Count == 0)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.EmptyOrder);
        }

        if (!await orders.IsActiveSupplierAsync(command.SupplierId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.SupplierUnknown(command.SupplierId));
        }

        if (!await orders.CanReceiveGoodsAsync(command.DestinationLocationId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.DestinationUnknown(command.DestinationLocationId));
        }

        // A line's unit must be the product's base unit for now. Buying in a
        // sold-by unit (boxes of twelve, say) is enabled once unit conversions
        // are wired into the purchase flow; until then ordering in a non-base
        // unit would silently mis-count the ledger.
        for (int i = 0; i < command.Lines.Count; i++)
        {
            PurchaseOrderLineSpec spec = command.Lines[i];
            int lineNo = i + 1;

            UnitOfMeasureId? baseUnit = await orders
                .GetActiveProductBaseUnitAsync(spec.ProductId, cancellationToken)
                .ConfigureAwait(false);

            if (baseUnit is null)
            {
                return Result<PurchaseOrderId>.Failure(PurchasingErrors.ProductUnknown(spec.ProductId));
            }

            if (baseUnit != spec.UnitOfMeasureId)
            {
                return Result<PurchaseOrderId>.Failure(PurchasingErrors.LineUnitMismatch(spec.ProductId, lineNo));
            }
        }

        Result<PurchaseOrder> created = PurchaseOrder.Create(
            command.SupplierId,
            command.DestinationLocationId,
            command.Lines,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow,
            command.CurrencyCode ?? "PHP",
            command.ExpectedAtUtc);

        if (created.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(created.Errors);
        }

        return await orders.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Handles <see cref="SubmitPurchaseOrderCommand"/>.</summary>
public sealed class SubmitPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IDocumentNumberGenerator numbers,
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

        // The unit of work flushes the tracked order at the end of the pipeline.
        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="ApprovePurchaseOrderCommand"/>.</summary>
public sealed class ApprovePurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
    IApprovalGate gate,
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

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="RejectPurchaseOrderCommand"/>.</summary>
public sealed class RejectPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
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

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="SendPurchaseOrderCommand"/>.</summary>
public sealed class SendPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
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

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="CancelPurchaseOrderCommand"/>.</summary>
public sealed class CancelPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
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

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="WithdrawPurchaseOrderCommand"/>.</summary>
public sealed class WithdrawPurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
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

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}

/// <summary>Handles <see cref="ClosePurchaseOrderCommand"/>.</summary>
public sealed class ClosePurchaseOrderCommandHandler(
    IPurchaseOrderRepository orders,
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

        return Result<PurchaseOrderId>.Success(order.Id);
    }
}