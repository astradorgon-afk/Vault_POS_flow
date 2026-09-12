using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>
/// Handles <see cref="PickTransferCommand"/>. Validates the requested
/// allocations against the available buckets at the source: batch-tracked
/// products must name real lots of this product, no allocation may exceed the
/// stock its lot actually has, and the picked lots must form a prefix of
/// first-expiry-first-out order. Unit costs are snapshotted here from master
/// data, never accepted from the caller.
/// </summary>
public sealed class PickTransferCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<PickTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        PickTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<ProductId, Product> productById = (await transfers
            .GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        foreach (ProductId productId in productIds)
        {
            if (!productById.ContainsKey(productId))
            {
                return Result<TransferOrderId>.Failure(TransferErrors.ProductUnknown(productId));
            }
        }

        BatchId[] namedBatches = [.. command.Allocations
            .Where(a => a.BatchId is not null)
            .Select(a => a.BatchId!.Value)
            .Distinct()];

        Dictionary<BatchId, Batch> batchById = (await transfers
            .GetBatchesAsync(namedBatches, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(b => b.Id);

        IReadOnlyList<PickableStockItem> buckets = await transfers
            .GetPickableStockAsync(transfer.SourceLocationId, productIds, cancellationToken)
            .ConfigureAwait(false);

        List<Error> validationErrors = EnforcePickability(transfer, command, productById, batchById, buckets);

        if (validationErrors.Count > 0)
        {
            return Result<TransferOrderId>.Failure(validationErrors);
        }

        List<TransferPickAllocationSpec> specs = [.. command.Allocations.Select(a => new TransferPickAllocationSpec(
            a.LineNo,
            a.BatchId,
            a.Quantity,
            UnitCostFor(transfer, a, productById, batchById)))];

        Result picked = transfer.Pick(currentUser.UserId ?? UserId.Empty, clock.UtcNow, specs);

        return picked.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(picked.Errors);
    }

    /// <summary>
    /// Validates that the requested allocations are physically possible and
    /// first-expiry-first-out: every named batch belongs to its line's product
    /// and carries enough available stock, and together the allocations match
    /// the canonical FEFO allocation for the requested total.
    /// </summary>
    private static List<Error> EnforcePickability(
        Transfer transfer,
        PickTransferCommand command,
        Dictionary<ProductId, Product> productById,
        Dictionary<BatchId, Batch> batchById,
        IReadOnlyList<PickableStockItem> buckets)
    {
        List<Error> errors = [];

        Dictionary<BatchId, PickableStockItem> pickableByBatch = buckets.ToDictionary(b => b.BatchKey);

        foreach (IGrouping<int, TransferPickRequestItem> group in command.Allocations.GroupBy(a => a.LineNo))
        {
            int lineNo = group.Key;
            TransferLine? line = transfer.Lines.FirstOrDefault(l => l.LineNo == lineNo);

            if (line is null || !productById.TryGetValue(line.ProductId, out Product? product))
            {
                continue;
            }

            decimal totalRequested = group.Sum(a => a.Quantity);

            foreach (TransferPickRequestItem allocation in group)
            {
                BatchId batchKey = allocation.BatchId is null ? BatchId.Empty : allocation.BatchId.Value;

                if (!product.TracksBatches && !batchKey.IsEmpty)
                {
                    errors.Add(TransferErrors.BatchNotAllowed(lineNo));
                    continue;
                }

                if (product.TracksBatches && batchKey.IsEmpty)
                {
                    errors.Add(TransferErrors.BatchRequired(lineNo));
                    continue;
                }

                if (product.TracksBatches)
                {
                    if (!batchById.TryGetValue(batchKey, out Batch? batch))
                    {
                        errors.Add(TransferErrors.StockUnavailable(lineNo, allocation.Quantity));
                        continue;
                    }

                    if (batch.ProductId != product.Id)
                    {
                        errors.Add(TransferErrors.BatchProductMismatch(lineNo));
                        continue;
                    }

                    if (!pickableByBatch.TryGetValue(batchKey, out PickableStockItem? bucket)
                        || bucket.AvailableQuantity < allocation.Quantity)
                    {
                        errors.Add(TransferErrors.StockUnavailable(lineNo, allocation.Quantity));
                    }
                }
                else if (!pickableByBatch.TryGetValue(BatchId.Empty, out PickableStockItem? bucket)
                         || bucket.AvailableQuantity < allocation.Quantity)
                {
                    errors.Add(TransferErrors.StockUnavailable(lineNo, allocation.Quantity));
                }
            }

            if (!product.TracksBatches)
            {
                continue;
            }

            // First-expiry-first-out: the canonical FEFO allocation fills
            // earlier-expiring lots completely before touching later ones. Any
            // deviation means an earlier lot was skipped.
            List<PickableStockItem> productBuckets = buckets
                .Where(b => b.ProductId == line.ProductId)
                .OrderBy(b => b.ExpiresOn ?? DateOnly.MaxValue)
                .ThenBy(b => b.BatchKey.Value)
                .ToList();

            decimal totalAvailable = productBuckets.Sum(b => b.AvailableQuantity);

            if (totalRequested > totalAvailable)
            {
                errors.Add(TransferErrors.StockUnavailable(lineNo, totalRequested));
                continue;
            }

            decimal remaining = totalRequested;
            Dictionary<BatchId, decimal> greedy = [];

            foreach (PickableStockItem bucket in productBuckets)
            {
                decimal take = Math.Min(remaining, bucket.AvailableQuantity);
                greedy[bucket.BatchKey] = take;
                remaining -= take;

                if (remaining == 0m)
                {
                    break;
                }
            }

            bool matchesGreedy = group.All(a =>
            {
                BatchId batchKey = a.BatchId is null ? BatchId.Empty : a.BatchId.Value;
                return greedy.TryGetValue(batchKey, out decimal canonical) && a.Quantity == canonical;
            });

            if (!matchesGreedy)
            {
                errors.Add(TransferErrors.PickSkipsEarlierExpiry(lineNo));
            }
        }

        return errors;
    }

    /// <summary>Snapshots the unit cost from master data: the lot cost, or the product default cost.</summary>
    private static decimal UnitCostFor(
        Transfer transfer,
        TransferPickRequestItem allocation,
        Dictionary<ProductId, Product> productById,
        Dictionary<BatchId, Batch> batchById)
    {
        TransferLine line = transfer.Lines.First(l => l.LineNo == allocation.LineNo);
        Product product = productById[line.ProductId];

        if (product.TracksBatches && allocation.BatchId is { } batchId)
        {
            return batchById[batchId].UnitCost;
        }

        return product.DefaultPurchaseCost;
    }
}

/// <summary>Handles <see cref="ReadyTransferCommand"/>.</summary>
public sealed class ReadyTransferCommandHandler(
    ITransferRepository transfers,
    ISystemClock clock) : ICommandHandler<ReadyTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        ReadyTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        Result ready = transfer.Ready(clock.UtcNow);

        return ready.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(ready.Errors);
    }
}

