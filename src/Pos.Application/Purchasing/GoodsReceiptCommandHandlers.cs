using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// Handles <see cref="CreateGoodsReceiptCommand"/>. Receiving plans the
/// dispositions in the domain, checks cost-variance approval authority, posts
/// the ledger group, and reconciles the order's received quantities.
/// </summary>
/// <remarks>
/// <para>
/// Everything happens inside the unit-of-work transaction the pipeline opens:
/// the GRN counter allocation, the ledger staging and the tracked mutations
/// either all commit or none do, so a receipt that fails validation never
/// burns a sequence value and a failed posting never leaves a half-receipt.
/// </para>
/// </remarks>
public sealed class CreateGoodsReceiptCommandHandler(
    IPurchaseOrderRepository orders,
    IInventoryLedger ledger,
    IDocumentNumberGenerator numbers,
    IApprovalGate approvalGate,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISystemClock clock) : ICommandHandler<CreateGoodsReceiptCommand, GoodsReceiptId>
{
    /// <inheritdoc />
    public async Task<Result<GoodsReceiptId>> HandleAsync(
        CreateGoodsReceiptCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Lines is null || command.Lines.Count == 0)
        {
            return Result<GoodsReceiptId>.Failure(PurchasingErrors.NothingReceived);
        }

        // The product of a receipt line is only known once the order is loaded,
        // so the repository resolves product identifiers itself; here we only
        // pass which lines carry a lot to look up.
        (PurchaseOrderLineId Line, string? Lot)[] requestedLots = [.. command.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.LotNumber))
            .Select(l => (l.PurchaseOrderLineId, l.LotNumber))];

        PurchaseReceivingContext? context = await orders
            .GetReceivingContextAsync(command.PurchaseOrderId, requestedLots, cancellationToken)
            .ConfigureAwait(false);

        if (context is null)
        {
            return Result<GoodsReceiptId>.Failure(PurchasingErrors.OrderUnknown(command.PurchaseOrderId));
        }

        PurchaseOrder order = context.Order;

        if (order.Status is not PurchaseOrderStatus.Ordered and not PurchaseOrderStatus.PartiallyReceived)
        {
            return Result<GoodsReceiptId>.Failure(PurchasingErrors.InvalidState(
                new PurchaseOrderStatus[]
                {
                    PurchaseOrderStatus.Ordered,
                    PurchaseOrderStatus.PartiallyReceived,
                },
                order.Status));
        }

        if (context.ExternalSupplierLocationId is null)
        {
            return Result<GoodsReceiptId>.Failure(PurchasingErrors.ExternalSupplierLocationMissing);
        }

        if (context.DestinationTimeZoneId is null)
        {
            return Result<GoodsReceiptId>.Failure(
                PurchasingErrors.ReceiptLocationTimeZoneMissing(order.DestinationLocationId));
        }

        ProductId[] productIds = [.. order.Lines.Select(l => l.ProductId).Distinct()];
        IReadOnlyList<Product> products = await orders
            .GetProductsForReceivingAsync(productIds, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, Product> productById = products.ToDictionary(p => p.Id);

        foreach (ProductId productId in productIds)
        {
            if (!productById.ContainsKey(productId))
            {
                return Result<GoodsReceiptId>.Failure(PurchasingErrors.ProductUnknown(productId));
            }
        }

        Dictionary<PurchaseOrderLineId, PurchaseOrderLineReceivingInfo> orderLines = [];
        Dictionary<ProductId, ProductReceivingTrackingInfo> tracking = [];

        foreach (PurchaseOrderLine line in order.Lines)
        {
            orderLines[line.Id] = new PurchaseOrderLineReceivingInfo(
                line.LineNo,
                line.ProductId,
                line.OrderedQuantity,
                line.UnitCost);
        }

        foreach (Product product in products)
        {
            tracking[product.Id] = new ProductReceivingTrackingInfo(product.TracksBatches, product.TracksExpiry);
        }

        UserId receiver = currentUser.UserId ?? UserId.Empty;

        Result<GoodsReceipt> planned = GoodsReceipt.Create(
            order.Id,
            order.SupplierId,
            order.DestinationLocationId,
            command.Lines,
            orderLines,
            context.CumulativeReceivedByLine,
            tracking,
            command.DocumentsMissing,
            clock.BusinessDateFor(context.DestinationTimeZoneId),
            receiver,
            clock.UtcNow);

        if (planned.IsFailure)
        {
            return Result<GoodsReceiptId>.Failure(planned.Errors);
        }

        GoodsReceipt receipt = planned.Value;

        // A cost variance needs purchase-approval authority. The approval gate
        // checks the value tier and self-approval, but the permission itself is
        // ours to verify: holding the receiving permission does not imply
        // approval authority over the money at stake.
        UserId? costVarianceApprover = null;

        if (receipt.CostVariancePendingApproval)
        {
            if (!await permissions
                    .HasPermissionAsync(receiver, Permissions.Purchasing.Approve, order.DestinationLocationId, cancellationToken)
                    .ConfigureAwait(false))
            {
                GoodsReceiptLine deviating = receipt.Lines.First(l => l.CostVarianceBeyondTolerance);
                PurchaseOrderLine orderLine = order.Lines.Single(l => l.Id == deviating.PurchaseOrderLineId);

                return Result<GoodsReceiptId>.Failure(PurchasingErrors.CostVarianceRequiresApproval(
                    deviating.LineNo,
                    orderLine.UnitCost,
                    deviating.UnitCost,
                    deviating.CostVariancePercent,
                    ReceivingPolicy.CostVarianceTolerancePercent));
            }

            Result gate = await approvalGate
                .RequireAsync(
                    Permissions.Purchasing.Approve,
                    receipt.CostVarianceValueAtStake,
                    receiver,
                    order.CreatedByUserId,
                    order.DestinationLocationId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (gate.IsFailure)
            {
                return Result<GoodsReceiptId>.Failure(gate.Errors);
            }

            Result granted = receipt.GrantCostVarianceApproval(receiver, clock.UtcNow);

            if (granted.IsFailure)
            {
                return Result<GoodsReceiptId>.Failure(granted.Errors);
            }

            costVarianceApprover = receiver;
        }

        // The number is allocated only now, after every domain and authority
        // check, so an invalid receipt cannot consume the sequence.
        DocumentNumber number = await numbers
            .NextAsync(DocumentType.GoodsReceipt, cancellationToken)
            .ConfigureAwait(false);

        Result assigned = receipt.AssignNumber(number);

        if (assigned.IsFailure)
        {
            return Result<GoodsReceiptId>.Failure(assigned.Errors);
        }

        // Materialise the batches for lots that have never been received, and
        // join every line to its batch so the ledger legs can name it.
        Dictionary<(ProductId Product, string Lot), BatchId> batchIds = new(context.ExistingBatches);
        List<Batch> newBatches = [];

        foreach (GoodsReceiptLine line in receipt.Lines)
        {
            if (line.LotNumber is not { Length: > 0 } lot)
            {
                continue;
            }

            (ProductId Product, string Lot) key = (line.ProductId, lot);

            if (batchIds.ContainsKey(key))
            {
                continue;
            }

            Result<Batch> created = Batch.Create(
                line.ProductId,
                order.SupplierId,
                lot,
                receipt.BusinessDate,
                line.ManufacturedOn,
                line.ExpiresOn,
                line.UnitCost,
                receiver,
                clock.UtcNow);

            if (created.IsFailure)
            {
                return Result<GoodsReceiptId>.Failure(created.Errors);
            }

            batchIds[key] = created.Value.Id;
            newBatches.Add(created.Value);
        }

        MovementGroupSpec group = new(
            EventId: EventId.New(),
            MovementType: InventoryMovementType.SupplierReceipt,
            ReferenceDocumentType: ReferenceDocumentType.GoodsReceipt,
            ReferenceDocumentId: receipt.Id.Value,
            ReferenceNumber: receipt.Number,
            Legs: BuildLegs(receipt, context, productById, batchIds, order.SupplierId),
            Actor: new LedgerActor(
                receiver,
                costVarianceApprover,
                currentUser.DeviceId,
                currentUser.CorrelationId),
            OccurredAtUtc: receipt.ReceivedAtUtc,
            BusinessDate: receipt.BusinessDate);

        Result<PostedMovementGroup> posted = await ledger.PostAsync(group, cancellationToken).ConfigureAwait(false);

        if (posted.IsFailure)
        {
            return Result<GoodsReceiptId>.Failure(posted.Errors);
        }

        Dictionary<PurchaseOrderLineId, decimal> cumulativeAfter = new(context.CumulativeReceivedByLine);

        foreach (GoodsReceiptLine line in receipt.Lines)
        {
            cumulativeAfter[line.PurchaseOrderLineId] =
                cumulativeAfter.GetValueOrDefault(line.PurchaseOrderLineId) + line.QuantityReceived;
        }

        Result reconciled = order.RecordReceipt(cumulativeAfter);

        if (reconciled.IsFailure)
        {
            return Result<GoodsReceiptId>.Failure(reconciled.Errors);
        }

        return await orders
            .AddReceiptAsync(receipt, newBatches, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the balanced movement legs of each receipt line: the negative
    /// EXT-SUPPLIER leg, the accepted quantity into the accepted state, and the
    /// refused and beyond-tolerance excess into Damaged or Quarantine. Also
    /// records the delivered cost on the product-supplier link.
    /// </summary>
    private static List<MovementLegSpec> BuildLegs(
        GoodsReceipt receipt,
        PurchaseReceivingContext context,
        Dictionary<ProductId, Product> productById,
        Dictionary<(ProductId Product, string Lot), BatchId> batchIds,
        SupplierId supplierId)
    {
        List<MovementLegSpec> legs = [];
        LocationId externalLocationId = context.ExternalSupplierLocationId!.Value;
        LocationId destinationLocationId = receipt.DestinationLocationId;
        LocationKind destinationKind = context.DestinationKind;

        foreach (GoodsReceiptLine line in receipt.Lines)
        {
            if (line.QuantityReceived == 0m)
            {
                continue;
            }

            Product product = productById[line.ProductId];
            BatchId? batchId = line.LotNumber is { Length: > 0 } lot
                ? batchIds[(line.ProductId, lot)]
                : null;

            legs.Add(new MovementLegSpec(
                line.ProductId,
                batchId,
                externalLocationId,
                LocationKind.External,
                InventoryState.External,
                -line.QuantityReceived,
                line.UnitCost,
                product.TracksBatches));

            if (line.QuantityAccepted > 0m)
            {
                legs.Add(new MovementLegSpec(
                    line.ProductId,
                    batchId,
                    destinationLocationId,
                    destinationKind,
                    line.AcceptedState,
                    line.QuantityAccepted,
                    line.UnitCost,
                    product.TracksBatches));
            }

            AddDispositionLeg(legs, line, destinationLocationId, destinationKind, product, batchId, line.QuantityDamaged, InventoryState.Damaged);
            AddDispositionLeg(legs, line, destinationLocationId, destinationKind, product, batchId, line.QuantityWrongItem, InventoryState.Quarantine);
            AddDispositionLeg(legs, line, destinationLocationId, destinationKind, product, batchId, line.QuantityExpired, InventoryState.Quarantine);
            AddDispositionLeg(legs, line, destinationLocationId, destinationKind, product, batchId, line.OverageBeyondTolerance, InventoryState.Quarantine);

            product.RecordSupplierReceipt(supplierId, line.UnitCost, line.QuantityReceived);
        }

        return legs;
    }

    /// <summary>Appends one destination leg for refused or excess quantity.</summary>
    private static void AddDispositionLeg(
        List<MovementLegSpec> legs,
        GoodsReceiptLine line,
        LocationId destinationLocationId,
        LocationKind destinationKind,
        Product product,
        BatchId? batchId,
        decimal quantity,
        InventoryState state)
    {
        if (quantity > 0m)
        {
            legs.Add(new MovementLegSpec(
                line.ProductId,
                batchId,
                destinationLocationId,
                destinationKind,
                state,
                quantity,
                line.UnitCost,
                product.TracksBatches));
        }
    }
}