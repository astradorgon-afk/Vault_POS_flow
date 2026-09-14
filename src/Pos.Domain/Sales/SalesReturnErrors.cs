using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// The expected business failures a customer return and its refunds can
/// produce. Codes are part of the API contract; messages may change.
/// </summary>
public static class SalesReturnErrors
{
    /// <summary>The return references an empty event identifier.</summary>
    public static Error EventRequired { get; } = Error.Validation(
        "sale.return.event_required",
        "A return must carry the event identifier that makes it idempotent.");

    /// <summary>The return references an empty sale.</summary>
    public static Error SaleRequired { get; } = Error.Validation(
        "sale.return.sale_required",
        "A return must name the sale the goods were bought under.");

    /// <summary>The return references an empty location.</summary>
    public static Error LocationRequired { get; } = Error.Validation(
        "sale.return.location_required",
        "A return must happen at a location.");

    /// <summary>The return references an empty cashier shift.</summary>
    public static Error ShiftRequired { get; } = Error.Validation(
        "sale.return.shift_required",
        "A return must happen inside an open cashier shift.");

    /// <summary>The return references an empty device.</summary>
    public static Error DeviceRequired { get; } = Error.Validation(
        "sale.return.device_required",
        "A return must happen on a registered device.");

    /// <summary>The return references an empty business date.</summary>
    public static Error BusinessDateRequired { get; } = Error.Validation(
        "sale.return.business_date_required",
        "A return must carry the business date it counts toward.");

    /// <summary>The return references the default acceptance timestamp.</summary>
    public static Error ReturnedAtRequired { get; } = Error.Validation(
        "sale.return.returned_at_required",
        "A return must record when the goods were accepted back.");

    /// <summary>The return references an empty accepting user.</summary>
    public static Error ReturnedByRequired { get; } = Error.Validation(
        "sale.return.returned_by_required",
        "A return must record the user who accepted the goods back.");

    /// <summary>The return has no lines.</summary>
    public static Error ItemsRequired { get; } = Error.Validation(
        "sale.return.items_required",
        "A return must accept at least one line back.");

    /// <summary>A return line references a sale line the return does not know about.</summary>
    /// <param name="saleItemId">The unknown sale line.</param>
    /// <returns>The error.</returns>
    public static Error ItemUnknown(SaleItemId saleItemId) => Error.Validation(
        "sale.return.item_unknown",
        FormattableString.Invariant($"A return line references sale line {saleItemId}, which the return knows nothing about."));

    /// <summary>A return line's quantity is not positive.</summary>
    public static Error QuantityInvalid { get; } = Error.Validation(
        "sale.return.item.quantity_invalid",
        "A return line must accept back more than zero units.");

    /// <summary>A return line accepts back more than the sale line still holds unreturned.</summary>
    /// <param name="saleItemId">The sale line.</param>
    /// <param name="remaining">How many units remain unreturned on the line, including this return's own lines.</param>
    /// <param name="requested">The quantity the return line asks for.</param>
    /// <returns>The error.</returns>
    public static Error QuantityExceedsRemaining(SaleItemId saleItemId, decimal remaining, decimal requested) => Error.Conflict(
        "sale.return.item.quantity_exceeds_remaining",
        FormattableString.Invariant($"Sale line {saleItemId} still has {remaining} units available to return; {requested} were requested."),
        new Dictionary<string, object?>
        {
            ["saleItemId"] = saleItemId.Value,
            ["remaining"] = remaining,
            ["requested"] = requested,
        });

    /// <summary>A refund carries no event identifier.</summary>
    public static Error RefundEventRequired { get; } = Error.Validation(
        "sale.refund.event_required",
        "A refund must carry the event identifier that makes it idempotent.");

    /// <summary>A refund references an empty cashier shift.</summary>
    public static Error RefundShiftRequired { get; } = Error.Validation(
        "sale.refund.shift_required",
        "A refund must happen inside an open cashier shift.");

    /// <summary>A refund references an empty device.</summary>
    public static Error RefundDeviceRequired { get; } = Error.Validation(
        "sale.refund.device_required",
        "A refund must happen on a registered device.");

    /// <summary>A refund has no timestamp.</summary>
    public static Error RefundStampRequired { get; } = Error.Validation(
        "sale.refund.stamp_required",
        "A refund must record when it was issued.");

    /// <summary>A refund has no authorising user.</summary>
    public static Error RefundByRequired { get; } = Error.Validation(
        "sale.refund.by_required",
        "A refund must record the user who issued it.");

    /// <summary>A refund uses an unknown payment method.</summary>
    public static Error RefundMethodUnknown { get; } = Error.Validation(
        "sale.refund.method_unknown",
        "The refund payment method is not recognised.");

    /// <summary>A refund amount is not positive.</summary>
    public static Error RefundAmountInvalid { get; } = Error.Validation(
        "sale.refund.amount_invalid",
        "A refund must pay back a positive amount.");

    /// <summary>A card or wallet refund's provider reference is too long.</summary>
    /// <param name="maxLength">The reference limit.</param>
    /// <returns>The error.</returns>
    public static Error RefundReferenceTooLong(int maxLength) => Error.Validation(
        "sale.refund.reference_too_long",
        FormattableString.Invariant($"A provider reference may be at most {maxLength} characters."));

    /// <summary>A cash refund has no tendered amount.</summary>
    public static Error RefundTenderedRequired { get; } = Error.Validation(
        "sale.refund.tendered_required",
        "A cash refund must record the amount handed back.");

    /// <summary>The cash rounding increment is not positive.</summary>
    public static Error RefundIncrementInvalid { get; } = Error.Validation(
        "sale.refund.increment_invalid",
        "The cash rounding increment must be positive.");

    /// <summary>A cash refund is tendered for less than the refund amount.</summary>
    public static Error RefundTenderedInsufficient { get; } = Error.Validation(
        "sale.refund.tendered_insufficient",
        "The amount handed back does not cover the refund.");

    /// <summary>The refund method was not used to pay the original sale.</summary>
    /// <param name="method">The method refused.</param>
    /// <returns>The error.</returns>
    public static Error RefundMethodNotOriginal(PaymentMethod method) => Error.Conflict(
        "sale.refund.method_not_original",
        FormattableString.Invariant($"The original sale was not paid with {method}; a refund cannot use it."));

    /// <summary>The refund exceeds what was paid by that method, counting every return of the sale.</summary>
    /// <param name="method">The payment method.</param>
    /// <param name="paid">What the original sale paid by the method.</param>
    /// <param name="requested">The cumulative refunds against the method, including this one.</param>
    /// <returns>The error.</returns>
    public static Error RefundExceedsPaidForMethod(
        PaymentMethod method,
        decimal paid,
        decimal requested) => Error.Conflict(
        "sale.refund.exceeds_paid_for_method",
        FormattableString.Invariant($"Refunds of {requested} exceed the {paid} the original sale paid by {method}."),
        new Dictionary<string, object?>
        {
            ["method"] = method.ToString(),
            ["paid"] = paid,
            ["requested"] = requested,
        });

    /// <summary>The refund exceeds what this return accepted back.</summary>
    /// <param name="refundable">The return's refundable total.</param>
    /// <param name="requested">The cumulative refunds against the return, including this one.</param>
    /// <returns>The error.</returns>
    public static Error RefundExceedsRefundable(decimal refundable, decimal requested) => Error.Conflict(
        "sale.refund.exceeds_refundable",
        FormattableString.Invariant($"Refunds of {requested} exceed the return's refundable total of {refundable}."),
        new Dictionary<string, object?>
        {
            ["refundable"] = refundable,
            ["requested"] = requested,
        });
}