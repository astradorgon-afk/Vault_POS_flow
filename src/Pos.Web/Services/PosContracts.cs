namespace Pos.Web.Services;

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
    /// <summary>Gets or sets the location override, or null for the organization price.</summary>
    public Guid? LocationId { get; set; }

    /// <summary>Gets or sets the selling amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets whether the row is effective now.</summary>
    public bool IsCurrent { get; set; }
}

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
