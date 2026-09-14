using System.Globalization;
using System.Text.RegularExpressions;

namespace Pos.Domain.Common;

/// <summary>
/// Document types that carry a human-readable number. The numeric value is
/// persisted in the counter table, so it must never be reordered or reused.
/// </summary>
public enum DocumentType
{
    /// <summary>Purchase order, prefix <c>PO</c>.</summary>
    PurchaseOrder = 1,

    /// <summary>Goods receipt note, prefix <c>GRN</c>.</summary>
    GoodsReceipt = 2,

    /// <summary>Transfer order, prefix <c>TRF</c>.</summary>
    TransferOrder = 3,

    /// <summary>Sale, prefix <c>SAL</c>. Device-scoped.</summary>
    Sale = 4,

    /// <summary>Customer return, prefix <c>RET</c>. Device-scoped.</summary>
    SalesReturn = 5,

    /// <summary>Stock adjustment, prefix <c>ADJ</c>.</summary>
    StockAdjustment = 6,

    /// <summary>Inventory count, prefix <c>CNT</c>.</summary>
    InventoryCount = 7,

    /// <summary>Quarantine incident, prefix <c>QRT</c>.</summary>
    QuarantineIncident = 8,

    /// <summary>Cashier shift, prefix <c>SHF</c>. Device-scoped.</summary>
    CashierShift = 9,

    /// <summary>Supplier return, prefix <c>SRT</c>.</summary>
    SupplierReturn = 10,

    /// <summary>Transfer shipment, prefix <c>SHP</c>.</summary>
    TransferShipment = 11,

    /// <summary>Transfer receipt, prefix <c>TRC</c>.</summary>
    TransferReceipt = 12,

    /// <summary>Pre-approval token, prefix <c>PAT</c>.</summary>
    PreApprovalToken = 13,

    /// <summary>Payment receipt, prefix <c>RCT</c>.</summary>
    Receipt = 14,

    /// <summary>Expiry quarantine run, prefix <c>EXP</c>.</summary>
    ExpiryRun = 15,
}

/// <summary>
/// A human-readable business document number such as <c>TRF-2026-000001</c> or,
/// for documents a device must number without contacting the server,
/// <c>SAL-2026-D03-000812</c>.
/// </summary>
/// <remarks>
/// The number is for humans. Joins, references and idempotency always use the
/// document's <see cref="Guid"/> identity, never this string.
/// </remarks>
public readonly partial record struct DocumentNumber
{
    /// <summary>Gets the longest value the format can represent, used to bound
    /// reference fields that store a document number.</summary>
    public const int MaxLength = 24;

    private DocumentNumber(string value) => Value = value;

    /// <summary>Gets the formatted number.</summary>
    public string Value { get; }

    /// <summary>
    /// Gets the device short code embedded in a device-scoped number, or
    /// <see langword="null"/> for a centrally numbered document type. Used by
    /// the server to verify that a posted number was issued by the device that
    /// claims it.
    /// </summary>
    public string? DeviceShortCode
    {
        get
        {
            if (Value is null)
            {
                return null;
            }

            string[] parts = Value.Split('-');
            return parts.Length == 4 ? parts[2] : null;
        }
    }

    /// <summary>Returns the three-letter prefix used for a document type.</summary>
    /// <param name="type">The document type.</param>
    /// <returns>The prefix.</returns>
    public static string PrefixFor(DocumentType type) => type switch
    {
        DocumentType.PurchaseOrder => "PO",
        DocumentType.GoodsReceipt => "GRN",
        DocumentType.TransferOrder => "TRF",
        DocumentType.Sale => "SAL",
        DocumentType.SalesReturn => "RET",
        DocumentType.StockAdjustment => "ADJ",
        DocumentType.InventoryCount => "CNT",
        DocumentType.QuarantineIncident => "QRT",
        DocumentType.CashierShift => "SHF",
        DocumentType.SupplierReturn => "SRT",
        DocumentType.TransferShipment => "SHP",
        DocumentType.TransferReceipt => "TRC",

        DocumentType.PreApprovalToken => "PAT",
        DocumentType.Receipt => "RCT",
        DocumentType.ExpiryRun => "EXP",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type."),
    };

    /// <summary>
    /// Gets a value indicating whether a document type is numbered by the device
    /// rather than centrally, so it can be issued while offline.
    /// </summary>
    /// <param name="type">The document type.</param>
    /// <returns><see langword="true"/> when the number is device-scoped.</returns>
    public static bool IsDeviceScoped(DocumentType type)
        => type is DocumentType.Sale or DocumentType.SalesReturn or DocumentType.CashierShift;

    /// <summary>Formats a centrally allocated number.</summary>
    /// <param name="type">The document type.</param>
    /// <param name="year">The calendar year of the period.</param>
    /// <param name="sequence">The allocated sequence value, starting at one.</param>
    /// <returns>The formatted number.</returns>
    public static DocumentNumber Create(DocumentType type, int year, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);

        string value = string.Create(
            CultureInfo.InvariantCulture,
            $"{PrefixFor(type)}-{year:D4}-{sequence:D6}");

        return new DocumentNumber(value);
    }

    /// <summary>Formats a device-scoped number that can be issued while offline.</summary>
    /// <param name="type">The document type.</param>
    /// <param name="year">The calendar year of the period.</param>
    /// <param name="deviceShortCode">The device short code, for example <c>D03</c>.</param>
    /// <param name="sequence">The device-local sequence value, starting at one.</param>
    /// <returns>The formatted number.</returns>
    public static DocumentNumber CreateForDevice(
        DocumentType type,
        int year,
        string deviceShortCode,
        long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceShortCode);

        string code = deviceShortCode.Trim().ToUpperInvariant();

        if (!DeviceCodePattern().IsMatch(code))
        {
            throw new ArgumentException(
                "A device short code must be two to six upper-case letters or digits.",
                nameof(deviceShortCode));
        }

        int digits = type == DocumentType.CashierShift ? 4 : 6;
        string value = string.Create(
            CultureInfo.InvariantCulture,
            $"{PrefixFor(type)}-{year:D4}-{code}-{sequence.ToString(new string('0', digits), CultureInfo.InvariantCulture)}");

        return new DocumentNumber(value);
    }

    /// <summary>Validates and wraps an existing number.</summary>
    /// <param name="value">The raw input.</param>
    /// <returns>The document number, or a validation failure.</returns>
    public static Result<DocumentNumber> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<DocumentNumber>.Failure(
                Error.Validation("document.number_required", "A document number is required."));
        }

        string normalised = value.Trim().ToUpperInvariant();

        return NumberPattern().IsMatch(normalised)
            ? Result<DocumentNumber>.Success(new DocumentNumber(normalised))
            : Result<DocumentNumber>.Failure(Error.Validation(
                "document.number_invalid_format",
                "A document number must look like PREFIX-YYYY-NNNNNN or PREFIX-YYYY-DEVICE-NNNNNN."));
    }

    /// <summary>
    /// Rehydrates a value already validated on the way in, such as from the database.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The document number.</returns>
    public static DocumentNumber FromTrustedSource(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Z]{2,3}-\d{4}(-[A-Z0-9]{2,6})?-\d{4,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^[A-Z0-9]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCodePattern();
}
