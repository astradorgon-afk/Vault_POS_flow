using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// The expected business failures a sale can produce. Codes are part of the API
/// contract; messages may change.
/// </summary>
public static class SaleErrors
{
    /// <summary>The sale references an empty event identifier.</summary>
    public static Error EventRequired { get; } = Error.Validation(
        "sale.event_required",
        "A sale must carry the event identifier that makes completion idempotent.");

    /// <summary>The sale references an empty location.</summary>
    public static Error LocationRequired { get; } = Error.Validation(
        "sale.location_required",
        "A sale must happen at a location.");

    /// <summary>The sale references an empty cashier shift.</summary>
    public static Error ShiftRequired { get; } = Error.Validation(
        "sale.shift_required",
        "A sale must belong to a cashier shift.");

    /// <summary>The sale references an empty device.</summary>
    public static Error DeviceRequired { get; } = Error.Validation(
        "sale.device_required",
        "A sale must happen on a registered device.");

    /// <summary>The sale references an empty business date.</summary>
    public static Error BusinessDateRequired { get; } = Error.Validation(
        "sale.business_date_required",
        "A sale must carry the business date it counts toward.");

    /// <summary>The sale references the default completion timestamp.</summary>
    public static Error CompletedAtRequired { get; } = Error.Validation(
        "sale.completed_at_required",
        "A sale must record when it was completed.");

    /// <summary>The sale references an empty cashier.</summary>
    public static Error CashierRequired { get; } = Error.Validation(
        "sale.cashier_required",
        "A sale must record the cashier who completed it.");

    /// <summary>The sale has no lines.</summary>
    public static Error ItemsRequired { get; } = Error.Validation(
        "sale.items_required",
        "A sale must have at least one line.");

    /// <summary>The sale has no payments.</summary>
    public static Error PaymentsRequired { get; } = Error.Validation(
        "sale.payments_required",
        "A sale must be paid in full.");

    /// <summary>The sum of the payment amounts does not equal the total due.</summary>
    /// <param name="expected">The total due.</param>
    /// <param name="actual">The sum of the payments.</param>
    /// <returns>The error.</returns>
    public static Error PaymentMismatch(decimal expected, decimal actual) => Error.Conflict(
        "sale.payment_mismatch",
        FormattableString.Invariant($"Payments of {actual} do not cover the total of {expected}."),
        new Dictionary<string, object?> { ["expected"] = expected, ["actual"] = actual });

    /// <summary>A line references an empty product.</summary>
    public static Error ItemProductRequired { get; } = Error.Validation(
        "sale.item.product_required",
        "A sale line must reference a product.");

    /// <summary>A line's product name snapshot is missing or too long.</summary>
    /// <param name="maxLength">The product name limit.</param>
    /// <returns>The error.</returns>
    public static Error ItemProductNameInvalid(int maxLength) => Error.Validation(
        "sale.item.product_name_invalid",
        FormattableString.Invariant($"A sale line needs the product name, at most {maxLength} characters."));

    /// <summary>A line's barcode snapshot is too long.</summary>
    /// <param name="maxLength">The barcode limit.</param>
    /// <returns>The error.</returns>
    public static Error ItemBarcodeTooLong(int maxLength) => Error.Validation(
        "sale.item.barcode_too_long",
        FormattableString.Invariant($"A barcode snapshot may be at most {maxLength} characters."));

    /// <summary>A line's quantity is not positive.</summary>
    public static Error ItemQuantityInvalid { get; } = Error.Validation(
        "sale.item.quantity_invalid",
        "A sale line must sell more than zero units.");

    /// <summary>A line's unit price is negative.</summary>
    public static Error ItemPriceNegative { get; } = Error.Validation(
        "sale.item.price_negative",
        "A sale line cannot have a negative price.");

    /// <summary>A line's discount is negative or exceeds the line gross.</summary>
    public static Error ItemDiscountInvalid { get; } = Error.Validation(
        "sale.item.discount_invalid",
        "A discount must be non-negative and no more than the line total.");

    /// <summary>A line with a discount does not record who authorised it.</summary>
    public static Error ItemDiscountAuthorizerRequired { get; } = Error.Validation(
        "sale.item.discount_authorizer_required",
        "A line with a manual discount must record the user who authorised it.");

    /// <summary>A price-overridden line does not record who authorised it.</summary>
    public static Error ItemPriceOverrideAuthorizerRequired { get; } = Error.Validation(
        "sale.item.price_override_authorizer_required",
        "A price override must record the user who authorised it.");

    /// <summary>A line is missing the price version that resolved its price.</summary>
    public static Error ItemPriceVersionRequired { get; } = Error.Validation(
        "sale.item.price_version_required",
        "Every line stamps the effective price version that resolved its price.");

