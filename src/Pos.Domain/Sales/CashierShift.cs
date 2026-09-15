using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// A cashier's shift: the physical period between drawer open and drawer close
/// at a single device. A sale may only be completed inside an <see cref="ShiftStatus.Open"/>
/// shift whose cashier owns it and whose device matches the sale device (POS.md §1).
/// </summary>
public class CashierShift : AggregateRoot<CashierShiftId>
{
    private CashierShift(
        CashierShiftId id,
        DocumentNumber number,
        LocationId locationId,
        DeviceId deviceId,
        UserId cashierUserId,
        decimal openingFloat,
        DateOnly businessDate,
        DateTimeOffset openedAtUtc)
    {
        Id = id;
        Number = number.Value;
        LocationId = locationId;
        DeviceId = deviceId;
        CashierUserId = cashierUserId;
        OpeningFloat = decimal.Round(openingFloat, Money.StorageScale, Money.IntermediateRounding);
        BusinessDate = businessDate;
        OpenedAtUtc = openedAtUtc;
        Status = ShiftStatus.Open;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private CashierShift()
    {
        Number = string.Empty;
        LocationId = LocationId.Empty;
        DeviceId = DeviceId.Empty;
        CashierUserId = UserId.Empty;
    }

    /// <summary>Gets the SHF document number.</summary>
    public string Number { get; private set; }

    /// <summary>Gets the location the shift was opened at.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets the device the shift was opened on.</summary>
    public DeviceId DeviceId { get; private set; }

    /// <summary>Gets the cashier who opened the shift.</summary>
    public UserId CashierUserId { get; private set; }

    /// <summary>Gets the opening cash drawer float.</summary>
    public decimal OpeningFloat { get; private set; }

    /// <summary>Gets the business date the shift opened on.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>Gets the instant the shift was opened.</summary>
    public DateTimeOffset OpenedAtUtc { get; private set; }

    /// <summary>Gets the lifecycle status.</summary>
    public ShiftStatus Status { get; private set; }

    /// <summary>Gets the instant the shift was closed, or null if still open.</summary>
    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>Gets the cash the cashier declared before counting, or null if not yet declared.</summary>
    public decimal? DeclaredCash { get; private set; }

    /// <summary>Gets the cash the cashier counted, or null if not yet counted.</summary>
    public decimal? CountedCash { get; private set; }

    /// <summary>Gets the computed cash variance: counted minus expected, or null if not yet closed.</summary>
    public decimal? CashVariance { get; private set; }

    /// <summary>
    /// Gets whether the worker force-closed this shift because it stayed open
    /// past its location's <c>MaxShiftHours</c> (POS.md §1). A force-closed
    /// shift never had its drawer counted, so <see cref="CashVariance"/> is null
    /// and it requires a review reason before it may be reconciled.
    /// </summary>
    public bool IsForceClosed { get; private set; }

    /// <summary>
    /// Opens a new cashier shift. The caller (device) allocates the SHF number
    /// and the opening instant so the counter advances exactly once.
    /// </summary>
    public static Result<CashierShift> Open(
        DocumentNumber number,
        LocationId locationId,
        DeviceId deviceId,
        UserId cashierUserId,
        decimal openingFloat,
        DateOnly businessDate,
        DateTimeOffset openedAtUtc)
    {
        List<Error> errors = [];

        if (string.IsNullOrWhiteSpace(number.Value))
        {
            errors.Add(ShiftErrors.NumberInvalid);
        }

        if (locationId == LocationId.Empty)
        {
            errors.Add(ShiftErrors.LocationRequired);
        }

        if (deviceId == DeviceId.Empty)
        {
            errors.Add(ShiftErrors.DeviceRequired);
        }

        if (cashierUserId == UserId.Empty)
        {
            errors.Add(ShiftErrors.CashierRequired);
        }

        if (openingFloat < 0m)
        {
            errors.Add(ShiftErrors.OpeningFloatInvalid);
        }

        if (businessDate == default)
        {
            errors.Add(ShiftErrors.BusinessDateRequired);
        }

        if (openedAtUtc == default)
        {
            errors.Add(ShiftErrors.OpenedAtRequired);
        }

        if (errors.Count > 0)
        {
            return Result<CashierShift>.Failure(errors);
        }

        CashierShiftId id = CashierShiftId.New();

        return Result<CashierShift>.Success(
            new CashierShift(id, number, locationId, deviceId, cashierUserId, openingFloat, businessDate, openedAtUtc));
    }

    /// <summary>
    /// Temporarily suspends the shift while the device is locked. The shift may
    /// be resumed to continue accepting sales.
    /// </summary>
    public Result Suspend()
    {
        if (Status != ShiftStatus.Open)
        {
            return Result.Failure(ShiftErrors.ShiftInvalidState(ShiftStatus.Open, Status));
        }

        Status = ShiftStatus.Suspended;
        return Result.Success();
    }

    /// <summary>Resumes a suspended shift, returning it to <see cref="ShiftStatus.Open"/>.</summary>
    public Result Resume()
    {
        if (Status != ShiftStatus.Suspended)
        {
            return Result.Failure(ShiftErrors.ShiftInvalidState(ShiftStatus.Suspended, Status));
        }

        Status = ShiftStatus.Open;
        return Result.Success();
    }

    /// <summary>
    /// The cashier declares the cash in the drawer, moving the shift into
    /// <see cref="ShiftStatus.PendingClose"/> so the counting and closure can follow.
    /// </summary>
    /// <param name="declared">The cash declared, greater than or equal to zero.</param>
    public Result DeclareCash(decimal declared)
    {
        if (Status is not (ShiftStatus.Open or ShiftStatus.Suspended))
        {
            return Result.Failure(ShiftErrors.ShiftInvalidState(ShiftStatus.Open, Status));
        }

        if (declared < 0m)
        {
            return Result.Failure(ShiftErrors.CashAmountInvalid(declared));
        }

        DeclaredCash = decimal.Round(declared, Money.StorageScale, Money.IntermediateRounding);
        Status = ShiftStatus.PendingClose;
        return Result.Success();
    }

    /// <summary>
    /// Closes the shift after the cash has been counted. The expected cash is
    /// <c>OpeningFloat + CashSales - CashRefunds - Payouts</c>; the variance
    /// records the difference from the actual cash counted (POS.md §1).
    /// </summary>
    /// <param name="countedCash">The cash actually counted in the drawer.</param>
    /// <param name="cashSales">Total cash received for sales during the shift.</param>
    /// <param name="cashRefunds">Total cash paid out as refunds during the shift.</param>
    /// <param name="payouts">Total petty-cash payouts during the shift.</param>
    /// <param name="closedAtUtc">The instant of closure.</param>
    public Result Close(decimal countedCash, decimal cashSales, decimal cashRefunds, decimal payouts, DateTimeOffset closedAtUtc)
    {
        if (Status != ShiftStatus.PendingClose)
        {
            return Result.Failure(ShiftErrors.ShiftInvalidState(ShiftStatus.PendingClose, Status));
        }

        if (countedCash < 0m)
        {
            return Result.Failure(ShiftErrors.CashAmountInvalid(countedCash));
        }

        if (closedAtUtc == default)
        {
            return Result.Failure(ShiftErrors.ClosedAtRequired);
        }

        decimal expected = OpeningFloat + cashSales - cashRefunds - payouts;
        decimal variance = decimal.Round(countedCash - expected, Money.StorageScale, Money.IntermediateRounding);

        CountedCash = decimal.Round(countedCash, Money.StorageScale, Money.IntermediateRounding);
        CashVariance = variance;
        ClosedAtUtc = closedAtUtc;
        Status = ShiftStatus.Closed;
        return Result.Success();
    }

    /// <summary>
    /// Worker closes a shift that stayed open past its location's
    /// <c>MaxShiftHours</c> (POS.md §1): the shift is closed with no count and
    /// flagged so it cannot silently absorb the next day's sales. The closure
    /// records no variance; a manager must still reconcile it with a reason.
    /// </summary>
    /// <param name="closedAtUtc">The instant of closure.</param>
    public Result ForceClose(DateTimeOffset closedAtUtc)
    {
        if (Status is not (ShiftStatus.Open or ShiftStatus.Suspended))
        {
            return Result.Failure(ShiftErrors.ShiftInvalidState(ShiftStatus.Open, Status));
        }

        if (closedAtUtc == default)
        {
            return Result.Failure(ShiftErrors.ClosedAtRequired);
        }

        ClosedAtUtc = closedAtUtc;
        IsForceClosed = true;
        Status = ShiftStatus.Closed;
        return Result.Success();
    }

    /// <summary>
    /// Manager review completes the lifecycle by marking the shift reconciled
    /// (POS.md §1). A shift is only reconciled when its cash variance lies
    /// within the location's tolerance, or a reason recording the manager's
    /// review has been provided. A force-closed shift has no variance to accept,
    /// so it always requires a reason; the strictest configuration tolerates no
    /// variance at all.
    /// </summary>
    /// <param name="varianceThreshold">
    /// The location's <c>CashVarianceThreshold</c>: the absolute variance a
    /// closed shift may carry without a review reason. Negative values are
    /// treated as zero.
    /// </param>
    /// <param name="reason">The manager's reason, when one is required.</param>
    public Result Reconcile(decimal varianceThreshold, string? reason)
    {
        if (Status != ShiftStatus.Closed)
        {
            return Result.Failure(ShiftErrors.ShiftInvalidState(ShiftStatus.Closed, Status));
        }

        decimal threshold = Math.Max(0m, varianceThreshold);
        bool varianceAccepted = CashVariance is not null && Math.Abs(CashVariance.Value) <= threshold;
        bool hasReason = !string.IsNullOrWhiteSpace(reason);

        if (!varianceAccepted && !hasReason)
        {
            return Result.Failure(ShiftErrors.ReconcileReasonRequired);
        }

        Status = ShiftStatus.Reconciled;
        return Result.Success();
    }
}