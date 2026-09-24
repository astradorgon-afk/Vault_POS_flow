namespace Pos.Web.Services;

/// <summary>The authenticated operator's durable notification feed.</summary>
public sealed class PosNotificationFeed
{
    /// <summary>Gets or sets the newest visible notifications.</summary>
    public IReadOnlyList<PosNotification> Items { get; set; } = [];

    /// <summary>Gets or sets the total unread count across the visible feed.</summary>
    public int UnreadCount { get; set; }
}

/// <summary>One operational notification delivered by HTTP or SignalR.</summary>
public sealed class PosNotification
{
    public Guid Id { get; set; }
    public int Kind { get; set; }
    public int Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public Guid? LocationId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? BatchId { get; set; }
    public int? ReferenceDocumentType { get; set; }
    public Guid? ReferenceDocumentId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReadAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}

/// <summary>The result of marking the visible feed read.</summary>
public sealed class PosMarkedReadResult
{
    public int MarkedRead { get; set; }
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
}

/// <summary>A product returned by catalog search or barcode lookup.</summary>
public sealed class PosProduct
{
    /// <summary>Gets or sets the product identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the SKU.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Gets or sets the product display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the base unit identifier used by sale lines.</summary>
    public Guid BaseUnitOfMeasureId { get; set; }

    /// <summary>Gets or sets whether the product is VAT exempt.</summary>
    public bool IsVatExempt { get; set; }

    /// <summary>Gets or sets active barcodes, primary first.</summary>
    public IReadOnlyList<string> Barcodes { get; set; } = [];
}

/// <summary>An effective-dated price returned by the catalog API.</summary>
public sealed class PosProductPrice
{
    /// <summary>Gets or sets the price row identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the location override, or null for the organization price.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>Gets or sets the selling amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets when the price takes effect.</summary>
    public DateTimeOffset EffectiveFromUtc { get; set; }

    /// <summary>Gets or sets when the temporary price stops, if applicable.</summary>
    public DateTimeOffset? EffectiveToUtc { get; set; }

    /// <summary>Gets or sets why this price was scheduled.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets whether the row is effective now.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>The body used to schedule a product selling price.</summary>
public sealed record PosSchedulePriceRequest(
    decimal Amount,
    string Reason,
    Guid? LocationId,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc);

/// <summary>The body used to cancel a future product price.</summary>
public sealed record PosCancelPriceRequest(string Reason);

/// <summary>One editable line in the in-memory POS cart.</summary>
public sealed class PosCartLine(PosProduct product, decimal unitPrice)
{
    /// <summary>Gets the product.</summary>
    public PosProduct Product { get; } = product;

    /// <summary>Gets the resolved selling price.</summary>
    public decimal UnitPrice { get; } = unitPrice;

    /// <summary>Gets or sets the quantity.</summary>
    public decimal Quantity { get; set; } = 1m;

    /// <summary>Gets the extended amount.</summary>
    public decimal Total => decimal.Round(UnitPrice * Quantity, 4, MidpointRounding.ToEven);
}

/// <summary>A browser register available for a store (an Active web-platform device).</summary>
public sealed class PosRegister
{
    /// <summary>Gets or sets the register (device) identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the document-number short code.</summary>
    public string ShortCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the register's display name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>The checkout context the server mints for one register (POS.md §2).</summary>
public sealed class PosTerminalSession
{
    /// <summary>Gets or sets the register (device) identifier.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>Gets or sets the register short code.</summary>
    public string DeviceShortCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the register name.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the store's business date.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets the store's VAT rate for non-exempt lines.</summary>
    public decimal VatRate { get; set; }

    /// <summary>Gets or sets the store's cash-change rounding increment.</summary>
    public decimal CashRoundingIncrement { get; set; }

    /// <summary>Gets or sets the open shift on the register, or null.</summary>
    public PosOpenShift? OpenShift { get; set; }
}

/// <summary>A shift currently open on a register.</summary>
public sealed class PosOpenShift
{
    /// <summary>Gets or sets the shift identifier.</summary>
    public Guid ShiftId { get; set; }

