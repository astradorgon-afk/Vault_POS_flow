using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Purchasing;

/// <summary>Lifecycle of a goods receipt. Persisted as <see cref="short"/>; values must never be renumbered.</summary>
public enum GoodsReceiptStatus
{
    /// <summary>Posted: the ledger has been updated and the receipt is live.</summary>
    Posted = 1,

    /// <summary>The receipt was created in error and voided.</summary>
    Voided = 2,

    /// <summary>The receipt was reversed by a reversing ledger event.</summary>
    Reversed = 3,
}

/// <summary>
/// The nature of a receiving discrepancy. The kind drives the follow-up: a
/// shortage is a credit owed, damaged goods are returned or written off, and
/// wrong, expired and beyond-tolerance goods are quarantined for an HQ decision.
/// Persisted as <see cref="short"/>; values must never be renumbered.
/// </summary>
public enum ReceivingDiscrepancyKind
{
    /// <summary>Goods ordered but never delivered.</summary>
    Shortage = 1,

    /// <summary>Goods delivered beyond the over-receipt tolerance; the excess is quarantined.</summary>
    Overage = 2,

    /// <summary>Goods delivered damaged.</summary>
    Damaged = 3,

    /// <summary>Goods delivered that were not the item ordered.</summary>
    WrongItem = 4,

    /// <summary>Goods past their expiry date on arrival.</summary>
    Expired = 5,

    /// <summary>The delivery arrived without the expected supporting documents.</summary>
    MissingDocuments = 6,
}

/// <summary>
/// The receiving policy knobs. Defaults stand in for settings that a later
/// phase will make configurable per location and category
/// (<c>AutoPassInspection</c>, <c>MinimumAcceptableShelfLifeDays</c>);
/// until then they are constants so the behaviour is documented in one place.
/// </summary>
public static class ReceivingPolicy
{
    /// <summary>
    /// Over-receipt tolerance as a percentage of the remaining expected
    /// quantity: receiving up to this much more than expected at a single
    /// delivery is accepted into PendingInspection; anything beyond the
    /// tolerance is quarantined.
    /// </summary>
    public const decimal OverageTolerancePercent = 5m;

    /// <summary>
    /// Cost-variance tolerance as a percentage of the purchase order unit
    /// cost. A receipt deviating by more than this requires purchase approval
    /// authority before it may post.
    /// </summary>
    public const decimal CostVarianceTolerancePercent = 5m;

    /// <summary>
    /// The state non-problem goods are received into. A future location/category
    /// setting (<c>AutoPassInspection</c>) will let trusted categories post
    /// straight to Available; until it exists every receipt goes to inspection.
    /// </summary>
    public static InventoryState DefaultAcceptedState => InventoryState.PendingInspection;
}

/// <summary>Input to create one goods receipt line.</summary>
/// <param name="PurchaseOrderLineId">The purchase order line being received against.</param>
/// <param name="QuantityReceived">The quantity physically counted on arrival.</param>
/// <param name="QuantityDamaged">Units counted but refused as damaged.</param>
/// <param name="QuantityWrongItem">Units counted but refused as the wrong item.</param>
/// <param name="QuantityExpired">Units counted but refused as expired on arrival.</param>
/// <param name="UnitCost">The actual purchase cost per unit for this delivery.</param>
/// <param name="LotNumber">The supplier's lot number, required for batch-tracked products.</param>
/// <param name="ManufacturedOn">The manufacture date, where recorded.</param>
/// <param name="ExpiresOn">The expiry date, required for expiry-tracked products.</param>
public sealed record GoodsReceiptLineSpec(
    PurchaseOrderLineId PurchaseOrderLineId,
    decimal QuantityReceived,
    decimal QuantityDamaged,
    decimal QuantityWrongItem,
    decimal QuantityExpired,
    decimal UnitCost,
    string? LotNumber = null,
    DateOnly? ManufacturedOn = null,
    DateOnly? ExpiresOn = null);

