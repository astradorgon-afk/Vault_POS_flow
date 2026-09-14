using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// Errors raised by the expiry management system. Codes are part of the API
/// contract and must never be renumbered.
/// </summary>
public static class ExpiryErrors
{
    /// <summary>No batches in the queried scope were past expiry.</summary>
    public static Error NoExpiredBatches { get; } = Error.NotFound(
        "expiry.no_expired_batches",
        "No batches past their expiry date were found at this location.");

    /// <summary>The location does not exist or is an external counterparty.</summary>
    /// <param name="locationId">The location.</param>
    /// <returns>The error.</returns>
    public static Error LocationInvalid(LocationId locationId) => Error.Validation(
        "expiry.location_invalid",
        "Expiry runs can only be performed at an existing warehouse or store.",
        new Dictionary<string, object?> { ["locationId"] = locationId.Value });

    /// <summary>The location has no time zone, so its business date is unknown.</summary>
    /// <param name="locationId">The location.</param>
    /// <returns>The error.</returns>
    public static Error LocationTimeZoneMissing(LocationId locationId) => Error.Conflict(
        "expiry.location_time_zone_missing",
        "The location has no time zone configured, so its business date cannot be determined.",
        new Dictionary<string, object?> { ["locationId"] = locationId.Value });

    /// <summary>The EXT-WRITEOFF counterparty has not been provisioned.</summary>
    public static readonly Error WriteOffLocationMissing = Error.Conflict(
        "expiry.writeoff_location_missing",
        "The EXT-WRITEOFF counterparty location is missing; run the location seeding first.");

    /// <summary>A ledger posting failed.</summary>
    /// <param name="errorCode">The ledger error code.</param>
    /// <param name="errorMessage">The ledger error message.</param>
    /// <returns>The error.</returns>
    public static Error LedgerPostFailed(string errorCode, string errorMessage) => Error.Conflict(
        "expiry.ledger_post_failed",
        FormattableString.Invariant($"The expiry ledger posting failed: {errorCode} — {errorMessage}."),
        new Dictionary<string, object?> { ["ledgerErrorCode"] = errorCode, ["ledgerErrorMessage"] = errorMessage });

    /// <summary>The batch to expire does not belong to the product.</summary>
    /// <param name="batchId">The batch.</param>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error BatchProductMismatch(BatchId batchId, ProductId productId) => Error.Validation(
        "expiry.batch_product_mismatch",
        "The batch does not belong to the specified product.",
        new Dictionary<string, object?> { ["batchId"] = batchId.Value, ["productId"] = productId.Value });

    /// <summary>A product named in the expiry query does not exist.</summary>
    /// <param name="productId">The product.</param>
    /// <returns>The error.</returns>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "expiry.product_unknown",
        "The specified product does not exist.");
}