    /// <summary>Gets or sets the SHF document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the cashier's user identifier.</summary>
    public Guid CashierId { get; set; }

    /// <summary>Gets or sets the cashier's display name.</summary>
    public string CashierName { get; set; } = string.Empty;

    /// <summary>Gets or sets the business date the shift opened on.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the shift opened.</summary>
    public DateTimeOffset OpenedAtUtc { get; set; }

    /// <summary>Gets or sets the opening float.</summary>
    public decimal OpeningFloat { get; set; }
}

/// <summary>A reference to a completed sale.</summary>
public sealed class PosCompletedSale
{
    /// <summary>Gets or sets the sale identifier.</summary>
    public Guid Id { get; set; }
}

/// <summary>The body of a complete-sale call. Mirrors the API's
/// <c>CompleteSaleBody</c>; the cashier comes from the signed-in session.</summary>
public sealed record PosCompleteSaleRequest(
    string Number,
    Guid EventId,
    Guid LocationId,
    Guid CashierShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<PosSaleLine> Lines,
    IReadOnlyList<PosPayment> Payments);

/// <summary>One line of a completed sale.</summary>
public sealed record PosSaleLine(
    Guid ProductId,
    decimal Quantity,
    Guid UnitOfMeasureId,
    string? Barcode,
    decimal? UnitPriceOverride,
    Guid? PriceOverrideAuthorizedByUserId,
    decimal Discount,
    Guid? DiscountAuthorizedByUserId,
    bool AllowExpiredOverride,
    string? ExpiredOverrideReason);

/// <summary>One payment that settles a sale. Method is the numeric
/// <c>PaymentMethod</c> value: <c>1</c> cash, <c>2</c> card, <c>3</c> e-wallet.</summary>
public sealed record PosPayment(
    int Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);

/// <summary>One completed sale as returned by the sales search.</summary>
public sealed class PosSaleSummary
{
    /// <summary>Gets or sets the sale identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the SAL document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the sale status (Completed, Voided).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the business date the sale belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the sale was completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>Gets or sets the gross total before discounts.</summary>
    public decimal GrossTotal { get; set; }

