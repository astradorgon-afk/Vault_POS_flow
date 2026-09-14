using Pos.Domain.Common;
using Pos.Domain.Sales;

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

    /// <summary>The sale carries no device-scoped SAL number.</summary>
    public static Error NumberInvalid => Error.Validation(
        "sale.number_invalid",
        "A sale must carry the device-allocated SAL number it was rung up under.");

    /// <summary>The number's device code does not match the device posting it.</summary>
    public static Error NumberDeviceMismatch => Error.Conflict(
        "sale.number_device_mismatch",
        "The device code in the SAL number does not match the device that completed the sale.");

    /// <summary>The device named by the command does not exist.</summary>
    public static Error DeviceUnknown(DeviceId deviceId) => Error.NotFound(
        "sale.device_unknown",
        FormattableString.Invariant($"Unknown device {deviceId.Value}."));

    /// <summary>The shift named by the command does not exist.</summary>
    public static Error ShiftUnknown(CashierShiftId shiftId) => Error.NotFound(
        "sale.shift_unknown",
        FormattableString.Invariant($"No shift has the identifier {shiftId}."));

    /// <summary>The shift is no longer open and cannot accept sales.</summary>
    public static Error ShiftNotOpen(ShiftStatus status) => Error.Conflict(
        "sale.shift_not_open",
        FormattableString.Invariant($"The shift must be open to accept sales; it is {status}."),
        new Dictionary<string, object?> { ["status"] = status.ToString() });

    /// <summary>The cashier does not own the shift the sale is attached to.</summary>
    public static Error ShiftCashierMismatch => Error.Conflict(
        "sale.shift_cashier_mismatch",
        "A sale can only be completed into a shift opened by the same cashier.");

    /// <summary>The device does not own the shift the sale is attached to.</summary>
    public static Error ShiftDeviceMismatch => Error.Conflict(
        "sale.shift_device_mismatch",
        "A sale can only be completed on the device the shift was opened on.");
}