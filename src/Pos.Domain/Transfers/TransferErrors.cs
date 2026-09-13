using Pos.Domain.Common;

namespace Pos.Domain.Transfers;

/// <summary>
/// Every error a transfer document can produce, keyed for stable machine
/// handling. Codes are part of the API contract and must never be renumbered.
/// </summary>
public static class TransferErrors
{
    /// <summary>A transfer must name at least one line.</summary>
    public static Error EmptyTransfer => Error.Validation(
        "transfer.empty",
        "A transfer must name at least one line.");

    /// <summary>The source location identifier is mandatory.</summary>
    public static Error SourceRequired => Error.Validation(
        "transfer.source_required",
        "A transfer must name a source location.");

    /// <summary>The destination location identifier is mandatory.</summary>
    public static Error DestinationRequired => Error.Validation(
        "transfer.destination_required",
        "A transfer must name a destination location.");

    /// <summary>Source and destination implementations.</summary>
    public static Error SameLocation => Error.Validation(
        "transfer.same_location",
        "A transfer cannot move stock to its own location.");

    /// <summary>The transfer does not exist.</summary>
    public static Error TransferUnknown(TransferOrderId transferId) => Error.NotFound(
        "transfer.unknown",
        $"No transfer exists with identifier '{transferId.Value}'.");

    /// <summary>The source location does not exist.</summary>
    public static Error SourceLocationUnknown(LocationId locationId) => Error.NotFound(
        "transfer.source_location_unknown",
        $"No location exists with identifier '{locationId.Value}'.");

    /// <summary>The destination location does not exist.</summary>
    public static Error DestinationLocationUnknown(LocationId locationId) => Error.NotFound(
        "transfer.destination_location_unknown",
        $"No location exists with identifier '{locationId.Value}'.");

    /// <summary>A transfer cannot leave an external counterparty.</summary>
    public static Error SourceLocationExternal(LocationId locationId) => Error.Validation(
        "transfer.source_location_external",
        $"Location '{locationId.Value}' is an external counterparty and cannot ship stock.");

    /// <summary>A transfer cannot arrive at an external counterparty.</summary>
    public static Error DestinationLocationExternal(LocationId locationId) => Error.Validation(
        "transfer.destination_location_external",
        $"Location '{locationId.Value}' is an external counterparty and cannot receive stock.");

    /// <summary>The source location's timezone must be configured for the ledger's business date.</summary>
    public static Error SourceTimeZoneMissing(LocationId locationId) => Error.Conflict(
        "transfer.source_timezone_missing",
        $"Location '{locationId.Value}' has no timezone configured; its business date cannot be computed.");

    /// <summary>The destination location's timezone must be configured for the ledger's business date.</summary>
    public static Error DestinationTimeZoneMissing(LocationId locationId) => Error.Conflict(
        "transfer.destination_timezone_missing",
        $"Location '{locationId.Value}' has no timezone configured; its business date cannot be computed.");

    /// <summary>A transfer line quantity must be positive.</summary>
    public static Error LineQuantityInvalid(int lineNo) => Error.Validation(
        "transfer.line_quantity_invalid",
        $"Line {lineNo} requests a non-positive quantity; quantities move whole units in the base unit of measure.");

    /// <summary>Two lines cannot name the same product.</summary>
    public static Error DuplicateLine(int lineNo) => Error.Validation(
        "transfer.duplicate_line",
        $"Line {lineNo} duplicates a product already on the transfer; merge the quantities onto one line.");

    /// <summary>The operation is not valid in the transfer's current state.</summary>
    public static Error InvalidTransferState(TransferStatus expected, TransferStatus actual) => Error.Conflict(
        "transfer.invalid_state",
        $"A transfer in state '{actual}' cannot perform an operation that requires state '{expected}'.");

    /// <summary>The operation is not valid in any of the transfer's allowed states.</summary>
    public static Error InvalidTransferState(IReadOnlyCollection<TransferStatus> expected, TransferStatus actual) => Error.Conflict(
        "transfer.invalid_state",
        $"A transfer in state '{actual}' cannot perform an operation that requires one of '{string.Join(", ", expected)}'.");

    /// <summary>A transfer number can only be assigned once.</summary>
    public static Error AlreadyNumbered(TransferOrderId transferId) => Error.Conflict(
        "transfer.already_numbered",
        $"Transfer '{transferId.Value}' already carries a document number.");

    /// <summary>Dispatching requires the allocated TRF number.</summary>
    public static Error NumberRequired => Error.Conflict(
        "transfer.number_required",
        "A transfer cannot be dispatched before its TRF number is allocated.");

