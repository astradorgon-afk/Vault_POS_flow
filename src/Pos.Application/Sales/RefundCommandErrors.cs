using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Errors raised while issuing a refund. Domain-shape errors live on
/// <see cref="SalesReturnErrors"/>; these cover the facts the handler verifies
/// before the domain runs — that the command names a return and a sale, that
/// the return exists and that the request matches its facts.
/// </summary>
internal static class RefundCommandErrors
{
    /// <summary>The refund names no sale.</summary>
    public static Error SaleRequired => Error.Validation(
        "sale.refund.sale_required",
        "A refund must name the sale the return was made against.");

    /// <summary>The refund names no return.</summary>
    public static Error ReturnRequired => Error.Validation(
        "sale.refund.return_required",
        "A refund must name the return it is issued against.");

    /// <summary>The refund names no location.</summary>
    public static Error LocationRequired => Error.Validation(
        "sale.refund.location_required",
        "A refund must happen at a location.");

    /// <summary>The sale named by the command does not exist.</summary>
    /// <param name="saleId">The missing sale.</param>
    /// <returns>The error.</returns>
    public static Error SaleNotFound(SaleId saleId) => Error.NotFound(
        "sale.refund.sale_not_found",
        FormattableString.Invariant($"No sale has the identifier {saleId}."));

    /// <summary>The return named by the command does not exist.</summary>
    /// <param name="salesReturnId">The missing return.</param>
    /// <returns>The error.</returns>
    public static Error ReturnNotFound(SalesReturnId salesReturnId) => Error.NotFound(
        "sale.refund.return_not_found",
        FormattableString.Invariant($"No return has the identifier {salesReturnId}."));

    /// <summary>The refund acts on a return that does not belong to the location.</summary>
    /// <param name="salesReturnId">The return.</param>
    /// <param name="locationId">The location the refund was requested at.</param>
    /// <returns>The error.</returns>
    public static Error LocationMismatch(SalesReturnId salesReturnId, LocationId locationId) => Error.Conflict(
        "sale.refund.location_mismatch",
        FormattableString.Invariant($"Return {salesReturnId} does not belong to location {locationId.Value}."));

    /// <summary>The refund acts on a return accepted on a different device.</summary>
    /// <param name="salesReturnId">The return.</param>
    /// <param name="deviceId">The device the refund was requested on.</param>
    /// <returns>The error.</returns>
    public static Error DeviceMismatch(SalesReturnId salesReturnId, DeviceId deviceId) => Error.Conflict(
        "sale.refund.device_mismatch",
        FormattableString.Invariant($"Return {salesReturnId} was not accepted on device {deviceId.Value}."));

    /// <summary>The refund names a sale different from the one the return was made against.</summary>
    /// <param name="salesReturnId">The return.</param>
    /// <param name="saleId">The sale the refund was requested against.</param>
    /// <returns>The error.</returns>
    public static Error SaleMismatch(SalesReturnId salesReturnId, SaleId saleId) => Error.Conflict(
        "sale.refund.sale_mismatch",
        FormattableString.Invariant($"Return {salesReturnId} was not made against sale {saleId}."));

    /// <summary>The device named by the command does not exist.</summary>
    /// <param name="deviceId">The unknown device.</param>
    /// <returns>The error.</returns>
    public static Error DeviceUnknown(DeviceId deviceId) => Error.NotFound(
        "sale.refund.device_unknown",
        FormattableString.Invariant($"Unknown device {deviceId.Value}."));

    /// <summary>The shift named by the command does not exist.</summary>
    /// <param name="shiftId">The unknown shift.</param>
    /// <returns>The error.</returns>
    public static Error ShiftUnknown(CashierShiftId shiftId) => Error.NotFound(
        "sale.refund.shift_unknown",
        FormattableString.Invariant($"No shift has the identifier {shiftId}."));

    /// <summary>The shift is no longer open and cannot issue refunds.</summary>
    /// <param name="status">The shift's actual status.</param>
    /// <returns>The error.</returns>
    public static Error ShiftNotOpen(ShiftStatus status) => Error.Conflict(
        "sale.refund.shift_not_open",
        FormattableString.Invariant($"The shift must be open to issue refunds; it is {status}."),
        new Dictionary<string, object?> { ["status"] = status.ToString() });

    /// <summary>The refund is issued on a device other than the one the shift was opened on.</summary>
    public static Error ShiftDeviceMismatch => Error.Conflict(
        "sale.refund.shift_device_mismatch",
        "A refund can only be issued on the device the shift was opened on.");
}