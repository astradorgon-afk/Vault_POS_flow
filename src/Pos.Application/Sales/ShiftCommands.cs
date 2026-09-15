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

/// <summary>
/// Suspends an open cashier shift while the cashier steps away; the device is
/// locked but the shift is retained (POS.md §1). Suspending another cashier's
/// shift requires the <c>shift.close.other</c> permission, checked inside the
/// handler.
/// </summary>
/// <param name="ShiftId">The shift to suspend.</param>
/// <param name="LocationId">The location the shift belongs to.</param>
public sealed record SuspendShiftCommand(
    CashierShiftId ShiftId,
    LocationId LocationId)
    : ICommand<CashierShiftId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.OpenShift;
}

/// <summary>
/// Resumes a suspended shift so it can keep accepting sales (POS.md §1).
/// Suspending and resuming are the same act of operating the idle shift, so the
/// same ownership rule — owner, or <c>shift.close.other</c> for administrative
/// acts — is checked inside the handler.
/// </summary>
/// <param name="ShiftId">The shift to resume.</param>
/// <param name="LocationId">The location the shift belongs to.</param>
public sealed record ResumeShiftCommand(
    CashierShiftId ShiftId,
    LocationId LocationId)
    : ICommand<CashierShiftId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.OpenShift;
}

/// <summary>
/// Marks a closed shift reconciled, completing its lifecycle (POS.md §1). A
/// shift whose cash variance lies beyond the location's
/// <c>CashVarianceThreshold</c> — or one the worker force-closed with no count —
/// requires the manager's <c>Reason</c> before it may be reconciled; the shift
/// itself enforces that rule. Reconciling another cashier's shift requires the
/// <c>shift.close.other</c> permission, checked inside the handler.
/// </summary>
/// <param name="ShiftId">The shift to reconcile.</param>
/// <param name="LocationId">The location the shift belongs to.</param>
/// <param name="Reason">The manager's review reason, when the variance requires one.</param>
public sealed record ReconcileShiftCommand(
    CashierShiftId ShiftId,
    LocationId LocationId,
    string? Reason)
    : ICommand<CashierShiftId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.CloseShift;
}