using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>The catalog's error catalogue. Codes are part of the API contract.</summary>
public static class CatalogErrors
{
    /// <summary>A barcode was attached that already belongs to the same product.</summary>
    /// <param name="barcode">The duplicated barcode.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateBarcode(string barcode) => Error.Conflict(
        "catalog.barcode_already_attached",
        FormattableString.Invariant($"The barcode {barcode} is already attached to this product."),
        new Dictionary<string, object?> { ["barcode"] = barcode });

    /// <summary>A price period overlaps an existing price for the same scope.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error OverlappingPrice(ProductId productId) => Error.Conflict(
        "catalog.price_overlap",
        "A price is already in effect for this product and scope during the requested period.",
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>A referenced supplier or unit no longer exists.</summary>
    /// <param name="code">The stable code.</param>
    /// <returns>The error.</returns>
    public static Error ReferenceMissing(string code) => Error.Conflict(code, "The referenced master data does not exist.");

    /// <summary>A master-data code or unique name is already in use.</summary>
    /// <param name="kind">The master data kind, for example <c>category</c>.</param>
    /// <param name="value">The duplicated value.</param>
    /// <returns>The error.</returns>
    public static Error CodeTaken(string kind, string value) => Error.Conflict(
        FormattableString.Invariant($"catalog.{kind}_already_exists"),
        FormattableString.Invariant($"The value '{value}' is already in use."),
        new Dictionary<string, object?> { ["value"] = value });
}