/// <summary>
/// The purchase order facts a receipt line is planned against: the expected
/// quantity is the line's remaining quantity, so the client can never mis-state
/// it. The expected / received / rejected split is preserved on the line for
/// the supplier performance report.
/// </summary>
/// <param name="LineNo">The order's line number.</param>
/// <param name="ProductId">The ordered product.</param>
/// <param name="OrderedQuantity">The quantity ordered.</param>
/// <param name="UnitCost">The cost agreed on the purchase order.</param>
public sealed record PurchaseOrderLineReceivingInfo(
    int LineNo,
    ProductId ProductId,
    decimal OrderedQuantity,
    decimal UnitCost);

/// <summary>Product facts the receiving rules need.</summary>
/// <param name="TracksBatches">Whether a lot number is required.</param>
/// <param name="TracksExpiry">Whether an expiry date is required.</param>
public sealed record ProductReceivingTrackingInfo(bool TracksBatches, bool TracksExpiry);

/// <summary>One line of a goods receipt, with the disposition plan.</summary>
public sealed class GoodsReceiptLine : Entity<GoodsReceiptLineId>
{
    /// <summary>Constructs a planned receipt line. Called by the receiving planner.</summary>
    internal GoodsReceiptLine(
        GoodsReceiptLineId id,
        GoodsReceiptId goodsReceiptId,
        int lineNo,
        PurchaseOrderLineId purchaseOrderLineId,
        ProductId productId,
        decimal quantityExpected,
        decimal quantityReceived,
        decimal quantityDamaged,
        decimal quantityWrongItem,
        decimal quantityExpired,
        decimal overageBeyondTolerance,
        decimal quantityAccepted,
        InventoryState acceptedState,
        decimal unitCost,
        string? lotNumber,
        DateOnly? manufacturedOn,
        DateOnly? expiresOn,
        decimal costVariancePercent)
    {
        Id = id;
        GoodsReceiptId = goodsReceiptId;
        LineNo = lineNo;
        PurchaseOrderLineId = purchaseOrderLineId;
        ProductId = productId;
        QuantityExpected = quantityExpected;
        QuantityReceived = quantityReceived;
        QuantityDamaged = quantityDamaged;
        QuantityWrongItem = quantityWrongItem;
        QuantityExpired = quantityExpired;
        OverageBeyondTolerance = overageBeyondTolerance;
        QuantityAccepted = quantityAccepted;
        AcceptedState = acceptedState;
        UnitCost = unitCost;
        LotNumber = lotNumber;
        ManufacturedOn = manufacturedOn;
        ExpiresOn = expiresOn;
        CostVariancePercent = costVariancePercent;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private GoodsReceiptLine()
    {
        PurchaseOrderLineId = PurchaseOrderLineId.Empty;
        ProductId = ProductId.Empty;
    }

    /// <summary>Gets the parent receipt identifier.</summary>
    public GoodsReceiptId GoodsReceiptId { get; private set; }

    /// <summary>Gets the sequential line number within the receipt.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the purchase order line this receipt line receives against.</summary>
    public PurchaseOrderLineId PurchaseOrderLineId { get; private set; }

    /// <summary>Gets the received product.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the remaining expected quantity at the time of this receipt.</summary>
    public decimal QuantityExpected { get; private set; }

    /// <summary>Gets the quantity physically counted on arrival.</summary>
    public decimal QuantityReceived { get; private set; }

    /// <summary>Gets the units refused as damaged.</summary>
    public decimal QuantityDamaged { get; private set; }

    /// <summary>Gets the units refused as the wrong item.</summary>
    public decimal QuantityWrongItem { get; private set; }

    /// <summary>Gets the units refused as expired on arrival.</summary>
    public decimal QuantityExpired { get; private set; }

    /// <summary>Gets the total refused quantity: damaged plus wrong item plus expired.</summary>
    public decimal QuantityRejected => QuantityDamaged + QuantityWrongItem + QuantityExpired;

    /// <summary>
    /// Gets the portion of the over-receipt beyond the tolerance, which is
    /// dispositioned to Quarantine.
    /// </summary>
    public decimal OverageBeyondTolerance { get; private set; }

    /// <summary>Gets the quantity posted into the accepted state.</summary>
    public decimal QuantityAccepted { get; private set; }

    /// <summary>Gets the state the accepted quantity is posted into.</summary>
    public InventoryState AcceptedState { get; private set; }

    /// <summary>Gets the actual unit cost of this delivery.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets the supplier's lot number, for batch-tracked products.</summary>
    public string? LotNumber { get; private set; }

    /// <summary>Gets the manufacture date, where recorded.</summary>
    public DateOnly? ManufacturedOn { get; private set; }

    /// <summary>Gets the expiry date, where the product tracks expiry.</summary>
    public DateOnly? ExpiresOn { get; private set; }

    /// <summary>
    /// Gets the percentage deviation of this line's unit cost from the purchase
    /// order's, or zero when the costs agree.
    /// </summary>
    public decimal CostVariancePercent { get; private set; }

    /// <summary>Gets whether the line deviates beyond the cost-variance tolerance.</summary>
    public bool CostVarianceBeyondTolerance => CostVariancePercent > ReceivingPolicy.CostVarianceTolerancePercent;

    /// <summary>Gets the user who approved this line's cost variance, if any.</summary>
    public UserId? CostVarianceApprovedByUserId { get; private set; }

    /// <summary>Gets when the cost variance was approved.</summary>
    public DateTimeOffset? CostVarianceApprovedAtUtc { get; private set; }

    /// <summary>Records a cost-variance approval on the line.</summary>
    /// <param name="approver">The approving user.</param>
    /// <param name="now">The current instant.</param>
    internal void ApproveCostVariance(UserId approver, DateTimeOffset now)
    {
        CostVarianceApprovedByUserId = approver;
        CostVarianceApprovedAtUtc = now;
    }
}

/// <summary>One discrepancy recorded against a goods receipt line.</summary>
public sealed class ReceivingDiscrepancy : Entity<ReceivingDiscrepancyId>
{
    /// <summary>Constructs a discrepancy. Called by the receiving planner.</summary>
    internal ReceivingDiscrepancy(
        ReceivingDiscrepancyId id,
        GoodsReceiptId goodsReceiptId,
        PurchaseOrderLineId purchaseOrderLineId,
        int lineNo,
        ReceivingDiscrepancyKind kind,
        decimal quantity,
        decimal valueImpact)
    {
        Id = id;
        GoodsReceiptId = goodsReceiptId;
        PurchaseOrderLineId = purchaseOrderLineId;
        LineNo = lineNo;
        Kind = kind;
        Quantity = quantity;
        ValueImpact = valueImpact;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ReceivingDiscrepancy()
    {
        PurchaseOrderLineId = PurchaseOrderLineId.Empty;
    }

    /// <summary>Gets the parent receipt identifier.</summary>
    public GoodsReceiptId GoodsReceiptId { get; private set; }

    /// <summary>Gets the purchase order line the discrepancy belongs to.</summary>
    public PurchaseOrderLineId PurchaseOrderLineId { get; private set; }

    /// <summary>Gets the receipt line number, for display.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the discrepancy kind.</summary>
    public ReceivingDiscrepancyKind Kind { get; private set; }

    /// <summary>Gets the quantity in dispute.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the monetary impact at the receipt unit cost.</summary>
    public decimal ValueImpact { get; private set; }
}

/// <summary>
/// A goods receipt: the physical delivery record that posts received quantity
/// into the inventory ledger and reconciles the purchase order against reality.
/// </summary>
/// <remarks>
/// <para>
/// The receipt plans dispositions at creation: expected quantity is the order
/// line's remaining quantity at the time of the receipt, and the accepted,
/// refused and quarantined pieces are derived from the three numbers the
/// receiver counted (received / damaged / wrong / expired), the batch and
/// expiry flags of the product, and the over-receipt and cost-variance
/// tolerances. The ledger legs are built by the receiving handler from the
/// planned lines; this aggregate records the plan, and the handler posts it.
/// </para>
/// </remarks>
public sealed class GoodsReceipt : AggregateRoot<GoodsReceiptId>
{
    private readonly List<GoodsReceiptLine> _lines = [];
    private readonly List<ReceivingDiscrepancy> _discrepancies = [];

