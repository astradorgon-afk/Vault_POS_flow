using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// Errors raised by stock adjustments and inventory counts. Codes are part of the
/// API contract.
/// </summary>
public static class InventoryControlErrors
{
    /// <summary>An adjustment or count needs at least one line.</summary>
    public static readonly Error AdjustmentEmpty = Error.Validation(
        "adjustment.empty", "An adjustment needs at least one line.");

    /// <summary>The reason cannot be used on a manual adjustment.</summary>
    /// <param name="reason">The refused reason.</param>
    /// <returns>The error.</returns>
    public static Error AdjustmentReasonNotAllowed(AdjustmentReasonCode reason) => Error.Validation(
        "adjustment.reason_not_allowed",
        FormattableString.Invariant($"'{reason}' cannot be used on a manual adjustment."),
        new Dictionary<string, object?> { ["reason"] = reason.ToString() });

    /// <summary>The reason <c>Other</c> needs explanatory notes.</summary>
    public static readonly Error AdjustmentNotesRequired = Error.Validation(
        "adjustment.notes_required", "An adjustment with the reason 'Other' needs notes of at least 10 characters.");

    /// <summary>A line's quantity is zero.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error AdjustmentQuantityZero(int lineNo) => Error.Validation(
        "adjustment.quantity_zero", FormattableString.Invariant($"Line {lineNo} must change the quantity."), Line(lineNo));

    /// <summary>A write-off line adds stock.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error AdjustmentMustRemoveStock(int lineNo) => Error.Validation(
        "adjustment.must_remove_stock",
        FormattableString.Invariant($"Line {lineNo} must remove stock: only the reason 'Other' can add it."),
        Line(lineNo));

    /// <summary>A line's state cannot be adjusted for the chosen reason.</summary>
    /// <param name="lineNo">The line.</param>
    /// <param name="state">The refused state.</param>
    /// <returns>The error.</returns>
    public static Error AdjustmentStateNotAllowed(int lineNo, InventoryState state) => Error.Validation(
        "adjustment.state_not_allowed",
        FormattableString.Invariant($"Line {lineNo}: stock in the {state} state cannot be adjusted for this reason."),
        new Dictionary<string, object?> { ["lineNo"] = lineNo, ["state"] = state.ToString() });

    /// <summary>A line's unit cost is negative.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error UnitCostNegative(int lineNo) => Error.Validation(
        "adjustment.unit_cost_negative", FormattableString.Invariant($"Line {lineNo} has a negative unit cost."), Line(lineNo));

    /// <summary>A batch-tracked product's line names no batch.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error BatchRequired(int lineNo) => Error.Validation(
        "inventory_control.batch_required",
        FormattableString.Invariant($"Line {lineNo} is for a batch-tracked product and needs a batch."),
        Line(lineNo));

    /// <summary>A product that is not batch-tracked names a batch.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error BatchNotAllowed(int lineNo) => Error.Validation(
        "inventory_control.batch_not_allowed",
        FormattableString.Invariant($"Line {lineNo} is for a product that is not batch-tracked."),
        Line(lineNo));

    /// <summary>Two lines name the same bucket.</summary>
    /// <param name="lineNo">The duplicate line.</param>
    /// <returns>The error.</returns>
    public static Error DuplicateLine(int lineNo) => Error.Validation(
        "inventory_control.duplicate_line",
        FormattableString.Invariant($"Line {lineNo} repeats a product, batch and state already listed."),
        Line(lineNo));

    /// <summary>A rejection, reversal or cancellation needs a reason.</summary>
    public static readonly Error ReasonRequired = Error.Validation(
        "inventory_control.reason_required", "A reason of at least 5 characters is required.");

    /// <summary>The adjustment is not in the state the operation needs.</summary>
    /// <param name="expected">The required state.</param>
    /// <param name="actual">The current state.</param>
    /// <returns>The error.</returns>
    public static Error AdjustmentInvalidState(StockAdjustmentStatus expected, StockAdjustmentStatus actual) => Error.Conflict(
        "adjustment.invalid_state",
        FormattableString.Invariant($"The adjustment must be {expected}; it is {actual}."),
        new Dictionary<string, object?> { ["expected"] = expected.ToString(), ["actual"] = actual.ToString() });

    /// <summary>No adjustment has the identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The error.</returns>
    public static Error AdjustmentUnknown(StockAdjustmentId id) => Error.NotFound(
        "adjustment.unknown", FormattableString.Invariant($"Stock adjustment {id.Value} was not found."));

    /// <summary>The caller has no authority at the document's location.</summary>
    public static readonly Error OutsideScope = Error.Forbidden(
        "inventory_control.outside_scope", "This document belongs to a location outside your scope.");

    /// <summary>The kind of count needs a scope it was not given, or was given one it cannot use.</summary>
    /// <param name="kind">The count kind.</param>
    /// <returns>The error.</returns>
    public static Error CountScopeInvalid(InventoryCountKind kind) => Error.Validation(
        "count.scope_invalid",
        kind switch
        {
            InventoryCountKind.FullPhysical => "A full physical count covers the whole location and takes no categories or products.",
            InventoryCountKind.Category => "A category count needs at least one category and takes no products.",
            InventoryCountKind.ProductSpecific => "A product count needs at least one product and takes no categories.",
            _ => "A cycle count needs at least one category or product.",
        },
        new Dictionary<string, object?> { ["kind"] = kind.ToString() });

    /// <summary>A counted quantity is negative.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error CountQuantityNegative(int lineNo) => Error.Validation(
        "count.quantity_negative", FormattableString.Invariant($"Line {lineNo} cannot count a negative quantity."), Line(lineNo));

    /// <summary>A product outside the count's sheet was recorded on a scoped count.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error CountProductNotOnSheet(ProductId productId) => Error.Validation(
        "count.product_not_on_sheet",
        "Only a full physical count accepts products that are not already on its count sheet.",
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>A count was submitted with nothing counted, or with lines still uncounted.</summary>
    /// <param name="uncountedLines">The uncounted line numbers.</param>
    /// <returns>The error.</returns>
    public static Error CountIncomplete(IReadOnlyCollection<int> uncountedLines) => Error.Validation(
        "count.incomplete",
        uncountedLines.Count == 0
            ? "A count needs at least one counted line before it is submitted."
            : "Every line must be counted before the count is submitted.",
        new Dictionary<string, object?> { ["uncountedLines"] = uncountedLines });

    /// <summary>Stock moved in counted buckets after they were counted.</summary>
    /// <param name="staleLines">The affected line numbers.</param>
    /// <returns>The error.</returns>
    public static Error CountStale(IReadOnlyCollection<int> staleLines) => Error.Conflict(
        "count.stock_moved_since_counted",
        "Stock moved after these lines were counted, so their variance is no longer reliable. Count them again.",
        new Dictionary<string, object?> { ["staleLines"] = staleLines });

    /// <summary>The count is not in the state the operation needs.</summary>
    /// <param name="expected">A state the operation accepts.</param>
    /// <param name="actual">The current state.</param>
    /// <returns>The error.</returns>
    public static Error CountInvalidState(InventoryCountStatus expected, InventoryCountStatus actual) => Error.Conflict(
        "count.invalid_state",
        FormattableString.Invariant($"The count must be {expected}; it is {actual}."),
        new Dictionary<string, object?> { ["expected"] = expected.ToString(), ["actual"] = actual.ToString() });

    /// <summary>No count has the identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The error.</returns>
    public static Error CountUnknown(InventoryCountId id) => Error.NotFound(
        "count.unknown", FormattableString.Invariant($"Inventory count {id.Value} was not found."));

    /// <summary>The location does not exist or is an external counterparty.</summary>
    /// <param name="locationId">The location.</param>
    /// <returns>The error.</returns>
    public static Error LocationInvalid(LocationId locationId) => Error.Validation(
        "inventory_control.location_invalid",
        "Stock can only be adjusted or counted at an existing warehouse or store.",
        new Dictionary<string, object?> { ["locationId"] = locationId.Value });

    /// <summary>The location has no time zone, so its business date is unknown.</summary>
    /// <param name="locationId">The location.</param>
    /// <returns>The error.</returns>
    public static Error LocationTimeZoneMissing(LocationId locationId) => Error.Conflict(
        "inventory_control.location_time_zone_missing",
        "The location has no time zone configured, so its business date cannot be determined.",
        new Dictionary<string, object?> { ["locationId"] = locationId.Value });

    /// <summary>The EXT-WRITEOFF counterparty has not been provisioned.</summary>
    public static readonly Error WriteOffLocationMissing = Error.Conflict(
        "inventory_control.writeoff_location_missing",
        "The EXT-WRITEOFF counterparty location is missing; run the location seeding first.");

    /// <summary>A product named on a line does not exist.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId productId) => Error.Validation(
        "inventory_control.product_unknown",
        "A product on the document does not exist.",
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>A batch named on a line does not exist or belongs to another product.</summary>
    /// <param name="lineNo">The line.</param>
    /// <returns>The error.</returns>
    public static Error BatchMismatch(int lineNo) => Error.Validation(
        "inventory_control.batch_mismatch",
        FormattableString.Invariant($"Line {lineNo} names a batch that does not belong to its product."),
        Line(lineNo));

    private static Dictionary<string, object?> Line(int lineNo) => new() { ["lineNo"] = lineNo };
}