/// <summary>
/// Handles <see cref="DispatchTransferCommand"/>. Allocates the TRF and SHP
/// numbers, moves the aggregate to Dispatched and posts the ledger group that
/// pulls the stock from available into in-transit at the source location. A
/// failed post rolls the whole transaction back, including the counter rows, so
/// a failed dispatch can never burn a sequence.
/// </summary>
public sealed class DispatchTransferCommandHandler(
    ITransferRepository transfers,
    IInventoryLedger ledger,
    IDocumentNumberGenerator numbers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<DispatchTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        DispatchTransferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        TransferLocationInfo? source = await transfers
            .GetLocationInfoAsync(transfer.SourceLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceLocationUnknown(transfer.SourceLocationId));
        }

        if (source.TimeZoneId is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceTimeZoneMissing(transfer.SourceLocationId));
        }

        UserId dispatcher = currentUser.UserId ?? UserId.Empty;

        DocumentNumber number = await numbers
            .NextAsync(DocumentType.TransferOrder, cancellationToken)
            .ConfigureAwait(false);

        DocumentNumber shipmentNumber = await numbers
            .NextAsync(DocumentType.TransferShipment, cancellationToken)
            .ConfigureAwait(false);

        Result dispatched = transfer.Dispatch(
            clock.UtcNow,
            number,
            TransferShipmentId.New(),
            shipmentNumber.Value,
            dispatcher);

        if (dispatched.IsFailure)
        {
            return Result<TransferOrderId>.Failure(dispatched.Errors);
        }

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<ProductId, Product> productById = (await transfers
            .GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.TransferDispatch,
            ReferenceDocumentType: ReferenceDocumentType.TransferShipment,
            ReferenceDocumentId: transfer.ShipmentId!.Value.Value,
            ReferenceNumber: transfer.ShipmentNumber!,
            Legs: BuildDispatchLegs(transfer, source, productById),
            Actor: new LedgerActor(
                dispatcher,
                null,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(source.TimeZoneId));

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(posted.Errors);
    }

    /// <summary>
    /// Builds the dispatch legs per allocation: the negative available leg and
    /// the positive in-transit leg, both at the source location.
    /// </summary>
    private static List<MovementLegSpec> BuildDispatchLegs(
        Transfer transfer,
        TransferLocationInfo source,
        Dictionary<ProductId, Product> productById)
    {
        List<MovementLegSpec> legs = [];

        foreach (TransferLine line in transfer.Lines)
        {
            Product product = productById[line.ProductId];

            foreach (TransferPickAllocation allocation in transfer.Allocations.Where(a => a.LineNo == line.LineNo))
            {
                legs.Add(new MovementLegSpec(
                    line.ProductId,
                    allocation.BatchId,
                    transfer.SourceLocationId,
                    source.Kind,
                    InventoryState.Available,
                    -allocation.Quantity,
                    allocation.UnitCost,
                    product.TracksBatches));

                legs.Add(new MovementLegSpec(
                    line.ProductId,
                    allocation.BatchId,
                    transfer.SourceLocationId,
                    source.Kind,
                    InventoryState.InTransit,
                    +allocation.Quantity,
                    allocation.UnitCost,
                    product.TracksBatches));
            }
        }

        return legs;
    }
}