    /// <summary>A vatable line has no positive VAT rate.</summary>
    public static Error ItemVatRateMissing { get; } = Error.Validation(
        "sale.item.vat_rate_missing",
        "A vatable line must carry the VAT rate its price was computed with.");

    /// <summary>A line is marked both VAT-exempt and zero-rated.</summary>
    public static Error ItemVatClassConflict { get; } = Error.Validation(
        "sale.item.vat_class_conflict",
        "A line is either VAT-exempt or zero-rated, not both.");

    /// <summary>A batch-tracked product line has no allocated batch.</summary>
    public static Error ItemBatchMissing { get; } = Error.Validation(
        "sale.item.batch_missing",
        "A batch-tracked product must record the batch the units were sold from.");

    /// <summary>A product that does not track batches was given an allocated batch.</summary>
    public static Error ItemBatchUnexpected { get; } = Error.Validation(
        "sale.item.batch_unexpected",
        "A product that does not track batches cannot carry an allocated batch.");

    /// <summary>A payment method is unknown.</summary>
    public static Error PaymentMethodUnknown { get; } = Error.Validation(
        "sale.payment.method_unknown",
        "The payment method is not recognised.");

    /// <summary>A payment amount is not positive.</summary>
    public static Error PaymentAmountInvalid { get; } = Error.Validation(
        "sale.payment.amount_invalid",
        "A payment must settle a positive amount.");

    /// <summary>A cash payment has no tendered amount.</summary>
    public static Error PaymentTenderedRequired { get; } = Error.Validation(
        "sale.payment.tendered_required",
        "A cash payment must record the amount tendered.");

    /// <summary>A cash payment is tendered for less than the amount due.</summary>
    public static Error PaymentTenderedInsufficient { get; } = Error.Validation(
        "sale.payment.tendered_insufficient",
        "The amount tendered does not cover the payment.");

    /// <summary>The cash rounding increment is not positive.</summary>
    public static Error PaymentIncrementInvalid { get; } = Error.Validation(
        "sale.payment.increment_invalid",
        "The cash rounding increment must be positive.");

    /// <summary>A card or wallet payment's provider reference is too long.</summary>
    /// <param name="maxLength">The reference limit.</param>
    /// <returns>The error.</returns>
    public static Error PaymentReferenceTooLong(int maxLength) => Error.Validation(
        "sale.payment.reference_too_long",
        FormattableString.Invariant($"A provider reference may be at most {maxLength} characters."));

    /// <summary>Only a completed sale can be voided.</summary>
    public static Error VoidOnlyCompleted { get; } = Error.Conflict(
        "sale.void.only_completed",
        "Only a completed sale can be voided.");

    /// <summary>The void references a different cashier shift than the sale belongs to.</summary>
    public static Error VoidShiftMismatch { get; } = Error.Conflict(
        "sale.void.shift_mismatch",
        "A sale can only be voided against the shift it was completed in.");

    /// <summary>The void references a business date different from the sale's.</summary>
    public static Error VoidBusinessDateMismatch { get; } = Error.Conflict(
        "sale.void.business_date_mismatch",
        "A sale can only be voided against the business date it was completed on.");

    /// <summary>The void has no timestamp.</summary>
    public static Error VoidStampRequired { get; } = Error.Validation(
        "sale.void.stamp_required",
        "A void must record when it happened.");

    /// <summary>The void has no authorising user.</summary>
    public static Error VoidByRequired { get; } = Error.Validation(
        "sale.void.by_required",
        "A void must record the user who authorised it.");

    /// <summary>The void reason is missing or too long.</summary>
    /// <param name="maxLength">The reason limit.</param>
    /// <returns>The error.</returns>
    public static Error VoidReasonInvalid(int maxLength) => Error.Validation(
        "sale.void.reason_invalid",
        FormattableString.Invariant($"A void reason is required and may be at most {maxLength} characters."));

    /// <summary>The available stock at the location cannot cover the request.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="locationId">The location.</param>
    /// <param name="available">The quantity currently sellable.</param>
    /// <param name="requested">The quantity requested.</param>
    /// <returns>The error.</returns>
    public static Error InsufficientStock(
        ProductId productId,
        LocationId locationId,
        decimal available,
        decimal requested) => Error.Conflict(
        "inventory.insufficient_stock",
        FormattableString.Invariant($"Available {available}, requested {requested}."),
        new Dictionary<string, object?>
        {
            ["productId"] = productId.Value,
            ["locationId"] = locationId.Value,
            ["available"] = available,
            ["requested"] = requested,
        });

    /// <summary>Every remaining batch for the product is expired.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="locationId">The location.</param>
    /// <returns>The error.</returns>
    public static Error ExpiredOnly(ProductId productId, LocationId locationId) => Error.Conflict(
        "inventory.expired_only",
        "Every remaining batch of this product is expired.",
        new Dictionary<string, object?>
        {
            ["productId"] = productId.Value,
            ["locationId"] = locationId.Value,
        });
}