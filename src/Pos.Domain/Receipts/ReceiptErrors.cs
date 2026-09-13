using Pos.Domain.Common;

namespace Pos.Domain.Receipts;

/// <summary>The domain errors a receipt can produce.</summary>
public static class ReceiptErrors
{
    /// <summary>The referenced receipt does not exist.</summary>
    public static Error Unknown(ReceiptId receiptId)
        => Error.NotFound("receipt.unknown", $"A receipt with id {receiptId} could not be found.");

    /// <summary>The receipt is at a location outside the caller's scope.</summary>
    public static Error OutsideScope(ReceiptId receiptId)
        => Error.Forbidden("receipt.outside_scope", "The receipt is at a location outside your scope.");

    /// <summary>A receipt can only be issued at a real branch, never an external counterparty.</summary>
    public static Error LocationExternal
        => Error.Validation("receipt.location_external", "A receipt cannot be issued against an external location.");

    /// <summary>The issuing location is unknown.</summary>
    public static Error LocationUnknown(LocationId locationId)
        => Error.Validation("receipt.location_unknown", $"Location {locationId} could not be found.");

    /// <summary>The kind is not a defined receipt kind.</summary>
    public static Error KindUnknown(ReceiptKind kind)
        => Error.Validation("receipt.kind_unknown", $"Unknown receipt kind: {kind}.");

    /// <summary>The location must be provided.</summary>
    public static Error LocationRequired
        => Error.Validation("receipt.location_required", "A location is required.");

    /// <summary>The amount must be greater than zero.</summary>
    public static Error AmountInvalid(decimal amount)
        => Error.Validation(
            "receipt.amount_invalid",
            FormattableString.Invariant($"A receipt amount must be greater than zero (received {amount})."));

    /// <summary>The counterparty name is too long.</summary>
    public static Error CounterpartyTooLong(int maxLength)
        => Error.Validation(
            "receipt.counterparty_too_long",
            FormattableString.Invariant($"A counterparty name may be at most {maxLength} characters."));

    /// <summary>The purpose note is too long.</summary>
    public static Error NoteTooLong(int maxLength)
        => Error.Validation(
            "receipt.note_too_long",
            FormattableString.Invariant($"A receipt note may be at most {maxLength} characters."));

    /// <summary>The issuing user could not be identified.</summary>
    public static Error IssuerRequired
        => Error.Validation("receipt.issuer_required", "The issuing user could not be identified.");

    /// <summary>A reference number must look like a business document number.</summary>
    public static Error ReferenceNumberInvalid
        => Error.Validation(
            "receipt.reference_number_invalid",
            "A reference number must look like PREFIX-YYYY-NNNNNN or PREFIX-YYYY-DEVICE-NNNNNN.");
}