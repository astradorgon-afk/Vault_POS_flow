namespace Pos.Client.Services;

/// <summary>A client API result with a user-safe failure message.</summary>
/// <typeparam name="T">The payload type.</typeparam>
/// <param name="Value">The payload, when the call succeeded.</param>
/// <param name="Error">A user-safe explanation, when it failed.</param>
/// <param name="ErrorCode">A stable error code, when the API supplied one.</param>
public sealed record ApiResult<T>(T? Value, string? Error, string? ErrorCode)
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess => Error is null && Value is not null;

    /// <summary>Creates a successful result.</summary>
    public static ApiResult<T> Success(T value) => new(value, null, null);

    /// <summary>Creates a failed result.</summary>
    public static ApiResult<T> Failure(string error, string? errorCode = null) => new(default, error, errorCode);
}

/// <summary>A reference to a head-office document.</summary>
public sealed class PosReference
{
    /// <summary>Gets or sets the document identifier.</summary>
    public Guid Id { get; set; }
}

/// <summary>A physical location available to the signed-in operator.</summary>
public sealed class PosLocation
{
    /// <summary>Gets or sets the location identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the short location code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the location name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the numeric location kind.</summary>
    public int Kind { get; set; }

    /// <summary>Gets or sets whether the location accepts operations.</summary>
    public bool IsActive { get; set; }
}

/// <summary>An inventory count as listed in the dashboard.</summary>
public sealed record PosInventoryCountSummary(
    Guid Id,
    string Number,
    Guid LocationId,
    string Kind,
    string Status,
    int Lines,
    int CountedLines,
    decimal TotalAbsoluteVarianceValue,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PostedAtUtc);

/// <summary>An inventory count with its recorded lines.</summary>
public sealed record PosInventoryCountDetail(
    PosInventoryCountSummary Count,
    string? Note,
    DateTimeOffset SnapshotTakenAtUtc,
    Guid CreatedByUserId,
    Guid? SubmittedByUserId,
    Guid? ApprovedByUserId,
    string? LastRejectionReason,
    string? CancellationReason,
    IReadOnlyList<PosInventoryCountLineView> Lines);

/// <summary>One counted bucket on an inventory count sheet.</summary>
public sealed record PosInventoryCountLineView(
    int LineNo,
    Guid ProductId,
    Guid? BatchId,
    decimal SystemQuantity,
    decimal? PhysicalQuantity,
    decimal? Variance,
    decimal UnitCost,
    decimal? VarianceValue,
    bool IsRepeatVariance,
    DateTimeOffset? CountedAtUtc);

/// <summary>A product category, used to scope a category or cycle count.</summary>
public sealed class PosCategory
{
    /// <summary>Gets or sets the category identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the category code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the category name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the category is active.</summary>
    public bool IsActive { get; set; }
}

/// <summary>A product master row used to name count-sheet lines.</summary>
public sealed record PosProductSummary(
    Guid Id,
    string Sku,
    string Name,
    bool IsActive);

/// <summary>
/// A product master row shown in the purchase order composer, carrying the
/// data an order line needs: the base unit and the default purchase cost.
/// The cost is null when the signed-in user lacks permission to view it.
/// </summary>
public sealed record PosOrderableProduct(
    Guid Id,
    string Sku,
    string Name,
    Guid BaseUnitOfMeasureId,
    decimal? DefaultPurchaseCost,
    bool IsActive);

/// <summary>A unit of measure available to order lines.</summary>
public sealed record PosUnitOfMeasure(
    Guid Id,
    string Code,
    string Name,
    int DecimalPlaces);

/// <summary>
/// A product resolved from a scanned barcode on the receiving sheet. The
/// barcode lookup answers exactly like an unknown code for retired barcodes,
/// so the receiving screen routes those to quarantine just like unknown codes.
/// </summary>
public sealed record PosScannedProduct(
    Guid Id,
    string Sku,
    string Name,
    IReadOnlyList<string> Barcodes,
    bool TracksBatches,
    bool IsActive);