/// <summary>
/// Handles <see cref="CancelTransferDispatchCommand"/>. Undoing a dispatch posts
/// an approved, reason-bearing movement, so the handler additionally requires
/// transfer.approve before the aggregate moves to Cancelled and the reverse
/// ledger group is posted.
/// </summary>
public sealed class CancelTransferDispatchCommandHandler(
    ITransferRepository transfers,
    IInventoryLedger ledger,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CancelTransferDispatchCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        CancelTransferDispatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByIdAsync(command.TransferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.TransferUnknown(command.TransferId));
        }

        UserId canceller = currentUser.UserId ?? UserId.Empty;

        bool hasApproval = await permissions
            .HasPermissionAsync(
                canceller,
                Permissions.Transfer.Approve,
                transfer.SourceLocationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!hasApproval)
        {
            return Result<TransferOrderId>.Failure(Error.Forbidden(
                "auth.permission_denied",
                "You do not have permission to perform this action."));
        }

        TransferLocationInfo? source = await transfers
            .GetLocationInfoAsync(transfer.SourceLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceLocationUnknown(transfer.SourceLocationId));
        }

        if (source.TimeZoneId is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.SourceTimeZoneMissing(transfer.SourceLocationId));
        }

        Result cancelled = transfer.CancelDispatch(clock.UtcNow, command.Reason, canceller);

        if (cancelled.IsFailure)
        {
            return Result<TransferOrderId>.Failure(cancelled.Errors);
        }

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<ProductId, Product> productById = (await transfers
            .GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.TransferCancelDispatch,
            ReferenceDocumentType: ReferenceDocumentType.TransferShipment,
            ReferenceDocumentId: transfer.ShipmentId!.Value.Value,
            ReferenceNumber: transfer.ShipmentNumber!,
            Legs: BuildCancellationLegs(transfer, source, productById),
            Actor: new LedgerActor(
                canceller,
                canceller,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(source.TimeZoneId),
            ReasonCode: AdjustmentReasonCode.TransferCancelled,
            Notes: command.Reason);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(posted.Errors);
    }

    /// <summary>
    /// Builds the cancellation legs per allocation: the negative in-transit leg
    /// and the positive available leg, both at the source location.
    /// </summary>
    private static List<MovementLegSpec> BuildCancellationLegs(
        Transfer transfer,
        TransferLocationInfo source,
        Dictionary<ProductId, Product> productById)
    {
        List<MovementLegSpec> legs = [];

        foreach (TransferLine line in transfer.Lines)
        {
            Product product = productById[line.ProductId];

            foreach (TransferPickAllocation allocation in transfer.Allocations.Where(a => a.LineNo == line.LineNo))
            {
                legs.Add(new MovementLegSpec(
                    line.ProductId,
                    allocation.BatchId,
                    transfer.SourceLocationId,
                    source.Kind,
                    InventoryState.InTransit,
                    -allocation.Quantity,
                    allocation.UnitCost,
                    product.TracksBatches));

                legs.Add(new MovementLegSpec(
                    line.ProductId,
                    allocation.BatchId,
                    transfer.SourceLocationId,
                    source.Kind,
                    InventoryState.Available,
                    +allocation.Quantity,
                    allocation.UnitCost,
                    product.TracksBatches));
            }
        }

        return legs;
    }
}