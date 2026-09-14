using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// The expected business failures a cashier shift can produce. Codes are part of
/// the API contract; messages may change.
/// </summary>
public static class ShiftErrors
{
    /// <summary>The shift does not carry a document number.</summary>
    public static Error NumberInvalid { get; } = Error.Validation(
        "shift.number_invalid",
        "A shift must carry its SHF document number.");

    /// <summary>The shift references an empty location.</summary>
    public static Error LocationRequired { get; } = Error.Validation(
        "shift.location_required",
        "A shift must belong to a location.");

    /// <summary>The shift references an empty device.</summary>
    public static Error DeviceRequired { get; } = Error.Validation(
        "shift.device_required",
        "A shift must be opened on a registered device.");

    /// <summary>The shift references an empty cashier.</summary>
    public static Error CashierRequired { get; } = Error.Validation(
        "shift.cashier_required",
        "A shift must record which cashier opened it.");

    /// <summary>The opening float is negative.</summary>
    public static Error OpeningFloatInvalid { get; } = Error.Validation(
        "shift.opening_float_invalid",
        "The opening float cannot be negative.");

    /// <summary>The shift references the default business date.</summary>
    public static Error BusinessDateRequired { get; } = Error.Validation(
        "shift.business_date_required",
        "A shift must carry the business date it opened on.");

    /// <summary>The shift references the default opening instant.</summary>
    public static Error OpenedAtRequired { get; } = Error.Validation(
        "shift.opened_at_required",
        "A shift must record when it was opened.");

    /// <summary>A counted or declared cash amount is negative.</summary>
    /// <param name="amount">The offending amount.</param>
    /// <returns>The error.</returns>
    public static Error CashAmountInvalid(decimal amount) => Error.Validation(
        "shift.cash_amount_invalid",
        FormattableString.Invariant($"Cash amounts cannot be negative, got {amount}."));

    /// <summary>The shift references the default closure instant.</summary>
    public static Error ClosedAtRequired { get; } = Error.Validation(
        "shift.closed_at_required",
        "A shift must record when it was closed.");

    /// <summary>No shift has the identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The error.</returns>
    public static Error ShiftUnknown(CashierShiftId id) => Error.NotFound(
        "shift.unknown",
        FormattableString.Invariant($"No shift has the identifier {id}."));

    /// <summary>The shift is not in the state the operation needs.</summary>
    /// <param name="expected">The required state.</param>
    /// <param name="actual">The current state.</param>
    /// <returns>The error.</returns>
    public static Error ShiftInvalidState(ShiftStatus expected, ShiftStatus actual) => Error.Conflict(
        "shift.invalid_state",
        FormattableString.Invariant($"The shift must be {expected}; it is {actual}."),
        new Dictionary<string, object?> { ["expected"] = expected.ToString(), ["actual"] = actual.ToString() });
}