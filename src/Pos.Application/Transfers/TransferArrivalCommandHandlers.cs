using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>
/// Handles <see cref="ReceiveTransferCommand"/>. Allocates the TRC number,
/// moves the transfer to Received or PartiallyReceived, and posts the arrival
/// ledger group that clears the whole dispatch out of in-transit and splits it
/// into available, damaged and transit-variance stock at the destination.
/// </summary>
/// <remarks>
/// Everything happens inside the unit-of-work transaction the pipeline opens:
/// the TRC counter allocation, the ledger staging and the tracked mutations
/// either all commit or none do, so a receipt that fails validation never burns
/// a sequence value and a failed posting never leaves a half-receipt.
/// </remarks>
public sealed class ReceiveTransferCommandHandler(
    ITransferRepository transfers,
    IInventoryLedger ledger,
    IDocumentNumberGenerator numbers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ReceiveTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        ReceiveTransferCommand command,
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

        TransferLocationInfo? destination = await transfers
            .GetLocationInfoAsync(transfer.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (destination is null)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationLocationUnknown(transfer.DestinationLocationId));
        }

        if (destination.TimeZoneId is null)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationTimeZoneMissing(transfer.DestinationLocationId));
        }

        UserId receiver = currentUser.UserId ?? UserId.Empty;

        // The number is allocated only after the location checks, so an invalid
        // receipt cannot consume the sequence. Receive advances the state, after
        // which the receipt identifier and number are fixed.
        DocumentNumber receiptNumber = await numbers
            .NextAsync(DocumentType.TransferReceipt, cancellationToken)
            .ConfigureAwait(false);

        Result received = transfer.Receive(
            receiver,
            clock.UtcNow,
            TransferReceiptId.New(),
            receiptNumber.Value,
            command.Receives);

        if (received.IsFailure)
        {
            return Result<TransferOrderId>.Failure(received.Errors);
        }

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<ProductId, Product> productById = (await transfers
            .GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false)).ToDictionary(p => p.Id);

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.TransferReceipt,
            ReferenceDocumentType: ReferenceDocumentType.TransferReceipt,
            ReferenceDocumentId: transfer.ReceiptId!.Value.Value,
            ReferenceNumber: transfer.ReceiptNumber!,
            Legs: BuildArrivalLegs(transfer, destination, productById),
            Actor: new LedgerActor(
                receiver,
                null,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(destination.TimeZoneId));

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(posted.Errors);
    }

    /// <summary>
    /// Builds the balanced arrival legs per allocation that was receipted: the
    /// whole picked quantity leaves in-transit at the source, and the destination
    /// receives the good quantity into available, the damaged quantity into
    /// damaged, and the shortfall into transit variance.
    /// </summary>
    private static List<MovementLegSpec> BuildArrivalLegs(
        Transfer transfer,
        TransferLocationInfo destination,
        Dictionary<ProductId, Product> productById)
    {
        List<MovementLegSpec> legs = [];

        foreach (TransferLine line in transfer.Lines)
        {
            Product product = productById[line.ProductId];

            foreach (TransferPickAllocation allocation in transfer.Allocations.Where(a => a.LineNo == line.LineNo))
            {
                if (allocation.ReceivedQuantity == 0m && allocation.DamagedQuantity == 0m)
                {
                    continue;
                }

                legs.Add(new MovementLegSpec(
                    line.ProductId,
                    allocation.BatchId,
                    transfer.SourceLocationId,
                    destination.Kind,
                    InventoryState.InTransit,
                    -allocation.Quantity,
                    allocation.UnitCost,
                    product.TracksBatches));

                if (allocation.ReceivedQuantity > 0m)
                {
                    legs.Add(new MovementLegSpec(
                        line.ProductId,
                        allocation.BatchId,
                        transfer.DestinationLocationId,
                        destination.Kind,
                        InventoryState.Available,
                        allocation.ReceivedQuantity,
                        allocation.UnitCost,
                        product.TracksBatches));
                }

                if (allocation.DamagedQuantity > 0m)
                {
                    legs.Add(new MovementLegSpec(
                        line.ProductId,
                        allocation.BatchId,
                        transfer.DestinationLocationId,
                        destination.Kind,
                        InventoryState.Damaged,
                        allocation.DamagedQuantity,
                        allocation.UnitCost,
                        product.TracksBatches));
                }

                decimal shortfall = allocation.Shortfall;

                if (shortfall > 0m)
                {
                    legs.Add(new MovementLegSpec(
                        line.ProductId,
                        allocation.BatchId,
                        transfer.DestinationLocationId,
                        destination.Kind,
                        InventoryState.TransitVariance,
                        shortfall,
                        allocation.UnitCost,
                        product.TracksBatches));
                }
            }
        }

        return legs;
    }
}

