using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// The ledger's error catalogue. Codes are part of the API contract and are
/// matched by clients (notably the unknown-barcode code, which drives the
/// quarantine workflow), so they must stay stable.
/// </summary>
public static class InventoryErrors
{
    /// <summary>A movement group must have at least two legs.</summary>
    public static Error TooFewLegs { get; } = Error.Validation(
        "inventory.movement_group_too_few_legs",
        "An inventory event must have at least two legs; stock cannot appear or vanish.");

    /// <summary>A leg carried a zero quantity.</summary>
    public static Error ZeroQuantityLeg { get; } = Error.Validation(
        "inventory.movement_zero_quantity",
        "An inventory movement leg must change the quantity by a non-zero amount.");

    /// <summary>A leg carried a negative unit cost.</summary>
    public static Error NegativeUnitCost { get; } = Error.Validation(
        "inventory.movement_negative_unit_cost",
        "An inventory movement cannot carry a negative unit cost.");

    /// <summary>A reversal group did not name the group it reverses.</summary>
    public static Error ReversalMissingOriginal { get; } = Error.Validation(
        "inventory.reversal_missing_original",
        "A reversal must reference the movement group it reverses.");

    /// <summary>A movement type that requires a reason code was posted without one.</summary>
    public static Error ReasonCodeRequired { get; } = Error.Validation(
        "inventory.reason_code_required",
        "This movement type requires an adjustment reason.");

    /// <summary>An adjustment reason of Other was given without explanatory notes.</summary>
    public static Error ReasonNotesRequired { get; } = Error.Validation(
        "inventory.reason_notes_required",
        "A reason of Other requires explanatory notes of at least ten characters.");

    /// <summary>A movement type that requires an approver was posted without one.</summary>
    public static Error ApproverRequired { get; } = Error.ApprovalRequired(
        "inventory.approver_required",
        "This movement type requires an approving user to be recorded.");

    /// <summary>The justifying document was missing or of the wrong type.</summary>
    public static Error ReferenceDocumentRequired { get; } = Error.Validation(
        "inventory.reference_document_required",
        "This movement type must be justified by a specific document type.");

    /// <summary>No rule is defined for the movement type.</summary>
    public static Error UnknownMovementType { get; } = Error.Validation(
        "inventory.unknown_movement_type",
        "No ledger rule is defined for this movement type.");

    /// <summary>A batch-tracked product was moved without a batch.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error BatchRequired(ProductId productId) => Error.Validation(
        "inventory.batch_required",
        "A batch-tracked product must be moved with a batch identifier.",
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>A batch was supplied for a product that is not batch-tracked.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error BatchNotAllowed(ProductId productId) => Error.Validation(
        "inventory.batch_not_allowed",
        "A batch identifier was supplied for a product that is not batch-tracked.",
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>The legs disagreed about whether a product is batch-tracked.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error BatchTrackingInconsistent(ProductId productId) => Error.Validation(
        "inventory.batch_tracking_inconsistent",
        "Legs for the same product disagree about batch tracking.",
        new Dictionary<string, object?> { ["productId"] = productId.Value });

    /// <summary>The legs for a product and batch did not net to zero.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="batchId">The batch, if any.</param>
    /// <param name="imbalance">The amount by which the legs failed to balance.</param>
    /// <returns>The error.</returns>
    public static Error NotBalanced(ProductId productId, BatchId? batchId, decimal imbalance) => Error.Validation(
        "inventory.movement_group_not_balanced",
        FormattableString.Invariant(
            $"Inventory event legs must net to zero per product and batch; out by {imbalance}."),
        new Dictionary<string, object?>
        {
            ["productId"] = productId.Value,
            ["batchId"] = batchId?.Value,
            ["imbalance"] = imbalance,
        });

    /// <summary>Stock was moved out of a state the movement type does not permit.</summary>
    /// <param name="type">The movement type.</param>
    /// <param name="state">The source state.</param>
    /// <returns>The error.</returns>
    public static Error SourceStateNotAllowed(InventoryMovementType type, InventoryState state) => Error.Validation(
        "inventory.source_state_not_allowed",
        FormattableString.Invariant($"A {type} movement may not take stock out of {state}."),
        new Dictionary<string, object?> { ["movementType"] = type.ToString(), ["state"] = state.ToString() });

    /// <summary>Stock was moved into a state the movement type does not permit.</summary>
    /// <param name="type">The movement type.</param>
    /// <param name="state">The destination state.</param>
    /// <returns>The error.</returns>
    public static Error DestinationStateNotAllowed(InventoryMovementType type, InventoryState state) => Error.Validation(
        "inventory.destination_state_not_allowed",
        FormattableString.Invariant($"A {type} movement may not put stock into {state}."),
        new Dictionary<string, object?> { ["movementType"] = type.ToString(), ["state"] = state.ToString() });

    /// <summary>A state was used on a location kind it is not valid for.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The error.</returns>
    public static Error StateInvalidForLocation(InventoryState state) => Error.Validation(
        "inventory.state_invalid_for_location",
        FormattableString.Invariant(
            $"The {state} state is not valid for this kind of location. The external state exists only on counterparty locations."),
        new Dictionary<string, object?> { ["state"] = state.ToString() });

    /// <summary>A posting would drive a bucket below zero under a prohibiting policy.</summary>
    /// <param name="productId">The product.</param>
    /// <param name="locationId">The location.</param>
    /// <param name="available">The quantity currently available.</param>
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

    /// <summary>The same business event was submitted with different content.</summary>
    public static Error IdempotencyKeyReuse { get; } = Error.Conflict(
        "inventory.idempotency_key_reuse",
        "This event identifier has already been processed with different content.");
}