    /// <summary>An approval amendment must name an existing line with a positive quantity.</summary>
    public static Error AmendmentInvalid(int lineNo) => Error.Validation(
        "transfer.amendment_invalid",
        $"Approval line {lineNo} is not a valid amendment: it must name a line on the transfer and request a positive quantity.");

    /// <summary>Picking requires at least one allocation.</summary>
    public static Error NothingPicked => Error.Validation(
        "transfer.nothing_picked",
        "Picking must record at least one allocated quantity before the transfer can be readied.");

    /// <summary>An allocation must name a line on the transfer.</summary>
    public static Error LineUnknown(int lineNo) => Error.Validation(
        "transfer.line_unknown",
        $"Line {lineNo} does not exist on the transfer.");

    /// <summary>An allocation quantity must be positive.</summary>
    public static Error PickQuantityInvalid(int lineNo) => Error.Validation(
        "transfer.pick_quantity_invalid",
        $"Line {lineNo} allocates a non-positive quantity; whole units move in the base unit of measure.");

    /// <summary>A received or damaged quantity must be non-negative.</summary>
    public static Error ReceiveQuantityInvalid(int lineNo) => Error.Validation(
        "transfer.receive_quantity_invalid",
        $"Line {lineNo} records a negative received or damaged quantity.");

    /// <summary>A receipt line must name an allocation that was dispatched.</summary>
    public static Error ReceiveUnknownAllocation(int lineNo) => Error.Validation(
        "transfer.receive_unknown_allocation",
        $"Line {lineNo} receipts a lot that was not picked on this transfer.");

    /// <summary>An allocation exceeds the quantity the line requested.</summary>
    public static Error PickExceedsRequested(int lineNo) => Error.Validation(
        "transfer.pick_exceeds_requested",
        $"Line {lineNo} allocates more than the transfer requested; reduce the picked quantity.");

    /// <summary>Two allocations cannot pick the same lot of the same line.</summary>
    public static Error DuplicateAllocation(int lineNo) => Error.Validation(
        "transfer.duplicate_allocation",
        $"Line {lineNo} allocates the same lot more than once; merge the quantities.");

    /// <summary>Picking a batch-tracked product requires a named lot.</summary>
    public static Error BatchRequired(int lineNo) => Error.Validation(
        "transfer.batch_required",
        $"Line {lineNo} picks a batch-tracked product without naming the lot.");

    /// <summary>A non-batch product cannot be allocated to a lot.</summary>
    public static Error BatchNotAllowed(int lineNo) => Error.Validation(
        "transfer.batch_not_allowed",
        $"Line {lineNo} names a lot for a product that does not track batches.");

    /// <summary>An allocation names a lot that belongs to a different product.</summary>
    public static Error BatchProductMismatch(int lineNo) => Error.Validation(
        "transfer.batch_product_mismatch",
        $"Line {lineNo} names a lot that belongs to a different product.");

    /// <summary>Not enough eligible stock exists to cover an allocation.</summary>
    public static Error StockUnavailable(int lineNo, decimal quantity) => Error.Conflict(
        "transfer.stock_unavailable",
        $"Line {lineNo} cannot pick {quantity} units: the source location has insufficient available stock.");

    /// <summary>
    /// The pick skips an earlier-expiring available lot. Transfers must be
    /// picked first-expiry-first-out so shelf life is consumed in order.
    /// </summary>
    public static Error PickSkipsEarlierExpiry(int lineNo) => Error.Validation(
        "transfer.pick_skips_earlier_expiry",
        $"Line {lineNo} picks a later-expiring lot while an earlier lot is still available; clear the earlier lot first.");

    /// <summary>Cancelling a dispatch needs a reason, which is recorded on the ledger.</summary>
    public static Error CancelReasonRequired => Error.Validation(
        "transfer.cancel_reason_required",
        "Cancelling a dispatch requires a reason explaining why the shipment was undone.");

    /// <summary>Cancelling a partly received shipment would strand its ledger.</summary>
    public static Error CancelAfterReceive => Error.Conflict(
        "transfer.cancel_after_receive",
        "A dispatched transfer can only be cancelled before the destination receives any of it.");

    /// <summary>A receipt cannot accept more than the dispatch carried, and never a negative quantity.</summary>
    public static Error ReceiveExceedsAllocation(int lineNo) => Error.Validation(
        "transfer.receive_exceeds_allocation",
        $"Line {lineNo} records received and damaged quantities greater than the dispatched quantity; over-receipt against a transfer is not permitted.");

    /// <summary>A receipt must count something.</summary>
    public static Error NothingReceived => Error.Validation(
        "transfer.nothing_received",
        "A receipt must record a received or damaged quantity.");

