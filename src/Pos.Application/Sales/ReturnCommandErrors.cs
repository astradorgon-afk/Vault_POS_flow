using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Errors raised while accepting a customer return. Domain-shape errors live
/// on <see cref="SalesReturnErrors"/>; these cover the facts the handler
/// re-derives — that the sale exists, that the request matches its location,
/// device and shift, and that the requested lines exist.
/// </summary>
internal static class ReturnCommandErrors
{
    /// <summary>The return carries no device-scoped RET number.</summary>
    public static Error NumberInvalid => Error.Validation(
        "sale.return.number_invalid",
        "A return must carry the device-allocated RET number it was accepted under.");

    /// <summary>The number's device code does not match the device accepting the return.</summary>
    public static Error NumberDeviceMismatch => Error.Conflict(
        "sale.return.number_device_mismatch",
        "The device code in the RET number does not match the device that accepted the return.");

    /// <summary>The return names no sale.</summary>
    public static Error SaleRequired => Error.Validation(
        "sale.return.sale_required",
        "A return must name the sale the goods were bought under.");

    /// <summary>The sale named by the command does not exist.</summary>
    /// <param name="saleId">The missing sale.</param>
    /// <returns>The error.</returns>
    public static Error SaleNotFound(SaleId saleId) => Error.NotFound(
        "sale.return.sale_not_found",
        FormattableString.Invariant($"No sale has the identifier {saleId}."));

    /// <summary>The return acts on a sale that does not belong to the location.</summary>
    /// <param name="returnId">The return.</param>
    /// <param name="locationId">The location the return was requested at.</param>
    /// <returns>The error.</returns>
    public static Error LocationMismatch(SalesReturnId returnId, LocationId locationId) => Error.Conflict(
        "sale.return.location_mismatch",
        FormattableString.Invariant($"Return {returnId} does not belong to location {locationId.Value}."));

    /// <summary>A return line references a product that is not on the original sale.</summary>
    /// <param name="productId">The unknown product.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "sale.return.product_unknown",
        FormattableString.Invariant($"The original sale has no line for product {productId.Value}."));

    /// <summary>A return line references an empty product.</summary>
    public static Error ItemProductRequired => Error.Validation(
        "sale.return.item.product_required",
        "A return line must name the product being accepted back.");

    /// <summary>The return asks for more of a product than the sale still holds unreturned.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="available">The total returnable quantity across all its sale lines.</param>
    /// <param name="requested">The quantity requested.</param>
    /// <returns>The error.</returns>
    public static Error QuantityExceedsAvailable(
        ProductId productId,
        decimal available,
        decimal requested) => Error.Conflict(
        "sale.return.quantity_exceeds_available",
        FormattableString.Invariant(
            $"The sale still has {available} units of product {productId.Value} available to return; {requested} were requested."),
        new Dictionary<string, object?>
        {
            ["productId"] = productId.Value,
            ["available"] = available,
            ["requested"] = requested,
        });

    /// <summary>A return line references a product that has no returnable sale lines.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error NoReturnableLines(ProductId productId) => Error.NotFound(
        "sale.return.no_returnable_lines",
        FormattableString.Invariant(
            $"Product {productId.Value} has no lines on the sale that still hold returnable units."));
}