    private GoodsReceipt(
        GoodsReceiptId id,
        PurchaseOrderId purchaseOrderId,
        SupplierId supplierId,
        LocationId destinationLocationId,
        bool documentsMissing,
        DateOnly businessDate,
        DateTimeOffset receivedAtUtc,
        UserId receivedBy,
        bool costVariancePendingApproval,
        decimal costVarianceValueAtStake)
    {
        Id = id;
        PurchaseOrderId = purchaseOrderId;
        SupplierId = supplierId;
        DestinationLocationId = destinationLocationId;
        Status = GoodsReceiptStatus.Posted;
        DocumentsMissing = documentsMissing;
        BusinessDate = businessDate;
        ReceivedAtUtc = receivedAtUtc;
        ReceivedByUserId = receivedBy;
        CostVariancePendingApproval = costVariancePendingApproval;
        CostVarianceValueAtStake = costVarianceValueAtStake;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private GoodsReceipt()
    {
        Number = string.Empty;
        PurchaseOrderId = PurchaseOrderId.Empty;
        SupplierId = SupplierId.Empty;
        DestinationLocationId = LocationId.Empty;
        ReceivedByUserId = UserId.Empty;
    }

    /// <summary>Gets the GRN document number, allocated by the handler before posting.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Gets the lifecycle status.</summary>
    public GoodsReceiptStatus Status { get; private set; }

    /// <summary>Gets the purchase order this receipt settles against.</summary>
    public PurchaseOrderId PurchaseOrderId { get; private set; }

    /// <summary>Gets the supplier the goods came from.</summary>
    public SupplierId SupplierId { get; private set; }

    /// <summary>Gets the location the goods were received into.</summary>
    public LocationId DestinationLocationId { get; private set; }

    /// <summary>Gets whether the delivery arrived without its expected documents.</summary>
    public bool DocumentsMissing { get; private set; }

    /// <summary>Gets the business date the receipt belongs to.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>Gets the instant the receipt was recorded.</summary>
    public DateTimeOffset ReceivedAtUtc { get; private set; }

    /// <summary>Gets who received the goods.</summary>
    public UserId ReceivedByUserId { get; private set; }

    /// <summary>
    /// Gets whether at least one line deviates beyond the cost-variance
    /// tolerance and therefore needs purchase approval authority before posting.
    /// </summary>
    public bool CostVariancePendingApproval { get; private set; }

    /// <summary>
    /// Gets the received value at stake on the lines that require cost-variance
    /// approval, used as the approval-gate amount.
    /// </summary>
    public decimal CostVarianceValueAtStake { get; private set; }

    /// <summary>Gets the receipt lines, in order.</summary>
    public IReadOnlyList<GoodsReceiptLine> Lines => _lines;

    /// <summary>Gets the discrepancies this receipt recorded.</summary>
    public IReadOnlyList<ReceivingDiscrepancy> Discrepancies => _discrepancies;

    /// <summary>
    /// Plans a goods receipt. Expected quantity per line is computed from the
    /// order line and the cumulative quantity already received; the accepted /
    /// refused / quarantined split follows the receiving policy and the
    /// product's batch and expiry tracking.
    /// </summary>
    /// <param name="purchaseOrderId">The order being received against.</param>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="destinationLocationId">The receiving location.</param>
    /// <param name="specs">The receipt lines as counted by the receiver.</param>
    /// <param name="orderLines">The order lines, keyed by line identifier.</param>
    /// <param name="receivedByLine">Cumulative received quantity per order line before this receipt.</param>
    /// <param name="products">Product tracking facts, keyed by product identifier.</param>
    /// <param name="documentsMissing">Whether the delivery lacked its documents.</param>
    /// <param name="businessDate">The business date of the receiving location.</param>
    /// <param name="receivedBy">Who is recording the receipt.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The planned receipt, or the validation failures.</returns>
    public static Result<GoodsReceipt> Create(
        PurchaseOrderId purchaseOrderId,
        SupplierId supplierId,
        LocationId destinationLocationId,
        IReadOnlyList<GoodsReceiptLineSpec> specs,
        IReadOnlyDictionary<PurchaseOrderLineId, PurchaseOrderLineReceivingInfo> orderLines,
        IReadOnlyDictionary<PurchaseOrderLineId, decimal> receivedByLine,
        IReadOnlyDictionary<ProductId, ProductReceivingTrackingInfo> products,
        bool documentsMissing,
        DateOnly businessDate,
        UserId receivedBy,
        DateTimeOffset now)
    {
        if (purchaseOrderId.IsEmpty)
        {
            return Result<GoodsReceipt>.Failure(PurchasingErrors.OrderIdRequired);
        }

        if (specs is null || specs.Count == 0)
        {
            return Result<GoodsReceipt>.Failure(PurchasingErrors.NothingReceived);
        }

        List<Error> errors = [];
        List<GoodsReceiptLine> lines = [];
        List<ReceivingDiscrepancy> discrepancies = [];
        HashSet<PurchaseOrderLineId> seenOrderLines = [];
        decimal varianceValueAtStake = 0m;
        bool anyVariance = false;

        GoodsReceiptId receiptId = GoodsReceiptId.New();

        for (int i = 0; i < specs.Count; i++)
        {
            GoodsReceiptLineSpec spec = specs[i];
            int lineNo = i + 1;

            if (!orderLines.TryGetValue(spec.PurchaseOrderLineId, out PurchaseOrderLineReceivingInfo? orderLine))
            {
                errors.Add(PurchasingErrors.ReceiptLineUnknown(spec.PurchaseOrderLineId));
                continue;
            }

            if (!seenOrderLines.Add(spec.PurchaseOrderLineId))
            {
                errors.Add(PurchasingErrors.DuplicateReceiptLine(spec.PurchaseOrderLineId));
                continue;
            }

            if (spec.QuantityReceived < 0m
                || spec.QuantityDamaged < 0m
                || spec.QuantityWrongItem < 0m
                || spec.QuantityExpired < 0m)
            {
                errors.Add(PurchasingErrors.ReceiptNegativeQuantity(lineNo));
            }

            decimal received = RoundQuantity(spec.QuantityReceived);
            decimal damaged = RoundQuantity(spec.QuantityDamaged);
            decimal wrongItem = RoundQuantity(spec.QuantityWrongItem);
            decimal expired = RoundQuantity(spec.QuantityExpired);
            decimal rejected = damaged + wrongItem + expired;
            decimal unitCost = decimal.Round(spec.UnitCost, Money.StorageScale, Money.IntermediateRounding);

            if (rejected > received)
            {
                errors.Add(PurchasingErrors.ReceiptRejectedExceedsReceived(lineNo));
            }

            if (!products.TryGetValue(orderLine.ProductId, out ProductReceivingTrackingInfo? tracking))
            {
                errors.Add(PurchasingErrors.ProductUnknown(orderLine.ProductId));
                continue;
            }

            string? lotNumber = spec.LotNumber?.Trim();
            bool lotProvided = !string.IsNullOrWhiteSpace(lotNumber);
            bool lotRequired = tracking.TracksBatches;

            if (lotRequired && !lotProvided)
            {
                errors.Add(PurchasingErrors.ReceiptLotRequired(lineNo));
            }

            if (!lotRequired && lotProvided)
            {
                errors.Add(PurchasingErrors.ReceiptLotNotAllowed(lineNo));
            }

            if (lotProvided)
            {
                lotNumber = lotNumber!.Length > 64 ? lotNumber[..64] : lotNumber;
            }

            if (tracking.TracksExpiry && spec.ExpiresOn is null)
            {
                errors.Add(PurchasingErrors.ReceiptExpiryRequired(lineNo));
            }

            if (spec.ExpiresOn is { } expiresOn && expiresOn <= businessDate)
            {
                // A lot that is already past its expiry date is expired on
                // arrival: nothing in it may be accepted. Every counted unit
                // must be recorded as refused so the goods post to quarantine.
                if (rejected < received)
                {
                    errors.Add(PurchasingErrors.ReceiptExpiredOnArrival(lineNo, expiresOn));
                }
            }

            if (spec.ManufacturedOn is { } manufactured
                && spec.ExpiresOn is { } expires
                && expires < manufactured)
            {
                errors.Add(PurchasingErrors.ReceiptExpiryBeforeManufacture(lineNo));
            }

            if (unitCost < 0m)
            {
                errors.Add(PurchasingErrors.InvalidUnitCost(lineNo));
                unitCost = 0m;
            }

            decimal remaining = decimal.Round(
                orderLine.OrderedQuantity - receivedByLine.GetValueOrDefault(spec.PurchaseOrderLineId),
                Quantity.Scale,
                MidpointRounding.ToEven);

            decimal overageBeyondTolerance = 0m;
            decimal accepted = received - rejected;

            if (received > remaining)
            {
                // Overage: within the tolerance it is accepted into
                // PendingInspection like any other good; beyond it the excess
                // is quarantined for an HQ decision.
                decimal overage = received - remaining;
                decimal toleranceQuantity = RoundQuantity(Math.Max(0m, remaining) * ReceivingPolicy.OverageTolerancePercent / 100m);

                if (overage > toleranceQuantity)
                {
                    overageBeyondTolerance = RoundQuantity(overage - toleranceQuantity);
                    accepted = RoundQuantity(accepted - overageBeyondTolerance);

                    discrepancies.Add(new ReceivingDiscrepancy(
                        ReceivingDiscrepancyId.New(),
                        receiptId,
                        spec.PurchaseOrderLineId,
                        lineNo,
                        ReceivingDiscrepancyKind.Overage,
                        overageBeyondTolerance,
                        RoundMoney(overageBeyondTolerance * unitCost)));
                }
            }
            else if (received < remaining)
            {
                // Shortage: goods that never arrived were never ours. No ledger
                // rows; the discrepancy drives the credit and the supplier
                // performance report.
                decimal shortage = RoundQuantity(remaining - received);

                discrepancies.Add(new ReceivingDiscrepancy(
                    ReceivingDiscrepancyId.New(),
                    receiptId,
                    spec.PurchaseOrderLineId,
                    lineNo,
                    ReceivingDiscrepancyKind.Shortage,
                    shortage,
                    RoundMoney(shortage * unitCost)));
            }

            if (received > 0m)
            {
                if (damaged > 0m)
                {
                    discrepancies.Add(new ReceivingDiscrepancy(
                        ReceivingDiscrepancyId.New(),
                        receiptId,
                        spec.PurchaseOrderLineId,
                        lineNo,
                        ReceivingDiscrepancyKind.Damaged,
                        damaged,
                        RoundMoney(damaged * unitCost)));
                }

                if (wrongItem > 0m)
                {
                    discrepancies.Add(new ReceivingDiscrepancy(
                        ReceivingDiscrepancyId.New(),
                        receiptId,
                        spec.PurchaseOrderLineId,
                        lineNo,
                        ReceivingDiscrepancyKind.WrongItem,
                        wrongItem,
                        RoundMoney(wrongItem * unitCost)));
                }

                if (expired > 0m)
                {
                    discrepancies.Add(new ReceivingDiscrepancy(
                        ReceivingDiscrepancyId.New(),
                        receiptId,
                        spec.PurchaseOrderLineId,
                        lineNo,
                        ReceivingDiscrepancyKind.Expired,
                        expired,
                        RoundMoney(expired * unitCost)));
                }
            }

            decimal costVariancePercent = VariancePercent(orderLine.UnitCost, unitCost);

            if (costVariancePercent > ReceivingPolicy.CostVarianceTolerancePercent)
            {
                anyVariance = true;
                varianceValueAtStake = RoundMoney(varianceValueAtStake + received * unitCost);
            }

            GoodsReceiptLine line = new(
                GoodsReceiptLineId.New(),
                receiptId,
                lineNo,
                spec.PurchaseOrderLineId,
                orderLine.ProductId,
                remaining,
                received,
                damaged,
                wrongItem,
                expired,
                overageBeyondTolerance,
                accepted,
                ReceivingPolicy.DefaultAcceptedState,
                unitCost,
                lotNumber,
                spec.ManufacturedOn,
                spec.ExpiresOn,
                costVariancePercent);

            lines.Add(line);
        }

        bool nothingReceived = lines.Count == 0 || lines.All(l => l.QuantityReceived == 0m);

        if (nothingReceived)
        {
            errors.Add(PurchasingErrors.NothingReceived);
        }

        if (errors.Count > 0)
        {
            return Result<GoodsReceipt>.Failure(errors);
        }

        if (documentsMissing)
        {
            discrepancies.Add(new ReceivingDiscrepancy(
                ReceivingDiscrepancyId.New(),
                receiptId,
                lines[0].PurchaseOrderLineId,
                lines[0].LineNo,
                ReceivingDiscrepancyKind.MissingDocuments,
                quantity: 0m,
                RoundMoney(0m)));
        }

        GoodsReceipt receipt = new(
            receiptId,
            purchaseOrderId,
            supplierId,
            destinationLocationId,
            documentsMissing,
            businessDate,
            now,
            receivedBy,
            anyVariance,
            RoundMoney(varianceValueAtStake));

        foreach (GoodsReceiptLine line in lines)
        {
            receipt._lines.Add(line);
        }

        foreach (ReceivingDiscrepancy discrepancy in discrepancies)
        {
            receipt._discrepancies.Add(discrepancy);
        }

        return Result<GoodsReceipt>.Success(receipt);
    }

    /// <summary>
    /// Assigns the GRN document number. Called by the handler after the number
    /// is allocated so an invalid receipt never consumes a sequence value.
    /// </summary>
    /// <param name="number">The allocated document number.</param>
    /// <returns>Success, or a conflict when the receipt already carries a number.</returns>
    public Result AssignNumber(DocumentNumber number)
    {
        if (Number.Length > 0)
        {
            return Result.Failure(Error.Conflict(
                "purchasing.receipt_already_numbered",
                FormattableString.Invariant($"Goods receipt {Id.Value} already has number {Number}.")));
        }

        Number = number.Value;
        return Result.Success();
    }

    /// <summary>
    /// Records that the cost variance on the deviating lines was approved under
    /// the receiver's purchase-approval authority.
    /// </summary>
    /// <param name="approver">The approving user.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a conflict when there is nothing to approve.</returns>
    public Result GrantCostVarianceApproval(UserId approver, DateTimeOffset now)
    {
        if (!CostVariancePendingApproval)
        {
            return Result.Failure(Error.Conflict(
                "purchasing.receipt_no_variance_to_approve",
                "This receipt carries no cost variance requiring approval."));
        }

        foreach (GoodsReceiptLine line in _lines.Where(l => l.CostVarianceBeyondTolerance))
        {
            line.ApproveCostVariance(approver, now);
        }

        return Result.Success();
    }

    /// <summary>Rounds to the ledger quantity scale.</summary>
    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, Quantity.Scale, MidpointRounding.ToEven);

    /// <summary>Rounds to the monetary storage scale.</summary>
    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, Money.StorageScale, Money.IntermediateRounding);

    private static decimal VariancePercent(decimal poUnitCost, decimal receiptUnitCost)
    {
        if (poUnitCost == 0m)
        {
            return receiptUnitCost == 0m ? 0m : 100m;
        }

        return Math.Abs(receiptUnitCost - poUnitCost) / poUnitCost * 100m;
    }
}