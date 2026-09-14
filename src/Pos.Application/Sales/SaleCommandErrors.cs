using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Errors raised while completing a sale. Domain-shape errors live on
/// <see cref="Pos.Domain.Sales.SaleErrors"/>; these cover the facts the handler
/// re-derives — the location, the products, the selling prices and the
/// authority to discount, override or sell past expiry.
/// </summary>
internal static class SaleCommandErrors
{
    /// <summary>The location named by the command does not exist.</summary>
    public static Error LocationUnknown(LocationId locationId) => Error.NotFound(
        "sale.location_unknown",
        FormattableString.Invariant($"Unknown location {locationId.Value}."));

    /// <summary>A sale cannot happen at an external counterparty location.</summary>
    public static Error LocationExternal => Error.Conflict(
        "sale.location_external",
        "A sale cannot happen at an external counterparty location.");

    /// <summary>The location's VAT configuration would make every vatable line fail.</summary>
    public static Error VatRateInvalid(LocationId locationId) => Error.Conflict(
        "sale.vat_rate_invalid",
        FormattableString.Invariant(
            $"Location {locationId.Value} must configure a positive VAT rate before sales can be completed."));

    /// <summary>The requested product does not exist.</summary>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "sale.product_unknown",
        FormattableString.Invariant($"Unknown product {productId.Value}."));

    /// <summary>The product is no longer sold.</summary>
    public static Error ProductInactive(ProductId productId) => Error.Conflict(
        "sale.product_inactive",
        FormattableString.Invariant($"Product {productId.Value} is inactive and cannot be sold."));

    /// <summary>No selling price is in effect for the product at the location.</summary>
    public static Error PriceMissing(ProductId productId, LocationId locationId) => Error.Conflict(
        "sale.item.price_missing",
        FormattableString.Invariant(
            $"No selling price is in effect for product {productId.Value} at location {locationId.Value}."),
        new Dictionary<string, object?>
        {
            ["productId"] = productId.Value,
            ["locationId"] = locationId.Value,
        });

    /// <summary>The recorded discount authorizer lacks the sale.discount permission.</summary>
    public static Error DiscountNotAuthorized => Error.Forbidden(
        "sale.discount_not_authorized",
        "The user recorded as discount authorizer does not hold the sale.discount permission.");

    /// <summary>The recorded price-override authorizer lacks the sale.price_override permission.</summary>
    public static Error PriceOverrideNotAuthorized => Error.Forbidden(
        "sale.price_override_not_authorized",
        "The user recorded as price-override authorizer does not hold the sale.price_override permission.");

    /// <summary>The cashier lacks the authority to sell from an expired batch.</summary>
    public static Error ExpiredOverrideDenied => Error.Forbidden(
        "sale.expired_override_denied",
        "Selling from an expired batch requires the sale.expired_override permission.");

    /// <summary>The EXT-CUSTOMER counterparty location has not been provisioned.</summary>
    public static Error ExternalCustomerLocationMissing => Error.Conflict(
        "sale.external_customer_missing",
        "The EXT-CUSTOMER counterparty location has not been provisioned; a sale cannot be posted without it.");
}