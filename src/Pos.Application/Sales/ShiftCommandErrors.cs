using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Errors raised while opening or closing a shift. Domain-shape errors live on
/// <see cref="ShiftErrors"/>; these cover the facts the handlers resolve — the
/// location, the device and the shift's current state.
/// </summary>
internal static class ShiftCommandErrors
{
    /// <summary>The shift number does not parse.</summary>
    public static Error NumberInvalid => Error.Validation(
        "shift.number_invalid_format",
        "The shift number does not look like a SHF document number.");

    /// <summary>The number's device code does not match the authenticated device.</summary>
    public static Error NumberDeviceMismatch => Error.Conflict(
        "shift.number_device_mismatch",
        "The device code in the shift number does not match the device posting it.");

    /// <summary>The device does not exist.</summary>
    public static Error DeviceUnknown(DeviceId deviceId) => Error.NotFound(
        "shift.device_unknown",
        FormattableString.Invariant($"Unknown device {deviceId.Value}."));

    /// <summary>The request is not device-authenticated.</summary>
    public static Error DeviceRequired => Error.Forbidden(
        "shift.device_required",
        "Opening a shift requires a device-authenticated request.");

    /// <summary>The device already has an open shift.</summary>
    public static Error ShiftAlreadyOpen(DeviceId deviceId) => Error.Conflict(
        "shift.already_open",
        FormattableString.Invariant($"Device {deviceId.Value} already has an open shift."));

    /// <summary>The location named by the command does not exist.</summary>
    public static Error LocationUnknown(LocationId locationId) => Error.NotFound(
        "shift.location_unknown",
        FormattableString.Invariant($"Unknown location {locationId.Value}."));

    /// <summary>A shift cannot open at an external counterparty location.</summary>
    public static Error LocationExternal => Error.Conflict(
        "shift.location_external",
        "A shift cannot open at an external counterparty location.");

    /// <summary>The location named by the command is not the shift's location.</summary>
    public static Error LocationMismatch => Error.Conflict(
        "shift.location_mismatch",
        "The stated location does not match the shift's location.");

    /// <summary>A register reported a shift time ahead of head office's clock.</summary>
    public static Error TimeInFuture => Error.Validation(
        "shift.time_in_future",
        "The shift time is in the future. Check the register's clock.");

    /// <summary>A register reported closing a shift before it opened.</summary>
    public static Error ClosedBeforeOpened => Error.Validation(
        "shift.closed_before_opened",
        "A shift cannot close before it opened.");

    /// <summary>The actor does not hold <c>shift.close.other</c>.</summary>
    public static Error CloseOtherShiftDenied => Error.Forbidden(
        "shift.close_other_denied",
        "Operating another cashier's shift requires the shift.close.other permission.");

    /// <summary>The review reason is longer than the tolerated maximum.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error ReconcileReasonTooLong(int maxLength) => Error.Validation(
        "shift.reconcile_reason_too_long",
        FormattableString.Invariant($"The review reason cannot exceed {maxLength} characters."));
}