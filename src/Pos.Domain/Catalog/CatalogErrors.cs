using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>The catalog's error catalogue. Codes are part of the API contract.</summary>
public static class CatalogErrors
{
    /// <summary>A referenced product does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId id) => Error.NotFound(
        "catalog.product_unknown",
        FormattableString.Invariant($"Product {id.Value} was not found."));

    /// <summary>No product was found for the given barcode.</summary>
    /// <param name="barcode">The barcode that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error BarcodeUnknown(string barcode) => Error.NotFound(
        "catalog.barcode_unknown",
        FormattableString.Invariant($"No product is registered under barcode '{barcode}'."));

    /// <summary>
    /// A barcode was attached that already belongs to a product. Barcodes are
    /// globally unique, so this covers the same product and any other.
    /// </summary>
    /// <param name="barcode">The duplicated barcode.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateBarcode(string barcode) => Error.Conflict(
        "catalog.barcode_already_attached",
        FormattableString.Invariant($"The barcode {barcode} is already attached to a product."),
        new Dictionary<string, object?> { ["barcode"] = barcode });

    /// <summary>The barcode was retired; it stays reserved and cannot be used again.</summary>
    /// <param name="barcode">The retired barcode.</param>
    /// <returns>The error.</returns>
    public static Error BarcodeRetired(string barcode) => Error.Conflict(
        "catalog.barcode_retired",
        FormattableString.Invariant($"The barcode {barcode} has been retired and cannot be used again."),
        new Dictionary<string, object?> { ["barcode"] = barcode });

    /// <summary>The product has no barcode with this value.</summary>
    /// <param name="barcode">The barcode that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error BarcodeNotAttached(string barcode) => Error.NotFound(
        "catalog.barcode_not_attached",
        FormattableString.Invariant($"The barcode '{barcode}' is not attached to this product."));

    /// <summary>A price was scheduled to start in the past.</summary>
    public static readonly Error PriceBackdated = Error.Validation(
        "catalog.price_backdated",
        "A price cannot start in the past; past prices are history and never rewritten.");

    /// <summary>The product is already in the requested activation state.</summary>
    /// <param name="active">The state that was requested.</param>
    /// <returns>The error.</returns>
    public static Error ActivationUnchanged(bool active) => Error.Conflict(
        active ? "catalog.product_already_active" : "catalog.product_already_inactive",
        active ? "The product is already active." : "The product is already inactive.");

    /// <summary>A conversion between the same two units already exists.</summary>
    public static readonly Error ConversionExists = Error.Conflict(
        "catalog.conversion_exists",
        "The product already has a conversion between these units.");

    /// <summary>The product has no conversion with this identifier.</summary>
    public static readonly Error ConversionUnknown = Error.NotFound(
        "catalog.conversion_unknown",
        "The unit conversion was not found on this product.");

    /// <summary>The supplier is not linked to the product.</summary>
    public static readonly Error SupplierNotLinked = Error.NotFound(
        "catalog.supplier_not_linked",
        "The supplier is not linked to this product.");

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