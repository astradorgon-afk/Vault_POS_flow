using Pos.Domain.Common;

namespace Pos.Domain.Transfers;

/// <summary>
/// Every error a pre-approval token can produce, keyed for stable machine
/// handling. Codes are part of the API contract and must never be renumbered.
/// </summary>
public static class PreApprovalTokenErrors
{
    /// <summary>The token does not exist.</summary>
    public static Error TokenUnknown(PreApprovalTokenId tokenId) => Error.NotFound(
        "preapproval.unknown",
        $"No pre-approval token exists with identifier '{tokenId.Value}'.");

    /// <summary>The token names no source location.</summary>
    public static Error SourceRequired => Error.Validation(
        "preapproval.source_required",
        "A pre-approval token must name the store the transfers leave.");

    /// <summary>The token names no destination location.</summary>
    public static Error DestinationRequired => Error.Validation(
        "preapproval.destination_required",
        "A pre-approval token must name the store the transfers arrive at.");

    /// <summary>Source and destination must differ.</summary>
    public static Error SameLocation => Error.Validation(
        "preapproval.same_location",
        "A pre-approval token cannot cover transfers to the same store.");

    /// <summary>The value ceiling must be positive when set.</summary>
    public static Error MaxValueInvalid => Error.Validation(
        "preapproval.max_value_invalid",
        "A pre-approval token's value ceiling must be greater than zero, or omitted for no ceiling.");

    /// <summary>The validity window must be well ordered.</summary>
    public static Error InvalidWindow => Error.Validation(
        "preapproval.invalid_window",
        "A pre-approval token's validity window must start before it ends.");

    /// <summary>A token can only be issued for the future.</summary>
    public static Error ExpiredAtIssue => Error.Conflict(
        "preapproval.expired_at_issue",
        "A pre-approval token cannot expire before or at the instant it is issued.");

    /// <summary>The token is not usable before its validity window opens.</summary>
    public static Error NotYetValid => Error.Conflict(
        "preapproval.not_yet_valid",
        "This pre-approval token is not yet usable; its validity window has not opened.");

    /// <summary>The token has passed its validity window.</summary>
    public static Error Expired => Error.Conflict(
        "preapproval.expired",
        "This pre-approval token has expired; raise a request through the ordinary review workflow.");

    /// <summary>The token was revoked before use.</summary>
    public static Error Revoked(PreApprovalTokenId tokenId) => Error.Conflict(
        "preapproval.revoked",
        $"Pre-approval token '{tokenId.Value}' has been revoked by head office.");

    /// <summary>The token is single use and already backed a transfer.</summary>
    public static Error AlreadyConsumed(PreApprovalTokenId tokenId) => Error.Conflict(
        "preapproval.already_consumed",
        $"Pre-approval token '{tokenId.Value}' has already been consumed by a transfer.");

    /// <summary>The requested route or products fall outside the token's scope.</summary>
    public static Error ScopeMismatch => Error.Validation(
        "preapproval.scope_mismatch",
        "The transfer does not match the pre-approval token's route or product scope.");

    /// <summary>The transfer's estimated value exceeds the token's ceiling.</summary>
    public static Error ValueExceeded => Error.Conflict(
        "preapproval.value_exceeded",
        "The transfer's estimated value exceeds the pre-approval token's ceiling; request a larger token from head office.");

    /// <summary>The route's source location does not exist.</summary>
    public static Error SourceLocationUnknown(LocationId locationId) => Error.NotFound(
        "preapproval.source_location_unknown",
        $"No location exists with identifier '{locationId.Value}'.");

    /// <summary>The route's destination location does not exist.</summary>
    public static Error DestinationLocationUnknown(LocationId locationId) => Error.NotFound(
        "preapproval.destination_location_unknown",
        $"No location exists with identifier '{locationId.Value}'.");

    /// <summary>A covered product does not exist.</summary>
    public static Error ProductUnknown(ProductId productId) => Error.NotFound(
        "preapproval.product_unknown",
        $"No product exists with identifier '{productId.Value}'.");

    /// <summary>A covered transfer would leave an external counterparty.</summary>
    public static Error SourceLocationExternal(LocationId locationId) => Error.Validation(
        "preapproval.source_location_external",
        $"Location '{locationId.Value}' is an external counterparty and cannot ship stock.");

    /// <summary>A covered transfer would arrive at an external counterparty.</summary>
    public static Error DestinationLocationExternal(LocationId locationId) => Error.Validation(
        "preapproval.destination_location_external",
        $"Location '{locationId.Value}' is an external counterparty and cannot receive stock.");

    /// <summary>Revoking requires a reason.</summary>
    public static Error RevokeReasonRequired => Error.Validation(
        "preapproval.revoke_reason_required",
        "Revoking a pre-approval token requires a reason explaining why it was withdrawn.");
}