/// <summary>
/// The body of a quarantine incident raised from the receiving sheet.
/// </summary>
/// <param name="LocationId">The location where the goods were found.</param>
/// <param name="Lines">The lines to quarantine, in the products' base units.</param>
/// <param name="Note">An optional note explaining the finding.</param>
public sealed record CreateQuarantineIncidentBody(
    Guid LocationId,
    IReadOnlyList<CreateQuarantineLineBody> Lines,
    string? Note = null);

/// <summary>One scanned line queued for quarantine while receiving.</summary>
/// <param name="Barcode">The barcode that was scanned.</param>
/// <param name="Quantity">The counted quantity, in the product's base unit.</param>
/// <param name="UnitCost">The recorded value per unit, or null to fall back to the product default.</param>
/// <param name="ClaimedProductName">A best-effort description from the raising store.</param>
public sealed record CreateQuarantineLineBody(
    string Barcode,
    decimal Quantity,
    decimal? UnitCost = null,
    string? ClaimedProductName = null);

/// <summary>The body that creates a purchase order.</summary>
/// <param name="SupplierId">The managed supplier, or null when provisioning a new supplier by name.</param>
/// <param name="DestinationLocationId">The store that will receive the goods.</param>
/// <param name="Lines">The ordered lines, in each product's base unit.</param>
/// <param name="CurrencyCode">Three-letter ISO-4217 code, defaulting to PHP.</param>
/// <param name="ExpectedAtUtc">Expected delivery instant, or null.</param>
/// <param name="CustomSupplierName">Name of a new supplier, used only when <paramref name="SupplierId"/> is null.</param>
public sealed record CreatePurchaseOrderBody(
    Guid? SupplierId,
    Guid DestinationLocationId,
    IReadOnlyList<CreatePurchaseOrderLineBody> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null,
    string? CustomSupplierName = null);

/// <summary>One line of a purchase order creation.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="UnitOfMeasureId">The product's base unit.</param>
/// <param name="OrderedQuantity">The quantity to order.</param>
/// <param name="UnitCost">The cost per base unit.</param>
public sealed record CreatePurchaseOrderLineBody(
    Guid ProductId,
    Guid UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost);

/// <summary>The body of an approval or rejection decision.</summary>
/// <param name="Notes">Optional notes attached to the decision.</param>
public sealed record PurchaseOrderDecisionBody(string? Notes = null);

/// <summary>The body that opens a stock count.</summary>
public sealed record PosOpenInventoryCountRequest(
    Guid LocationId,
    int Kind,
    IReadOnlyList<Guid>? CategoryIds,
    string? Note);

/// <summary>One counted quantity sent for a count line.</summary>
public sealed record PosCountLineRequest(Guid ProductId, decimal PhysicalQuantity, Guid? BatchId = null);

/// <summary>The body that records counted quantities.</summary>
public sealed record PosRecordCountLinesRequest(IReadOnlyList<PosCountLineRequest> Lines);

/// <summary>A reason attached to a count rejection or cancellation.</summary>
public sealed record PosInventoryControlReasonRequest(string? Reason);

/// <summary>A transfer as listed in the transfers dashboard.</summary>
public sealed record PosTransferSummary(
    Guid Id,
    string Number,
    string Status,
    Guid SourceLocationId,
    Guid DestinationLocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc,
    decimal TotalValue,
    int LineCount);

/// <summary>A transfer with its lines, allocations and arrival state.</summary>
public sealed record PosTransferDetail(
    PosTransferSummary Transfer,
    string Kind,
    string Mode,
    string? ReviewNote,
    IReadOnlyList<PosTransferLineView> Lines);

/// <summary>One line of a transfer detail.</summary>
public sealed record PosTransferLineView(
    int LineNo,
    Guid ProductId,
    string? ProductName,
    decimal RequestedQuantity,
    decimal PickedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity,
    string? Note,
    string? DiscrepancyState,
    IReadOnlyList<PosTransferAllocationView> Allocations);

