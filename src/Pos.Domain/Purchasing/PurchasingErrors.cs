using Pos.Domain.Common;

namespace Pos.Domain.Purchasing;

/// <summary>The purchasing module's error catalogue. Codes are part of the API contract.</summary>
public static class PurchasingErrors
{
    /// <summary>A referenced purchase order does not exist.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error OrderUnknown(PurchaseOrderId id) => Error.NotFound(
        "purchasing.order_unknown",
        FormattableString.Invariant($"Purchase order {id.Value} was not found."));

    /// <summary>An action required a purchase order in one state, but it was in another.</summary>
    /// <param name="expected">The state the action requires.</param>
    /// <param name="actual">The order's current state.</param>
    /// <returns>The error.</returns>
    public static Error InvalidState(PurchaseOrderStatus expected, PurchaseOrderStatus actual) => Error.Conflict(
        "purchasing.invalid_state",
        FormattableString.Invariant(
            $"A purchase order {ExpectedPhrase([expected])}; this one is {actual}."),
        new Dictionary<string, object?>
        {
            ["expectedStatus"] = expected,
            ["actualStatus"] = actual,
        });

    /// <summary>An action allowed several states, and the order was in none of them.</summary>
    /// <param name="expected">The states the action requires.</param>
    /// <param name="actual">The order's current state.</param>
    /// <returns>The error.</returns>
    public static Error InvalidState(IReadOnlyCollection<PurchaseOrderStatus> expected, PurchaseOrderStatus actual)
        => Error.Conflict(
            "purchasing.invalid_state",
            FormattableString.Invariant(
                $"A purchase order {ExpectedPhrase(expected)}; this one is {actual}."),
            new Dictionary<string, object?>
            {
                ["expectedStatuses"] = expected.Select(s => s.ToString()).ToArray(),
                ["actualStatus"] = actual,
            });

    /// <summary>An order must carry a purchase order identifier.</summary>
    /// <returns>The error.</returns>
    public static Error OrderIdRequired => Error.Validation(
        "purchasing.order_required",
        "A purchase order identifier must be specified.");

    /// <summary>An order must name a supplier.</summary>
    /// <returns>The error.</returns>
    public static Error SupplierRequired => Error.Validation(
        "purchasing.supplier_required",
        "A supplier must be specified.");

    /// <summary>An order must name a destination location.</summary>
    /// <returns>The error.</returns>
    public static Error DestinationRequired => Error.Validation(
        "purchasing.destination_required",
        "A destination location must be specified.");

    /// <summary>The currency code is not a valid ISO-4217 code.</summary>
    /// <returns>The error.</returns>
    public static Error CurrencyInvalid => Error.Validation(
        "purchasing.currency_invalid",
        "Currency must be a three-letter ISO-4217 code such as PHP.");

    /// <summary>An order must carry at least one line.</summary>
    /// <returns>The error.</returns>
    public static Error EmptyOrder => Error.Validation(
        "purchasing.empty_order",
        "A purchase order must have at least one line.");

    /// <summary>A line quantity must be positive, in the storage unit.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error InvalidQuantity(int lineNo) => Error.Validation(
        "purchasing.line_quantity_invalid",
        FormattableString.Invariant($"Line {lineNo}: ordered quantity must be greater than zero."));

    /// <summary>A line unit cost must not be negative.</summary>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error InvalidUnitCost(int lineNo) => Error.Validation(
        "purchasing.line_unit_cost_invalid",
        FormattableString.Invariant($"Line {lineNo}: unit cost must not be negative."));

    /// <summary>The same product appeared on more than one line.</summary>
    /// <param name="productId">The duplicated product.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateProductLine(ProductId productId) => Error.Validation(
        "purchasing.duplicate_product_line",
        FormattableString.Invariant($"Product {productId.Value} appears on more than one line."),
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>A referenced supplier does not exist or is inactive.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error SupplierUnknown(SupplierId id) => Error.NotFound(
        "purchasing.supplier_unknown",
        FormattableString.Invariant($"Supplier {id.Value} was not found or is inactive."));

    /// <summary>A referenced product does not exist or is inactive.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId id) => Error.NotFound(
        "purchasing.product_unknown",
        FormattableString.Invariant($"Product {id.Value} was not found or is inactive."));

    /// <summary>A line is bought in a unit the product does not count itself in.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="lineNo">The offending line.</param>
    /// <returns>The error.</returns>
    public static Error LineUnitMismatch(ProductId productId, int lineNo) => Error.Validation(
        "purchasing.line_uom_mismatch",
        FormattableString.Invariant(
            $"Line {lineNo}: product {productId.Value} cannot be bought in the requested unit of measure."));

    /// <summary>The destination location does not exist or is an external counterparty.</summary>
    /// <param name="id">The identifier that was looked up.</param>
    /// <returns>The error.</returns>
    public static Error DestinationUnknown(LocationId id) => Error.NotFound(
        "purchasing.destination_unknown",
        FormattableString.Invariant($"Location {id.Value} was not found or cannot receive goods."));

    /// <summary>Closing an order that has not been fully received needs a reason.</summary>
    /// <returns>The error.</returns>
    public static Error CloseReasonRequired => Error.Validation(
        "purchasing.close_reason_required",
        "Closing a purchase order that has not been fully received requires a reason.");

    /// <summary>Cancelling an order that has already been submitted needs a reason.</summary>
    /// <returns>The error.</returns>
    public static Error CancelReasonRequired => Error.Validation(
        "purchasing.cancel_reason_required",
        "Cancelling a submitted purchase order requires a reason.");

    /// <summary>The order has a number, so it can no longer be withdrawn as a draft.</summary>
    /// <returns>The error.</returns>
    public static Error NumberedCannotBeWithdrawn => Error.Conflict(
        "purchasing.numbered_cannot_be_withdrawn",
        "A purchase order that has been submitted cannot be withdrawn; cancel it instead.");

    private static string ExpectedPhrase(IReadOnlyCollection<PurchaseOrderStatus> expected)
        => expected.Count switch
        {
            1 => $"must be {expected.First()}",
            _ => FormattableString.Invariant($"must be one of {string.Join(", ", expected.Select(s => s.ToString()))}"),
        };
}