    /// <summary>The discrepancy does not exist.</summary>
    public static Error DiscrepancyUnknown(TransferDiscrepancyId discrepancyId) => Error.NotFound(
        "transfer.discrepancy_unknown",
        $"No transfer discrepancy exists with identifier '{discrepancyId.Value}'.");

    /// <summary>A discrepancy can only be resolved once.</summary>
    public static Error DiscrepancyAlreadyResolved(TransferDiscrepancyId discrepancyId) => Error.Conflict(
        "transfer.discrepancy_already_resolved",
        $"Transfer discrepancy '{discrepancyId.Value}' is already resolved.");

    /// <summary>Verifying requires every shortfall to have been resolved first.</summary>
    public static Error VerifyHasOpenVariance => Error.Conflict(
        "transfer.verify_has_open_variance",
        "A transfer with unresolved quantity shortfalls cannot be verified; resolve the discrepancies first.");

    /// <summary>Verifying requires at least some stock to have arrived.</summary>
    public static Error VerifyNothingReceived => Error.Conflict(
        "transfer.verify_nothing_received",
        "A transfer that delivered no stock cannot be verified; cancel the dispatch instead.");

    /// <summary>A variance resolution outcome is required.</summary>
    public static Error ResolutionOutcomeRequired => Error.Validation(
        "transfer.resolution_outcome_required",
        "Resolving a transfer discrepancy requires a resolution outcome.");

    /// <summary>A named product does not exist.</summary>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "transfer.product_unknown",
        $"No product exists with identifier '{productId.Value}'.");

    /// <summary>The write-off counterparty is not provisioned.</summary>
    public static Error WriteOffExternalLocationMissing => Error.Conflict(
        "transfer.writeoff_external_missing",
        "The EXT-WRITEOFF counterparty location is not provisioned; variance write-offs cannot post.");

    /// <summary>An emergency transfer can only move stock between two stores.</summary>
    public static Error EmergencyInvalidRoute => Error.Validation(
        "transfer.emergency_invalid_route",
        "An emergency transfer can only move stock between two store locations.");

    /// <summary>Emergency transfers cannot carry batch-tracked products.</summary>
    public static Error EmergencyBatchNotSupported(int lineNo) => Error.Validation(
        "transfer.emergency_batch_not_supported",
        $"Line {lineNo} names a batch-tracked product; emergency movements cannot name a lot and are restricted to non-batch products.");

    /// <summary>A store has already exhausted its monthly emergency budget.</summary>
    public static Error EmergencyMonthlyCapExceeded => Error.Conflict(
        "transfer.emergency_monthly_cap_exceeded",
        "This store has exhausted its monthly emergency transfer budget; raise a normal transfer and wait for approval.");

    /// <summary>An emergency transfer must name a distinct co-signing manager.</summary>
    public static Error EmergencyCoSignerSameAsBearer => Error.Validation(
        "transfer.emergency_co_signer_same",
        "The co-signing manager must be a different user from the originating manager.");

    /// <summary>The co-signing manager could not be authorised against the destination.</summary>
    public static Error EmergencyCoSignerUnauthorized => Error.Validation(
        "transfer.emergency_co_signer_unauthorized",
        "The named co-signing manager does not hold emergency transfer permission at the destination store.");

    /// <summary>Submitting pre-approved requires the transfer to carry a token.</summary>
    public static Error SubmitPreApprovedTokenMissing => Error.Conflict(
        "transfer.submit_preapproved_token_missing",
        "A pre-approved transfer must carry a pre-approval token.");

    /// <summary>Submitting pre-approved requires the transfer to be in pre-approved mode.</summary>
    public static Error SubmitPreApprovedModeRequired => Error.Conflict(
        "transfer.submit_preapproved_mode_required",
        "Only transfers raised against a pre-approval token can be submitted as pre-approved.");

    /// <summary>Rejecting an emergency requires a reason, which is recorded on the ledger.</summary>
    public static Error ReviewRejectReasonRequired => Error.Validation(
        "transfer.review_reject_reason_required",
        "Rejecting an emergency transfer requires a reason explaining why the stock movement is reversed.");

    /// <summary>Only emergency transfers carry an emergency ledger link.</summary>
    public static Error EmergencyLedgerLinkInvalid => Error.Conflict(
        "transfer.emergency_ledger_link_invalid",
        "Only an emergency transfer can record an emergency ledger group.");

    /// <summary>An emergency transfer can only link one ledger group.</summary>
    public static Error EmergencyAlreadyPosted => Error.Conflict(
        "transfer.emergency_already_posted",
        "This emergency transfer already recorded its ledger group; a second group cannot be attached.");
}