/// <summary>One picked lot within a transfer line.</summary>
public sealed record PosTransferAllocationView(
    Guid? BatchId,
    decimal PickedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

/// <summary>One arrival receipt line, keyed to a dispatched allocation.</summary>
public sealed record PosTransferReceiveLine(
    int LineNo,
    Guid? BatchId,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

/// <summary>The body that records a transfer arrival.</summary>
public sealed record PosReceiveTransferRequest(IReadOnlyList<PosTransferReceiveLine> Receives);

/// <summary>One line of a transfer request.</summary>
public sealed record PosTransferLineRequest(Guid ProductId, decimal Quantity, string? Note = null);

/// <summary>The body that raises a transfer request.</summary>
public sealed record PosCreateTransferRequest(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<PosTransferLineRequest> Lines,
    Guid? PreApprovalTokenId = null);

/// <summary>An approval-time quantity change.</summary>
public sealed record PosTransferAmendment(int LineNo, decimal RequestedQuantity);

/// <summary>The body of a transfer approval, optionally amending quantities.</summary>
public sealed record PosApproveTransferRequest(
    IReadOnlyList<PosTransferAmendment>? Amendments = null,
    string? Note = null);

/// <summary>One picked lot of a transfer.</summary>
public sealed record PosTransferPickLine(Guid? BatchId, int LineNo, decimal Quantity);

/// <summary>The body recording what was picked.</summary>
public sealed record PosPickTransferRequest(IReadOnlyList<PosTransferPickLine> Allocations);

/// <summary>The body cancelling a dispatch.</summary>
public sealed record PosCancelTransferDispatchRequest(string Reason);

/// <summary>A note attached to a review, rejection or cancellation.</summary>
public sealed record PosTransferReasonRequest(string? Note);

/// <summary>One step in a transfer's custody timeline.</summary>
public sealed record PosTransferCustodyEventSummary(
    int Sequence,
    string Kind,
    Guid ActorUserId,
    DateTimeOffset OccurredAtUtc,
    string? Note);

/// <summary>The movement chain for one inventory document.</summary>
public sealed class PosInventoryTimeline
{
    /// <summary>Gets or sets the document type, for example Sale or Transfer.</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Gets or sets the document identifier.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Gets or sets the human-readable document number.</summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the movement groups that make up the chain.</summary>
    public List<PosInventoryTimelineGroup> Groups { get; set; } = [];
}

/// <summary>One movement group in an inventory timeline.</summary>
public sealed class PosInventoryTimelineGroup
{
    /// <summary>Gets or sets the movement group identifier.</summary>
    public Guid MovementGroupId { get; set; }

    /// <summary>Gets or sets the movement type, for example Post, Adjustment or Dispatch.</summary>
    public string MovementType { get; set; } = string.Empty;

    /// <summary>Gets or sets the reference number of the owning document.</summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets when the movement occurred.</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Gets or sets when the movement was recorded.</summary>
    public DateTimeOffset RecordedAtUtc { get; set; }

    /// <summary>Gets or sets the legs of the movement group.</summary>
    public List<PosInventoryTimelineLeg> Legs { get; set; } = [];
}

/// <summary>One leg of an inventory movement group.</summary>
public sealed class PosInventoryTimelineLeg
{
    /// <summary>Gets or sets the individual movement identifier.</summary>
    public Guid MovementId { get; set; }

    /// <summary>Gets or sets the leg number within the group.</summary>
    public short LegNumber { get; set; }

    /// <summary>Gets or sets the product.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Gets or sets the location the leg applied to.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the bucket state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the signed quantity change.</summary>
    public decimal QuantityDelta { get; set; }

    /// <summary>Gets or sets the per-unit cost at the time of the movement.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>Gets or sets the source location for a dispatch.</summary>
    public Guid? SourceLocationId { get; set; }

    /// <summary>Gets or sets the destination location for an arrival.</summary>
    public Guid? DestinationLocationId { get; set; }

    /// <summary>Gets or sets the movement group this leg reverses.</summary>
    public Guid? ReversesMovementGroupId { get; set; }
}

/// <summary>A product/location pair repeatedly refused by the stock policy.</summary>
public sealed class PosNegativeStockSummary
{
    /// <summary>Gets or sets the location.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the location code.</summary>
    public string? LocationCode { get; set; }

    /// <summary>Gets or sets the product.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the SKU.</summary>
    public string? Sku { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string? ProductName { get; set; }

    /// <summary>Gets or sets the number of refused attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the total shortfall refused.</summary>
    public decimal TotalShortfall { get; set; }
}

/// <summary>A product/location pair with repeated physical-count variance.</summary>
public sealed class PosRepeatVariance
{
    /// <summary>Gets or sets the location.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the product.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the SKU.</summary>
    public string? Sku { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string? ProductName { get; set; }

    /// <summary>Gets or sets the number of posted counts that varied.</summary>
    public int Occurrences { get; set; }

    /// <summary>Gets or sets the total absolute variance value.</summary>
    public decimal TotalAbsoluteVarianceValue { get; set; }
}

/// <summary>Scoped inventory availability totals for the stock dashboard.</summary>
public sealed class PosInventoryOverview
{
    /// <summary>Gets or sets the total available quantity.</summary>
    public decimal AvailableQuantity { get; set; }

    /// <summary>Gets or sets the total in-transit quantity.</summary>
    public decimal InTransitQuantity { get; set; }

    /// <summary>Gets or sets the total quarantine quantity.</summary>
    public decimal QuarantineQuantity { get; set; }

    /// <summary>Gets or sets the number of low-stock items.</summary>
    public int LowStockItems { get; set; }

    /// <summary>Gets or sets the number of out-of-stock items.</summary>
    public int OutOfStockItems { get; set; }

    /// <summary>Gets or sets the number of over-stock items.</summary>
    public int OverStockItems { get; set; }
}

/// <summary>The daily close-of-trade report for a store.</summary>
public sealed record PosDailySalesReport(
    Guid LocationId,
    string LocationName,
    DateOnly BusinessDate,
    PosDailySalesSummary SalesSummary,
    IReadOnlyList<PosDailyPaymentSummary> PaymentsByMethod,
    IReadOnlyList<PosDailyShiftSummary> Shifts);

/// <summary>Sales and refund totals for a business date.</summary>
public sealed record PosDailySalesSummary(
    int SalesCount,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    decimal VatTotal,
    decimal VatExemptTotal,
    decimal ZeroRatedTotal,
    decimal TaxableBaseTotal,
    decimal RefundTotal);

/// <summary>Collected value through one payment rail.</summary>
public sealed record PosDailyPaymentSummary(
    string Method,
    decimal Amount,
    decimal ChangeGiven);

/// <summary>One cashier shift's contribution to a daily report.</summary>
public sealed record PosDailyShiftSummary(
    Guid ShiftId,
    string ShiftNumber,
    Guid CashierUserId,
    string Status,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal OpeningFloat,
    int SalesCount,
    decimal NetTotal,
    decimal CashSalesTotal,
    decimal CashRefundsTotal);

/// <summary>A supplier as listed for purchasing.</summary>
public sealed record SupplierSummary(
    Guid Id,
    string Code,
    string Name,
    string? TaxId,
    int PaymentTermsDays,
    int LeadTimeDays,
    bool IsActive);

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
    decimal LineTotal,
    string? ProductSku = null,
    string? ProductName = null);

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
public sealed record CreateGoodsReceiptBody(
    IReadOnlyList<CreateGoodsReceiptLineBody> Lines,
    bool DocumentsMissing = false);

/// <summary>One line of a goods receipt creation.</summary>
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