using System.Globalization;
using Pos.Shared.Scanning;

namespace Pos.Web.Components.Pages;

/// <summary>What asking the catalogue to identify a code produced.</summary>
public enum BarcodeLookupOutcome
{
    /// <summary>Nothing was asked: the box was empty.</summary>
    Empty = 0,

    /// <summary>A product answers to the code.</summary>
    Known = 1,

    /// <summary>
    /// No product answers to it. Not a failure — it is the case this screen
    /// exists to resolve.
    /// </summary>
    Unregistered = 2,

    /// <summary>The lookup itself failed, so nothing is known either way.</summary>
    Failed = 3,
}

/// <summary>
/// Wording and reading for the catalogue screens: what a scanned code came back
/// as, and whether a hand-typed one looks mistyped.
/// </summary>
public static class CatalogView
{
    /// <summary>
    /// Reads the result of a barcode lookup.
    /// </summary>
    /// <remarks>
    /// A retired code is treated exactly as an unknown one, matching the API,
    /// which answers the same for both so the register and receiving route them
    /// down the same path. Here it means the operator is offered the same
    /// choice: attach the code to a product, or create one for it.
    /// </remarks>
    /// <param name="code">The cleaned code that was asked about.</param>
    /// <param name="succeeded">Whether a product came back.</param>
    /// <param name="errorCode">The API's error code, when it did not.</param>
    /// <returns>What the answer means for the screen.</returns>
    public static BarcodeLookupOutcome Classify(string? code, bool succeeded, string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return BarcodeLookupOutcome.Empty;
        }

        if (succeeded)
        {
            return BarcodeLookupOutcome.Known;
        }

        return errorCode is "catalog.barcode_unknown" or "catalog.barcode_retired"
            ? BarcodeLookupOutcome.Unregistered
            : BarcodeLookupOutcome.Failed;
    }

    /// <summary>
    /// Warns when a code has the shape of a retail barcode but fails its own
    /// check digit.
    /// </summary>
    /// <remarks>
    /// A scanner that read the code got it right or did not read it at all; the
    /// mistake this catches is a human copying digits off a label. Registering
    /// a mistyped code is quietly expensive — it will never scan, and the right
    /// code stays unregistered — so it is worth a word before saving. It is a
    /// warning and not a block, because own-brand and internal codes are not
    /// GTINs and have no check digit to satisfy.
    /// </remarks>
    /// <param name="code">The code as typed.</param>
    /// <returns>A sentence to show, or <see langword="null"/> when nothing looks wrong.</returns>
    public static string? CheckDigitWarning(string? code)
    {
        string cleaned = BarcodeText.Clean(code);
        if (cleaned.Length is not (8 or 12 or 13 or 14) || !cleaned.All(char.IsAsciiDigit))
        {
            return null;
        }

        return BarcodeText.IsValidGtin(cleaned)
            ? null
            : FormattableString.Invariant(
                $"This looks like a {cleaned.Length}-digit retail barcode, but its check digit does not match. Worth re-reading the label before saving it.");
    }

    /// <summary>
    /// Says in words what one scan of a code will add to stock.
    /// </summary>
    /// <param name="packQuantity">Base units per scan.</param>
    /// <param name="unitName">What the product's base unit is called.</param>
    /// <returns>For example, "one scan counts as 24 EA".</returns>
    public static string PackSummary(decimal packQuantity, string unitName)
        => packQuantity == 1m
            ? FormattableString.Invariant($"One scan counts as 1 {unitName}.")
            : FormattableString.Invariant($"One scan counts as {Quantity(packQuantity)} {unitName}.");

    /// <summary>Quantities without trailing zeros: 24, not 24.000.</summary>
    public static string Quantity(decimal value)
        => value % 1m == 0m
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
}
