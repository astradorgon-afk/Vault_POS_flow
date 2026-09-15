using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// The application-level failures of a blind return acceptance, on top of what
/// the domain (<see cref="Pos.Domain.Sales.SalesReturnErrors"/>) and the
/// pipeline behaviours already reject.
/// </summary>
public static class BlindReturnCommandErrors
{
    /// <summary>A blind return references a product the catalog does not know.</summary>
    /// <param name="productId">The unknown product.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "sale.return.blind.product_unknown",
        FormattableString.Invariant($"Product {productId} does not exist."));

    /// <summary>A blind return line's current selling price could not be resolved.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="locationId">The location.</param>
    /// <returns>The error.</returns>
    public static Error PriceMissing(ProductId productId, LocationId locationId) => Error.Conflict(
        "sale.return.blind.price_missing",
        FormattableString.Invariant($"Product {productId} has no selling price at location {locationId}."));
}