/// <summary>
/// Handles <see cref="ResolveTransferDiscrepancyCommand"/>. Either returns the
/// short stock to available at the destination or writes it off to the external
/// counterparty; both post a balanced, approved, reason-bearing ledger group.
/// </summary>
public sealed class ResolveTransferDiscrepancyCommandHandler(
    ITransferRepository transfers,
    IInventoryLedger ledger,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<ResolveTransferDiscrepancyCommand, TransferOrderId>
{
    private LocationId? externalLocationId;

    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        ResolveTransferDiscrepancyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Transfer? transfer = await transfers
            .GetByDiscrepancyAsync(command.DiscrepancyId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.DiscrepancyUnknown(command.DiscrepancyId));
        }

        TransferDiscrepancy discrepancy = transfer
            .Discrepancies
            .First(d => d.Id == command.DiscrepancyId);

        if (command.Outcome == TransferDiscrepancyResolutionOutcome.WriteOff)
        {
            LocationId? externalWriteOffId = await transfers
                .GetExternalWriteOffLocationIdAsync(cancellationToken)
                .ConfigureAwait(false);

            if (externalWriteOffId is null)
            {
                return Result<TransferOrderId>.Failure(TransferErrors.WriteOffExternalLocationMissing);
            }

            externalLocationId = externalWriteOffId;
        }

        TransferLocationInfo? destination = await transfers
            .GetLocationInfoAsync(transfer.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        if (destination is null)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationLocationUnknown(transfer.DestinationLocationId));
        }

        if (destination.TimeZoneId is null)
        {
            return Result<TransferOrderId>.Failure(
                TransferErrors.DestinationTimeZoneMissing(transfer.DestinationLocationId));
        }

        UserId resolver = currentUser.UserId ?? UserId.Empty;

        Result resolved = transfer.ResolveDiscrepancy(
            command.DiscrepancyId,
            command.Outcome,
            resolver,
            clock.UtcNow,
            command.Note);

        if (resolved.IsFailure)
        {
            return Result<TransferOrderId>.Failure(resolved.Errors);
        }

        ProductId productId = transfer.Lines.First(l => l.LineNo == discrepancy.LineNo).ProductId;

        Product? product = (await transfers
            .GetProductsAsync([productId], cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault(p => p.Id == productId);

        if (product is null)
        {
            return Result<TransferOrderId>.Failure(TransferErrors.ProductUnknown(productId));
        }

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: command.Outcome == TransferDiscrepancyResolutionOutcome.Found
                ? InventoryMovementType.TransitVarianceResolveFound
                : InventoryMovementType.TransitVarianceWriteOff,
            ReferenceDocumentType: ReferenceDocumentType.TransferOrder,
            ReferenceDocumentId: transfer.Id.Value,
            ReferenceNumber: transfer.Number,
            Legs: BuildResolutionLegs(transfer, discrepancy, product, destination, command.Outcome, externalLocationId),
            Actor: new LedgerActor(
                resolver,
                resolver,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: clock.UtcNow,
            BusinessDate: clock.BusinessDateFor(destination.TimeZoneId),
            ReasonCode: command.Outcome == TransferDiscrepancyResolutionOutcome.Found
                ? AdjustmentReasonCode.TransitVarianceFound
                : AdjustmentReasonCode.TransitVarianceWriteOff,
            Notes: command.Note);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        return posted.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(posted.Errors);
    }

    /// <summary>
    /// Builds the balanced resolution legs: the transit-variance bucket at the
    /// destination gives up the short quantity, and it returns to available at
    /// the destination (found) or moves to the EXT-WRITEOFF counterparty.
    /// </summary>
    private static List<MovementLegSpec> BuildResolutionLegs(
        Transfer transfer,
        TransferDiscrepancy discrepancy,
        Product product,
        TransferLocationInfo destination,
        TransferDiscrepancyResolutionOutcome outcome,
        LocationId? externalLocationId)
    {
        // The resolution carries the dispatch's snapshot cost: the allocation
        // that went short was valued at pick time, and that value must ride
        // through the variance bucket unchanged.
        TransferPickAllocation allocation = transfer
            .Allocations
            .First(a =>
                a.LineNo == discrepancy.LineNo
                && (a.BatchId is null ? BatchId.Empty : a.BatchId.Value)
                    == (discrepancy.BatchId is null ? BatchId.Empty : discrepancy.BatchId.Value));

        // Write-offs leave the destination and land on the external write-off
        // counterparty; found stock returns to the destination's available.
        // The negative leg always lives at the destination, whose transit
        // variance bucket holds the short stock.
        bool writeOff = outcome == TransferDiscrepancyResolutionOutcome.WriteOff;

        return
        [
            new MovementLegSpec(
                product.Id,
                discrepancy.BatchId,
                transfer.DestinationLocationId,
                destination.Kind,
                InventoryState.TransitVariance,
                -discrepancy.Quantity,
                allocation.UnitCost,
                product.TracksBatches),
            new MovementLegSpec(
                product.Id,
                discrepancy.BatchId,
                writeOff ? externalLocationId!.Value : transfer.DestinationLocationId,
                writeOff ? LocationKind.External : destination.Kind,
                writeOff ? InventoryState.External : InventoryState.Available,
                +discrepancy.Quantity,
                allocation.UnitCost,
                product.TracksBatches),
        ];
    }
}

/// <summary>Handles <see cref="VerifyTransferCommand"/>. Verification is a state
/// transition only; the ledger buckets are already correct after the arrival and
/// resolution groups.</summary>
public sealed class VerifyTransferCommandHandler(
    ITransferRepository transfers,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<VerifyTransferCommand, TransferOrderId>
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> HandleAsync(
        VerifyTransferCommand command,
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

        Result verified = transfer.Verify(currentUser.UserId ?? UserId.Empty, clock.UtcNow);

        return verified.IsSuccess
            ? Result<TransferOrderId>.Success(transfer.Id)
            : Result<TransferOrderId>.Failure(verified.Errors);
    }
}