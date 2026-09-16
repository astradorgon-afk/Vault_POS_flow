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
    bool AllowExpiredOverride);

/// <summary>One payment that settles a sale. Method is the numeric
/// <c>PaymentMethod</c> value: <c>1</c> cash, <c>2</c> card, <c>3</c> e-wallet.</summary>
public sealed record PosPayment(
    int Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);
