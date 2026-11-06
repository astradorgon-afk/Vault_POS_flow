using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Handles <see cref="CreateSupplyRequestCommand"/>.</summary>
public sealed class CreateSupplyRequestCommandHandler(
    IPurchaseOrderRepository orders,
    IDocumentNumberGenerator numbers,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateSupplyRequestCommand, SupplyRequestId>
{
    /// <inheritdoc />
    public async Task<Result<SupplyRequestId>> HandleAsync(
        CreateSupplyRequestCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Only the warehouse buys. A store that needs stock asks the warehouse
        // for it, which is a transfer, not a purchase.
        LocationKind? kind = await orders
            .GetActiveLocationKindAsync(command.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (kind is not LocationKind.MainWarehouse)
        {
            return Result<SupplyRequestId>.Failure(PurchasingErrors.SupplyRequestDestinationInvalid);
        }

        foreach (SupplyRequestLineSpec spec in command.Lines ?? [])
        {
            if (await orders.GetActiveProductBaseUnitAsync(spec.ProductId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Result<SupplyRequestId>.Failure(PurchasingErrors.ProductUnknown(spec.ProductId));
            }
        }

        Result<SupplyRequest> created = SupplyRequest.Create(
            command.DestinationLocationId,
            command.Lines ?? [],
            command.Note,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow);

        if (created.IsFailure)
        {
            return Result<SupplyRequestId>.Failure(created.Errors);
        }

        // Unlike a purchase order, a request is numbered as it is raised: there
        // is no draft state in which it could be abandoned before anyone sees it.
        DocumentNumber number = await numbers
            .NextAsync(DocumentType.SupplyRequest, cancellationToken)
            .ConfigureAwait(false);
        created.Value.AssignNumber(number.Value);

        Result<SupplyRequestId> stored = await orders
            .AddSupplyRequestAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);

        if (stored.IsSuccess)
        {
            await SupplyRequestAuditing.WriteAsync(
                    audit, AuditActions.Purchasing.SupplyRequested, created.Value,
                    new
                    {
                        created.Value.Number,
                        Lines = created.Value.Lines.Count,
                        Units = created.Value.Lines.Sum(line => line.Quantity),
                    },
                    created.Value.Note, cancellationToken)
                .ConfigureAwait(false);
        }

        return stored;
    }
}

/// <summary>Handles <see cref="OrderSupplyRequestCommand"/>.</summary>
/// <remarks>
/// Creating the order and marking the request as ordered happen in one command
/// so they share a transaction. Splitting them would let an order exist that no
/// request knows about, which is exactly the trail this feature is for.
/// </remarks>
public sealed class OrderSupplyRequestCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<OrderSupplyRequestCommand, PurchaseOrderId>
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> HandleAsync(
        OrderSupplyRequestCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplyRequest? request = await orders
            .GetSupplyRequestByIdAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.SupplyRequestUnknown(command.RequestId));
        }

        if (command.Lines is not { Count: > 0 })
        {
            return Result<PurchaseOrderId>.Failure(PurchasingErrors.SupplyRequestEmpty);
        }

        List<PurchaseOrderLineSpec> orderLines = [];
        Dictionary<int, decimal> orderedByLine = [];

        foreach (SupplyRequestOrderLineSpec spec in command.Lines)
        {
            SupplyRequestLine? line = request.Lines.FirstOrDefault(l => l.LineNo == spec.LineNo);

            if (line is null)
            {
                return Result<PurchaseOrderId>.Failure(PurchasingErrors.SupplyRequestLineUnknown(spec.LineNo));
            }

            if (orderedByLine.ContainsKey(spec.LineNo))
            {
                return Result<PurchaseOrderId>.Failure(PurchasingErrors.SupplyRequestDuplicateLine(spec.LineNo));
            }

            orderedByLine[spec.LineNo] = spec.Quantity;
            orderLines.Add(new PurchaseOrderLineSpec(
                line.ProductId,
                spec.UnitOfMeasureId,
                spec.Quantity,
                spec.UnitCost));
        }

        // The goods are bought to the warehouse that asked for them; nobody gets
        // to redirect a delivery by raising an order against someone's request.
        Result<PurchaseOrder> draft = await PurchaseOrderDrafting
            .DraftAsync(
                orders,
                command.SupplierId,
                command.SupplierName,
                request.DestinationLocationId,
                orderLines,
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

        Result recorded = request.RecordPurchaseOrder(
            draft.Value.Id,
            orderedByLine,
            currentUser.UserId ?? UserId.Empty,
            clock.UtcNow);

        if (recorded.IsFailure)
        {
            return Result<PurchaseOrderId>.Failure(recorded.Errors);
        }

        Result<PurchaseOrderId> added = await orders.AddAsync(draft.Value, cancellationToken).ConfigureAwait(false);

        if (added.IsSuccess)
        {
            await SupplyRequestAuditing.WriteAsync(
                    audit, AuditActions.Purchasing.SupplyOrdered, request,
                    new
                    {
                        request.Number,
                        Status = request.Status.ToString(),
                        PurchaseOrderId = draft.Value.Id.Value,
                        Lines = orderLines.Count,
                        Units = orderedByLine.Values.Sum(),
                        draft.Value.GrandTotal,
                    },
                    reason: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return added;
    }
}

/// <summary>Handles <see cref="RejectSupplyRequestCommand"/>.</summary>
public sealed class RejectSupplyRequestCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<RejectSupplyRequestCommand, SupplyRequestId>
{
    /// <inheritdoc />
    public async Task<Result<SupplyRequestId>> HandleAsync(
        RejectSupplyRequestCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplyRequest? request = await orders
            .GetSupplyRequestByIdAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result<SupplyRequestId>.Failure(PurchasingErrors.SupplyRequestUnknown(command.RequestId));
        }

        Result rejected = request.Reject(command.Reason, currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        if (rejected.IsFailure)
        {
            return Result<SupplyRequestId>.Failure(rejected.Errors);
        }

        await SupplyRequestAuditing.WriteAsync(
                audit, AuditActions.Purchasing.SupplyClosed, request,
                new { request.Number, Status = request.Status.ToString() },
                command.Reason, cancellationToken)
            .ConfigureAwait(false);

        return Result<SupplyRequestId>.Success(request.Id);
    }
}

/// <summary>Handles <see cref="CancelSupplyRequestCommand"/>.</summary>
public sealed class CancelSupplyRequestCommandHandler(
    IPurchaseOrderRepository orders,
    IAuditWriter audit,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CancelSupplyRequestCommand, SupplyRequestId>
{
    /// <inheritdoc />
    public async Task<Result<SupplyRequestId>> HandleAsync(
        CancelSupplyRequestCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SupplyRequest? request = await orders
            .GetSupplyRequestByIdAsync(command.RequestId, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Result<SupplyRequestId>.Failure(PurchasingErrors.SupplyRequestUnknown(command.RequestId));
        }

        Result cancelled = request.Cancel(command.Reason, currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        if (cancelled.IsFailure)
        {
            return Result<SupplyRequestId>.Failure(cancelled.Errors);
        }

        await SupplyRequestAuditing.WriteAsync(
                audit, AuditActions.Purchasing.SupplyClosed, request,
                new { request.Number, Status = request.Status.ToString() },
                command.Reason, cancellationToken)
            .ConfigureAwait(false);

        return Result<SupplyRequestId>.Success(request.Id);
    }
}

/// <summary>Shared audit staging for the supply request handlers.</summary>
internal static class SupplyRequestAuditing
{
    /// <summary>Stages one entry against a supply request.</summary>
    /// <param name="audit">The writer.</param>
    /// <param name="action">The stable action code.</param>
    /// <param name="request">The request.</param>
    /// <param name="details">What changed.</param>
    /// <param name="reason">The reason given, where one was required.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the entry is staged.</returns>
    internal static Task WriteAsync(
        IAuditWriter audit,
        string action,
        SupplyRequest request,
        object? details,
        string? reason,
        CancellationToken cancellationToken)
        => PurchasingAudit.WriteAsync(
            audit,
            action,
            nameof(SupplyRequest),
            request.Id.Value,
            request.DestinationLocationId,
            details,
            reason,
            ReferenceDocumentType.PurchaseOrder,
            request.Id.Value,
            cancellationToken);
}
