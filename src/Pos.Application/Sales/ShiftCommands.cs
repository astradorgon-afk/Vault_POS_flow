using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Opens a cashier shift on a device (POS.md §1). The device allocates the SHF
/// number and the business date; the server verifies the number's device code
/// against the authenticated device, refuses a second open shift on the device,
/// and persists the shift.
/// </summary>
/// <param name="Number">The device-allocated SHF number, for example <c>SHF-2026-D03-0042</c>.</param>
/// <param name="LocationId">The location the shift is opened at.</param>
/// <param name="BusinessDate">The business date the shift opens on.</param>
/// <param name="OpeningFloat">The cash in the drawer when the shift opens, greater than or equal to zero.</param>
public sealed record OpenShiftCommand(
    DocumentNumber Number,
    LocationId LocationId,
    DateOnly BusinessDate,
    decimal OpeningFloat)
    : ICommand<CashierShiftId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.OpenShift;
}

/// <summary>
/// Closes a cashier shift: the cashier declares the cash on hand, counts the
/// drawer, and the server records the variance against the expected float-plus-
/// cash-sales figure (POS.md §1). Closing another cashier's shift requires the
/// <c>shift.close.other</c> permission, checked inside the handler.
/// </summary>
/// <param name="ShiftId">The shift to close.</param>
/// <param name="LocationId">The location the shift belongs to.</param>
/// <param name="DeclaredCash">The cash the cashier declared before counting.</param>
/// <param name="CountedCash">The cash actually counted in the drawer.</param>
public sealed record CloseShiftCommand(
    CashierShiftId ShiftId,
    LocationId LocationId,
    decimal DeclaredCash,
    decimal CountedCash)
    : ICommand<CashierShiftId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.CloseShift;
}