    /// <summary>Gets or sets the net amount settled.</summary>
    public decimal NetTotal { get; set; }
}

/// <summary>A product/location pair repeatedly refused by the stock policy.</summary>
public sealed class PosNegativeStockSummary
{
    public Guid LocationId { get; set; }
    public string? LocationCode { get; set; }
    public Guid ProductId { get; set; }
    public string? Sku { get; set; }
    public string? ProductName { get; set; }
    public int Attempts { get; set; }
    public decimal TotalShortfall { get; set; }
}

/// <summary>A product/location pair with repeated physical-count variance.</summary>
public sealed class PosRepeatVariance
{
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    public string? Sku { get; set; }
    public string? ProductName { get; set; }
    public int Occurrences { get; set; }
    public decimal TotalAbsoluteVarianceValue { get; set; }
}

/// <summary>Scoped inventory availability totals for the owner dashboard.</summary>
public sealed class PosInventoryOverview
{
    public decimal AvailableQuantity { get; set; }
    public decimal InTransitQuantity { get; set; }
    public decimal QuarantineQuantity { get; set; }
    public int LowStockItems { get; set; }
    public int OutOfStockItems { get; set; }
    public int OverStockItems { get; set; }
}

/// <summary>The movement chain for one inventory document.</summary>
public sealed class PosInventoryTimeline
{
    public string DocumentType { get; set; } = string.Empty;
    public Guid DocumentId { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public List<PosInventoryTimelineGroup> Groups { get; set; } = [];
}

public sealed class PosInventoryTimelineGroup
{
    public Guid MovementGroupId { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public List<PosInventoryTimelineLeg> Legs { get; set; } = [];
}

public sealed class PosInventoryTimelineLeg
{
    public Guid MovementId { get; set; }
    public short LegNumber { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public Guid LocationId { get; set; }
    public string State { get; set; } = string.Empty;
    public decimal QuantityDelta { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? SourceLocationId { get; set; }
    public Guid? DestinationLocationId { get; set; }
    public Guid? ReversesMovementGroupId { get; set; }
}

/// <summary>An open synchronization failure requiring operator attention.</summary>
public sealed class PosSyncFailure
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextRetryAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>A completed sale as returned by the detail route.</summary>
public sealed class PosSaleDetail
{
    /// <summary>Gets or sets the sale identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the SAL document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the sale status (Completed, Voided).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the store the sale was completed at.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the customer the sale was settled for, or null.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets the business date the sale belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the sale was completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>Gets or sets the gross total before discounts.</summary>
    public decimal GrossTotal { get; set; }

    /// <summary>Gets or sets the applied discounts.</summary>
    public decimal DiscountTotal { get; set; }

    /// <summary>Gets or sets the net amount settled.</summary>
    public decimal NetTotal { get; set; }

    /// <summary>Gets or sets the VAT charged on the sale.</summary>
    public decimal VatTotal { get; set; }

    /// <summary>Gets or sets the sale lines, ordered by line number.</summary>
    public IReadOnlyList<PosSaleLineDetail> Lines { get; set; } = [];

    /// <summary>Gets or sets the payments that settled the sale.</summary>
    public IReadOnlyList<PosSalePaymentDetail> Payments { get; set; } = [];
}

/// <summary>One line of a completed sale.</summary>
public sealed class PosSaleLineDetail
{
    /// <summary>Gets or sets the line number on the sale.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the product identifier.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Gets or sets the scanned barcode, or null.</summary>
    public string? Barcode { get; set; }

    /// <summary>Gets or sets the quantity sold.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Gets or sets the selling unit price.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Gets or sets the applied line discount.</summary>
    public decimal Discount { get; set; }

    /// <summary>Gets or sets the gross line amount.</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>Gets or sets the net line amount after discount.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>Gets or sets the VAT allocated to the line.</summary>
    public decimal Vat { get; set; }
}

/// <summary>One payment that settled a sale.</summary>
public sealed class PosSalePaymentDetail
{
    /// <summary>Gets or sets the payment method name.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the settled amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the cash tendered, or null.</summary>
    public decimal? Tendered { get; set; }

    /// <summary>Gets or sets the change returned, or null.</summary>
    public decimal? Change { get; set; }

    /// <summary>Gets or sets the card or wallet reference, or null.</summary>
    public string? ProviderReference { get; set; }
}

/// <summary>A customer return as returned by the detail route.</summary>
public sealed class PosReturnDetail
{
    /// <summary>Gets or sets the return identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the RET document number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the return has no original sale.</summary>
    public bool IsBlind { get; set; }

    /// <summary>Gets or sets the original sale, or null for a blind return.</summary>
    public Guid? SaleId { get; set; }

    /// <summary>Gets or sets the store the return was accepted at.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the customer, or null.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Gets or sets the business date the return belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>Gets or sets when the return was accepted.</summary>
    public DateTimeOffset ReturnedAtUtc { get; set; }

    /// <summary>Gets or sets the total still refundable.</summary>
    public decimal RefundableTotal { get; set; }

    /// <summary>Gets or sets the total already refunded.</summary>
    public decimal RefundedTotal { get; set; }

    /// <summary>Gets or sets the return lines, ordered by line number.</summary>
    public IReadOnlyList<PosReturnLineDetail> Lines { get; set; } = [];

    /// <summary>Gets or sets the refunds paid, ordered by time.</summary>
    public IReadOnlyList<PosReturnRefundDetail> Refunds { get; set; } = [];
}

/// <summary>One accepted return line.</summary>
public sealed class PosReturnLineDetail
{
    /// <summary>Gets or sets the line number on the return.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the original sale item, or null for a blind return.</summary>
    public Guid? SaleItemId { get; set; }

    /// <summary>Gets or sets the product identifier.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Gets or sets the scanned barcode, or null.</summary>
    public string? Barcode { get; set; }

    /// <summary>Gets or sets the accepted quantity.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Gets or sets the quantity inspected so far.</summary>
    public decimal DispositionedQuantity { get; set; }

    /// <summary>Gets or sets the quantity awaiting inspection.</summary>
    public decimal PendingDispositionQuantity { get; set; }

    /// <summary>Gets or sets the original selling unit price.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Gets or sets the net amount returned.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>Gets or sets the amount still refundable on this line.</summary>
    public decimal RefundableAmount { get; set; }

    /// <summary>Gets or sets the batch returned, or null.</summary>
    public string? BatchCode { get; set; }

    /// <summary>Gets or sets the batch expiry, or null.</summary>
    public DateOnly? BatchExpiresOn { get; set; }
}

/// <summary>One refund paid against a return.</summary>
public sealed class PosReturnRefundDetail
{
    /// <summary>Gets or sets the refund identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the payment method name.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the refunded amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the cash tendered, or null.</summary>
    public decimal? Tendered { get; set; }

    /// <summary>Gets or sets the card or wallet reference, or null.</summary>
    public string? ProviderReference { get; set; }

    /// <summary>Gets or sets when the refund was paid.</summary>
    public DateTimeOffset RefundedAtUtc { get; set; }
}

/// <summary>The body of a receipt-reprint request. The operator comes from the
/// signed-in session; a reprint is a read-side emission and needs no shift.</summary>
public sealed record PosReprintSaleRequest(
    Guid LocationId,
    Guid DeviceId,
    string Reason,
    DateTimeOffset ReprintedAtUtc);

/// <summary>The body of a void-sale request.</summary>
public sealed record PosVoidSaleRequest(
    Guid EventId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    DateOnly BusinessDate,
    DateTimeOffset VoidedAtUtc,
    string Reason);

/// <summary>The body of a return-against-a-sale request. The operator comes from
/// the signed-in session.</summary>
public sealed record PosCreateReturnRequest(
    string Number,
    Guid EventId,
    Guid SaleId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset ReturnedAtUtc,
    IReadOnlyList<PosCreateReturnLine> Lines);

/// <summary>One line of an accepted return.</summary>
public sealed record PosCreateReturnLine(Guid ProductId, decimal Quantity);

/// <summary>The body of a return-refund request. The operator comes from the
/// signed-in session; a blind return's refund is cash only.</summary>
public sealed record PosRefundReturnRequest(
    Guid? SaleId,
    Guid EventId,
    Guid LocationId,
    Guid ShiftId,
    Guid DeviceId,
    int Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference,
    DateTimeOffset RefundedAtUtc);

/// <summary>The body of a return-inspection decision. Needs no register context.</summary>
public sealed record PosDisposeReturnRequest(
    Guid EventId,
    Guid LocationId,
    int LineNumber,
    decimal Quantity,
    int Kind,
    int ReasonCode,
    string Note);

/// <summary>A server-issued reference to a created or updated document.</summary>
public sealed class PosReference
{
    /// <summary>Gets or sets the referenced document identifier.</summary>
    public Guid Id { get; set; }
}

/// <summary>A payment receipt as returned by the receipts list.</summary>
public sealed class PosReceiptSummary
{
    /// <summary>Gets or sets the receipt identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the RCT number, for example RCT-2026-D03-000012.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind name: WalkInSale, BranchExpense or OwnerWithdrawal.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the store the cash event happened at.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the amount recorded.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the counterparty, if any.</summary>
    public string? Counterparty { get; set; }

    /// <summary>Gets or sets who issued the receipt.</summary>
    public Guid IssuedByUserId { get; set; }

    /// <summary>Gets or sets when the receipt was issued.</summary>
    public DateTimeOffset IssuedAtUtc { get; set; }
}

/// <summary>The body of a payment receipt issue.</summary>
public sealed class PosIssueReceiptRequest
{
    /// <summary>Gets or sets the store the cash event happened at.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the receipt kind: 1 Walk-in sale, 2 Branch expense, 3 Owner withdrawal.</summary>
    public int Kind { get; set; }

    /// <summary>Gets or sets the amount recorded, greater than zero.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the optional counterparty name, for example a customer or supplier.</summary>
    public string? Counterparty { get; set; }

    /// <summary>Gets or sets the optional purpose note.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets the optional number of a related document, such as a sale.</summary>
    public string? ReferenceNumber { get; set; }
}

/// <summary>The response of a payment receipt issue.</summary>
public sealed class PosNewReceipt
{
    /// <summary>Gets or sets the created receipt identifier.</summary>
    public Guid Id { get; set; }
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

// ---- People & roles ----

/// <summary>An account as listed for administration.</summary>
public sealed record PosUserSummary(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? EmployeeCode,
    bool IsActive,
    bool TwoFactorEnabled,
    int ApprovalTier,
    IReadOnlyList<string> Roles,
    IReadOnlyList<Guid> LocationIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

/// <summary>One location a user is assigned to.</summary>
public sealed record PosUserLocationSpec(Guid LocationId, bool IsPrimary);

/// <summary>A per-user permission override shown to an administrator.</summary>
public sealed record PosUserOverride(
    Guid Id,
    string PermissionCode,
    string Effect,
    Guid? LocationId,
    string Reason,
    DateTimeOffset GrantedAtUtc,
    Guid GrantedByUserId,
    DateTimeOffset? ExpiresAtUtc,
    bool IsActive);

/// <summary>An account with everything that shapes its authority.</summary>
public sealed record PosUserDetail(
    PosUserSummary User,
    IReadOnlyList<PosUserLocationSpec> Locations,
    IReadOnlyList<PosUserOverride> Overrides,
    IReadOnlyList<string> EffectivePermissions,
    bool HasPin,
    bool IsLockedOut,
    DateTimeOffset? DisabledAtUtc,
    string? DisabledReason);

/// <summary>The body creating an account.</summary>
public sealed record PosCreateUserRequest(
    string UserName,
    string DisplayName,
    string Password,
    string? Email,
    string? EmployeeCode,
    int ApprovalTier,
    IReadOnlyList<string> Roles,
    IReadOnlyList<PosUserLocationSpec> Locations);

/// <summary>The body updating an account's details.</summary>
public sealed record PosUpdateUserRequest(
    string DisplayName,
    string? Email,
    string? EmployeeCode,
    int ApprovalTier);

/// <summary>The roles an account should hold.</summary>
public sealed record PosUserRolesRequest(IReadOnlyList<string> Roles);

/// <summary>The locations an account should be assigned to.</summary>
public sealed record PosUserLocationsRequest(IReadOnlyList<PosUserLocationSpec> Locations);

/// <summary>An override granted to or withheld from one account.</summary>
public sealed record PosOverrideRequest(
    string PermissionCode,
    int Effect,
    string Reason,
    DateTimeOffset? ExpiresAtUtc = null,
    Guid? LocationId = null);

/// <summary>A new password set by an administrator.</summary>
public sealed record PosResetPasswordRequest(string NewPassword);

/// <summary>A cashier PIN set by an administrator.</summary>
public sealed record PosSetPinRequest(string Pin);

/// <summary>The permissions a role should bundle.</summary>
public sealed record PosRolePermissionsRequest(IReadOnlyList<string> Permissions, string Reason);

/// <summary>A reason recorded for an administration change.</summary>
public sealed record PosReasonRequest(string Reason);

/// <summary>A role as shown to an administrator.</summary>
public sealed record PosRoleView(
    Guid Id,
    string Name,
    string Description,
    bool IsSystemRole,
    IReadOnlyList<string> Permissions,
    int MemberCount);

/// <summary>A catalogue permission as shown to an administrator.</summary>
public sealed record PosPermissionView(
    string Code,
    string Module,
    string Description,
    bool IsOfflineCapable,
    bool IsReadOnly,
    bool IsPrivileged);

// ---- Devices ----

/// <summary>A registered terminal as shown in the fleet console.</summary>
public sealed record PosDeviceSummary(
    Guid Id,
    string ShortCode,
    string Name,
    Guid LocationId,
    int Platform,
    int Status,
    string? AppVersion,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    decimal? ClockSkewSeconds,
    string? StatusReason);

/// <summary>The body used to register a terminal.</summary>
public sealed record PosRegisterDeviceRequest(
    string ShortCode,
    string Name,
    Guid LocationId,
    int Platform);

/// <summary>The one-time result of registering or reissuing a terminal enrolment.</summary>
public sealed record PosDeviceRegistration(
    Guid DeviceId,
    string ShortCode,
    string? EnrolmentCode,
    DateTimeOffset? ExpiresAtUtc);

// ---- Locations ----

/// <summary>Operational policy values attached to one physical location.</summary>
public sealed record PosLocationSettings(
    int NegativeStockPolicy,
    bool AllowsDirectSupplierDelivery,
    TimeSpan OfflineGracePeriod,
    string ReceiptHeader,
    string ReceiptFooter,
    decimal VatRate,
    decimal CashRoundingIncrement,
    int ExpiryWarningDays,
    decimal CashVarianceThreshold,
    TimeSpan MaxShiftHours);

/// <summary>A location with its operating policy.</summary>
public sealed record PosLocationAdmin(
    Guid Id,
    string Code,
    string Name,
    int Kind,
    string TimeZoneId,
    bool IsActive,
    bool IsSystemCreated,
    DateOnly OpenedOn,
    DateOnly? ClosedOn,
    PosLocationSettings Settings);

/// <summary>The body used to add a physical location.</summary>
public sealed record PosCreateLocationRequest(
    string Code,
    string Name,
    int Kind,
    string TimeZoneId,
    PosLocationSettings? Settings = null);

// ---- Reports ----

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

// ---- Stock adjustments ----

/// <summary>A stock adjustment as listed.</summary>
public sealed record PosStockAdjustmentSummary(
    Guid Id,
    string Number,
    Guid LocationId,
    string Reason,
    string Status,
    decimal TotalAbsoluteValue,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc);

/// <summary>A stock adjustment with its lines.</summary>
public sealed record PosStockAdjustmentDetail(
    PosStockAdjustmentSummary Adjustment,
    string? Notes,
    DateTimeOffset? SubmittedAtUtc,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAtUtc,
    string? RejectionReason,
    Guid? ReversedByUserId,
    DateTimeOffset? ReversedAtUtc,
    string? ReversalReason,
    IReadOnlyList<PosStockAdjustmentLineView> Lines);

/// <summary>One line of a stock adjustment.</summary>
public sealed record PosStockAdjustmentLineView(
    int LineNo,
    Guid ProductId,
    Guid? BatchId,
    string State,
    decimal QuantityDelta,
    decimal UnitCost,
    decimal AbsoluteValue,
    string MovementType);

/// <summary>One line change on a new stock adjustment.</summary>
public sealed record PosStockAdjustmentLineRequest(
    Guid ProductId, int State, decimal QuantityDelta, Guid? BatchId = null);

/// <summary>The body that raises a draft stock adjustment.</summary>
public sealed record PosCreateStockAdjustmentRequest(
    Guid LocationId,
    int Reason,
    IReadOnlyList<PosStockAdjustmentLineRequest> Lines,
    string? Notes = null);

// ---- Quarantine ----

/// <summary>A quarantine incident as listed.</summary>
public sealed record PosQuarantineIncidentSummary(
    Guid Id,
    string Number,
    string Status,
    Guid LocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? InvestigatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    decimal TotalValue,
    int LineCount,
    int OpenLineCount);

/// <summary>One line of a quarantine incident.</summary>
public sealed record PosQuarantineLineSummary(
    int LineNo,
    string Barcode,
    decimal Quantity,
    decimal UnitCost,
    decimal RemainingQuantity,
    string? ClaimedProductName,
    Guid? ProductId,
    Guid? BatchId,
    string Disposition,
    Guid? DispositionedByUserId,
    DateTimeOffset? DispositionedAtUtc,
    string? DispositionNote);

/// <summary>One photograph of an incident, without the image bytes.</summary>
public sealed record PosQuarantinePhotoSummary(
    Guid Id,
    string FileName,
    string ContentType,
    string? Note,
    Guid UploadedByUserId,
    DateTimeOffset UploadedAtUtc);

/// <summary>One step in an incident's audit timeline.</summary>
public sealed record PosQuarantineEventSummary(
    int Sequence,
    string Kind,
    Guid ActorUserId,
    DateTimeOffset OccurredAtUtc,
    decimal? Quantity,
    string? Note);

/// <summary>A quarantine incident with its lines, photos and timeline.</summary>
public sealed record PosQuarantineIncidentDetail(
    Guid Id,
    string Number,
    string Status,
    Guid LocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? InvestigatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    string? Note,
    decimal TotalValue,
    IReadOnlyList<PosQuarantineLineSummary> Lines,
    IReadOnlyList<PosQuarantinePhotoSummary> Photos,
    IReadOnlyList<PosQuarantineEventSummary> Timeline);

/// <summary>One found line on a new quarantine incident.</summary>
public sealed record PosQuarantineLineRequest(
    string Barcode, decimal Quantity, decimal? UnitCost = null, string? ClaimedProductName = null);

/// <summary>The body that raises a quarantine incident.</summary>
public sealed record PosCreateQuarantineIncidentRequest(
    Guid LocationId, IReadOnlyList<PosQuarantineLineRequest> Lines, string? Note = null);

/// <summary>The body that identifies a quarantine line against a catalogue product.</summary>
public sealed record PosLinkQuarantineProductRequest(
    int LineNo, Guid ProductId, Guid? BatchId = null, string? Note = null);

/// <summary>The body of a release or reject disposition.</summary>
public sealed record PosQuarantineQuantityRequest(int LineNo, decimal Quantity, string? Note = null);

/// <summary>The body of a write-off disposition.</summary>
public sealed record PosWriteOffQuarantineLineRequest(
    int LineNo, decimal Quantity, int ReasonCode, string? Note = null);

/// <summary>A note attached to moving an incident into investigation.</summary>
public sealed record PosQuarantineNoteRequest(string? Note = null);

// ---- Replenishment & inventory exceptions ----

/// <summary>A suggested restock for one product at one location.</summary>
public sealed record PosReplenishmentRecommendation(
    Guid LocationId,
    Guid ProductId,
    decimal Available,
    decimal Deficit,
    string Urgency,
    Guid? SuggestedSourceLocationId,
    decimal SuggestedQuantity);

/// <summary>One stock draw the ledger refused.</summary>
public sealed record PosNegativeStockAttemptView(
    Guid Id,
    DateTimeOffset AttemptedAtUtc,
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    Guid? BatchId,
    string State,
    string MovementType,
    decimal RequestedQuantity,
    decimal AvailableQuantity,
    decimal Shortfall,
    string Policy,
    string ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    string ReferenceNumber,
    Guid UserId,
    Guid? DeviceId,
    Guid CorrelationId);

/// <summary>Refused draws for one product at one location, ranked by frequency.</summary>
public sealed record PosNegativeStockAttemptSummaryRow(
    Guid LocationId,
    string? LocationCode,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    int Attempts,
    decimal TotalShortfall,
    DateTimeOffset FirstAttemptAtUtc,
    DateTimeOffset LastAttemptAtUtc);
