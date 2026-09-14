using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>
/// Tests for the <see cref="CashierShift"/> aggregate: factory validation,
/// state transitions, and variance arithmetic (POS.md §1).
/// </summary>
public sealed class CashierShiftTests
{
    private static readonly LocationId Store = LocationId.New();
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly UserId Cashier = UserId.New();
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    private static DocumentNumber NewNumber()
        => DocumentNumber.Create(DocumentType.CashierShift, 2026, 1);

    #region Open — happy path

    [Fact]
    public void Open_SetsAllPropertiesAndOpenStatus()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now);

        result.IsSuccess.Should().BeTrue();
        CashierShift shift = result.Value;
        shift.Number.Should().Be("SHF-2026-000001");
        shift.LocationId.Should().Be(Store);
        shift.DeviceId.Should().Be(Device);
        shift.CashierUserId.Should().Be(Cashier);
        shift.OpeningFloat.Should().Be(200m);
        shift.BusinessDate.Should().Be(BusinessDate);
        shift.OpenedAtUtc.Should().Be(Now);
        shift.Status.Should().Be(ShiftStatus.Open);
        shift.ClosedAtUtc.Should().BeNull();
        shift.DeclaredCash.Should().BeNull();
        shift.CountedCash.Should().BeNull();
        shift.CashVariance.Should().BeNull();
    }

    [Fact]
    public void Open_WithZeroFloat_Succeeds()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, Cashier, 0m, BusinessDate, Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.OpeningFloat.Should().Be(0m);
    }

    [Fact]
    public void Open_RoundsOpeningFloat()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, Cashier, 123.45678m, BusinessDate, Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.OpeningFloat.Should().Be(123.4568m);
    }

    [Fact]
    public void Open_AllocatesUniqueIdentifier()
    {
        CashierShift a = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        CashierShift b = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        a.Id.Should().NotBe(b.Id);
    }

    #endregion

    #region Open — factory guards

    [Fact]
    public void Open_EmptyNumber_ReturnsNumberInvalid()
    {
        Result<CashierShift> result = CashierShift.Open(
            default(DocumentNumber), Store, Device, Cashier, 200m, BusinessDate, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.number_invalid");
    }

    [Fact]
    public void Open_EmptyLocation_ReturnsLocationRequired()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), LocationId.Empty, Device, Cashier, 200m, BusinessDate, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.location_required");
    }

    [Fact]
    public void Open_EmptyDevice_ReturnsDeviceRequired()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, DeviceId.Empty, Cashier, 200m, BusinessDate, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.device_required");
    }

    [Fact]
    public void Open_EmptyCashier_ReturnsCashierRequired()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, UserId.Empty, 200m, BusinessDate, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.cashier_required");
    }

    [Fact]
    public void Open_NegativeFloat_ReturnsOpeningFloatInvalid()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, Cashier, -1m, BusinessDate, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.opening_float_invalid");
    }

    [Fact]
    public void Open_DefaultBusinessDate_ReturnsBusinessDateRequired()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, Cashier, 200m, default, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.business_date_required");
    }

    [Fact]
    public void Open_DefaultOpenedAt_ReturnsOpenedAtRequired()
    {
        Result<CashierShift> result = CashierShift.Open(
            NewNumber(), Store, Device, Cashier, 200m, BusinessDate, default);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.opened_at_required");
    }

    [Fact]
    public void Open_MultipleInvalidations_ReturnsAllErrors()
    {
        Result<CashierShift> result = CashierShift.Open(
            default(DocumentNumber), LocationId.Empty, DeviceId.Empty, UserId.Empty, -1m, default, default);

        result.IsFailure.Should().BeTrue();
        result.Errors.Count.Should().BeGreaterOrEqualTo(6);
    }

    #endregion

    #region Suspend / Resume

    [Fact]
    public void Suspend_FromOpen_SetsSuspended()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.Suspend();

        result.IsSuccess.Should().BeTrue();
        shift.Status.Should().Be(ShiftStatus.Suspended);
    }

    [Fact]
    public void Suspend_FromPendingClose_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.DeclareCash(100m);

        Result result = shift.Suspend();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    [Fact]
    public void Suspend_FromClosed_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.DeclareCash(100m);
        shift.Close(100m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddHours(8));

        Result result = shift.Suspend();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    [Fact]
    public void Resume_FromSuspended_SetsOpen()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.Suspend();

        Result result = shift.Resume();

        result.IsSuccess.Should().BeTrue();
        shift.Status.Should().Be(ShiftStatus.Open);
    }

    [Fact]
    public void Resume_FromOpen_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.Resume();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    #endregion

    #region DeclareCash

    [Fact]
    public void DeclareCash_FromOpen_SetsPendingClose()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.DeclareCash(150m);

        result.IsSuccess.Should().BeTrue();
        shift.Status.Should().Be(ShiftStatus.PendingClose);
        shift.DeclaredCash.Should().Be(150m);
    }

    [Fact]
    public void DeclareCash_FromSuspended_SetsPendingClose()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.Suspend();

        Result result = shift.DeclareCash(100m);

        result.IsSuccess.Should().BeTrue();
        shift.Status.Should().Be(ShiftStatus.PendingClose);
    }

    [Fact]
    public void DeclareCash_NegativeAmount_ReturnsCashAmountInvalid()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.DeclareCash(-1m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.cash_amount_invalid");
    }

    [Fact]
    public void DeclareCash_ZeroAmount_Succeeds()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.DeclareCash(0m);

        result.IsSuccess.Should().BeTrue();
        shift.DeclaredCash.Should().Be(0m);
    }

    [Fact]
    public void DeclareCash_RoundsValue()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.DeclareCash(123.45678m);

        result.IsSuccess.Should().BeTrue();
        shift.DeclaredCash.Should().Be(123.4568m);
    }

    [Fact]
    public void DeclareCash_FromPendingClose_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.DeclareCash(100m);

        Result result = shift.DeclareCash(100m);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void DeclareCash_FromClosed_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.DeclareCash(100m);
        shift.Close(100m, 0m, 0m, 0m, Now.AddHours(8));

        Result result = shift.DeclareCash(100m);

        result.IsFailure.Should().BeTrue();
    }

    #endregion

    #region Close

    [Fact]
    public void Close_FromPendingClose_SetsClosedAndComputesVariance()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(300m);
        DateTimeOffset closedAt = Now.AddHours(8);

        Result result = shift.Close(countedCash: 305m, cashSales: 100m, cashRefunds: 0m, payouts: 5m, closedAt);

        result.IsSuccess.Should().BeTrue();
        shift.Status.Should().Be(ShiftStatus.Closed);
        shift.ClosedAtUtc.Should().Be(closedAt);
        shift.CountedCash.Should().Be(305m);
        // Expected: 200 + 100 - 0 - 5 = 295; variance: 305 - 295 = 10
        shift.CashVariance.Should().Be(10m);
    }

    [Fact]
    public void Close_NegativeVariance_ComputesCorrectly()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(280m);

        Result result = shift.Close(countedCash: 280m, cashSales: 100m, cashRefunds: 0m, payouts: 0m, Now.AddHours(8));

        result.IsSuccess.Should().BeTrue();
        // Expected: 200 + 100 - 0 - 0 = 300; variance: 280 - 300 = -20
        shift.CashVariance.Should().Be(-20m);
    }

    [Fact]
    public void Close_ExactMatch_VarianceIsZero()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(300m);

        Result result = shift.Close(countedCash: 300m, cashSales: 100m, cashRefunds: 0m, payouts: 0m, Now.AddHours(8));

        result.IsSuccess.Should().BeTrue();
        shift.CashVariance.Should().Be(0m);
    }

    [Fact]
    public void Close_RoundsCountedCashAndVariance()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(300m);

        Result result = shift.Close(countedCash: 305.12345m, cashSales: 100m, cashRefunds: 0m, payouts: 0m, Now.AddHours(8));

        result.IsSuccess.Should().BeTrue();
        // Expected: 300; both the counted amount and the variance are rounded to
        // StorageScale with ToEven at the exact midpoint.
        shift.CountedCash.Should().Be(305.1234m);
        shift.CashVariance.Should().Be(5.1234m);
    }

    [Fact]
    public void Close_NegativeCounted_ReturnsCashAmountInvalid()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(0m);

        Result result = shift.Close(countedCash: -1m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddHours(8));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.cash_amount_invalid");
    }

    [Fact]
    public void Close_DefaultClosedAt_ReturnsClosedAtRequired()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(0m);

        Result result = shift.Close(countedCash: 0m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, default);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.closed_at_required");
    }

    [Fact]
    public void Close_FromOpen_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;

        Result result = shift.Close(200m, 0m, 0m, 0m, Now.AddHours(8));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    [Fact]
    public void Close_FromSuspended_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.Suspend();

        Result result = shift.Close(200m, 0m, 0m, 0m, Now.AddHours(8));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    [Fact]
    public void Close_FromClosed_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(200m);
        shift.Close(200m, 0m, 0m, 0m, Now.AddHours(8));

        Result result = shift.Close(200m, 0m, 0m, 0m, Now.AddHours(9));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Close_WithCashRefunds_ComputesVarianceCorrectly()
    {
        // Float: 200, Sales: 150, Refunds: 30, Payouts: 10
        // Expected: 200 + 150 - 30 - 10 = 310
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(310m);

        Result result = shift.Close(countedCash: 310m, cashSales: 150m, cashRefunds: 30m, payouts: 10m, Now.AddHours(8));

        result.IsSuccess.Should().BeTrue();
        shift.CashVariance.Should().Be(0m);
    }

    [Fact]
    public void Close_ZeroCountedZeroExpected_VarianceIsZero()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 0m, BusinessDate, Now).Value;
        shift.DeclareCash(0m);

        Result result = shift.Close(countedCash: 0m, cashSales: 0m, cashRefunds: 0m, payouts: 0m, Now.AddHours(8));

        result.IsSuccess.Should().BeTrue();
        shift.CashVariance.Should().Be(0m);
    }

    #endregion

    #region Reconcile

    [Fact]
    public void Reconcile_FromClosed_SetsReconciled()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(300m);
        shift.Close(300m, 100m, 0m, 0m, Now.AddHours(8));

        Result result = shift.Reconcile();

        result.IsSuccess.Should().BeTrue();
        shift.Status.Should().Be(ShiftStatus.Reconciled);
    }

    [Fact]
    public void Reconcile_FromOpen_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;

        Result result = shift.Reconcile();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    [Fact]
    public void Reconcile_FromPendingClose_ReturnsInvalidState()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 200m, BusinessDate, Now).Value;
        shift.DeclareCash(200m);

        Result result = shift.Reconcile();

        result.IsFailure.Should().BeTrue();
    }

    #endregion

    #region State-machine exhaustiveness — cannot skip steps

    [Fact]
    public void CannotCloseWithoutDeclare()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.Close(100m, 0m, 0m, 0m, Now.AddHours(8));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "shift.invalid_state");
    }

    [Fact]
    public void CannotReconcileWithoutClose()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.DeclareCash(100m);

        Result result = shift.Reconcile();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void CannotSuspendAfterDeclare()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;
        shift.DeclareCash(100m);

        Result result = shift.Suspend();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void CannotResumeFromOpen()
    {
        CashierShift shift = CashierShift.Open(NewNumber(), Store, Device, Cashier, 100m, BusinessDate, Now).Value;

        Result result = shift.Resume();

        result.IsFailure.Should().BeTrue();
    }

    #endregion
}