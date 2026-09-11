using System.Text.RegularExpressions;
using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>Barcode symbologies the system recognises.</summary>
public enum BarcodeSymbology
{
    /// <summary>Unknown or supplier-specific symbology; no checksum is enforced.</summary>
    Unknown = 0,

    /// <summary>EAN-8, eight numeric digits with a check digit.</summary>
    Ean8 = 1,

    /// <summary>EAN-13, thirteen numeric digits with a check digit.</summary>
    Ean13 = 2,

    /// <summary>UPC-A, twelve numeric digits with a check digit.</summary>
    UpcA = 3,

    /// <summary>Code 128, variable length alphanumeric.</summary>
    Code128 = 4,

    /// <summary>Internally assigned code, typically for loose or in-store packed goods.</summary>
    Internal = 5,
}

/// <summary>
/// A scannable product code. Barcodes are globally unique across the catalog:
/// attaching one that already belongs to another product is rejected rather
/// than silently repointed, because a mis-pointed barcode silently corrupts
/// both stock and revenue for two products at once.
/// </summary>
public readonly partial record struct Barcode
{
    /// <summary>Minimum permitted length.</summary>
    public const int MinLength = 4;

    /// <summary>Maximum permitted length.</summary>
    public const int MaxLength = 48;

    private Barcode(string value, BarcodeSymbology symbology)
    {
        Value = value;
        Symbology = symbology;
    }

    /// <summary>Gets the normalised barcode value.</summary>
    public string Value { get; }

    /// <summary>Gets the detected symbology.</summary>
    public BarcodeSymbology Symbology { get; }

    /// <summary>Parses, normalises and validates a scanned or typed barcode.</summary>
    /// <param name="value">The raw input.</param>
    /// <param name="requireChecksum">
    /// When true, a value whose length matches EAN/UPC must also carry a valid
    /// check digit. Receiving and product setup use this; a POS scan does not,
    /// because an unrecognised code must reach the quarantine workflow rather
    /// than being rejected as malformed.
    /// </param>
    /// <returns>The barcode, or a validation failure.</returns>
    public static Result<Barcode> Create(string? value, bool requireChecksum = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<Barcode>.Failure(
                Error.Validation("catalog.barcode_required", "A barcode is required."));
        }

        string normalised = value.Trim().ToUpperInvariant();

        if (normalised.Length is < MinLength or > MaxLength)
        {
            return Result<Barcode>.Failure(Error.Validation(
                "catalog.barcode_invalid_length",
                FormattableString.Invariant(
                    $"A barcode must be between {MinLength} and {MaxLength} characters.")));
        }

        if (!BarcodePattern().IsMatch(normalised))
        {
            return Result<Barcode>.Failure(Error.Validation(
                "catalog.barcode_invalid_characters",
                "A barcode may contain only letters, digits and hyphen."));
        }

        BarcodeSymbology symbology = DetectSymbology(normalised);

        if (requireChecksum && IsGtin(symbology) && !HasValidCheckDigit(normalised))
        {
            return Result<Barcode>.Failure(Error.Validation(
                "catalog.barcode_checksum_failed",
                "The barcode check digit is not valid for its symbology."));
        }

        return Result<Barcode>.Success(new Barcode(normalised, symbology));
    }

    /// <summary>
    /// Rehydrates a value already validated on the way in, such as from the database.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The barcode.</returns>
    public static Barcode FromTrustedSource(string value) => new(value, DetectSymbology(value));

    /// <summary>Verifies the GS1 modulo-10 check digit.</summary>
    /// <param name="value">A numeric GTIN-8, GTIN-12 or GTIN-13 string.</param>
    /// <returns><see langword="true"/> when the check digit is correct.</returns>
    public static bool HasValidCheckDigit(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length is not (8 or 12 or 13) || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        int sum = 0;
        int weight = 3;

        for (int i = value.Length - 2; i >= 0; i--)
        {
            sum += (value[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        int expected = (10 - (sum % 10)) % 10;
        return expected == value[^1] - '0';
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsGtin(BarcodeSymbology symbology)
        => symbology is BarcodeSymbology.Ean8 or BarcodeSymbology.Ean13 or BarcodeSymbology.UpcA;

    private static BarcodeSymbology DetectSymbology(string value)
    {
        if (!value.All(char.IsAsciiDigit))
        {
            return BarcodeSymbology.Code128;
        }

        return value.Length switch
        {
            8 => BarcodeSymbology.Ean8,
            12 => BarcodeSymbology.UpcA,
            13 => BarcodeSymbology.Ean13,
            _ => BarcodeSymbology.Unknown,
        };
    }

    [GeneratedRegex(@"^[A-Z0-9-]{4,48}$", RegexOptions.CultureInvariant)]
    private static partial Regex BarcodePattern();
}
