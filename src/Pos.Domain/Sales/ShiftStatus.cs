namespace Pos.Domain.Sales;

/// <summary>
/// The lifecycle state of a cashier shift (POS.md §1). A sale may only be
/// completed inside an <see cref="Open"/> shift whose cashier owns it.
/// </summary>
public enum ShiftStatus
{
    /// <summary>The shift is open and accepting sales.</summary>
    Open = 1,

    /// <summary>The cashier stepped away and the device is locked; the shift is retained.</summary>
    Suspended = 2,

    /// <summary>The cashier has declared the cash on hand; counting and closure are pending.</summary>
    PendingClose = 3,

    /// <summary>The drawer was counted and the variance recorded.</summary>
    Closed = 4,

    /// <summary>A manager reviewed and accepted the cash variance.</summary>
    Reconciled = 5,
}