using FluentAssertions;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Tests <see cref="OpenShiftCommandValidator"/>.</summary>
public sealed class OpenShiftCommandValidatorTests
{
    private readonly OpenShiftCommandValidator _validator = new();
    private static readonly DateOnly BusinessDate = new(2026, 9, 14);

    [Fact]
    public void ValidCommand_IsAccepted()
    {
        _validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyNumber_IsRejected()
    {
        var command = ValidCommand() with { Number = default };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == ShiftErrors.NumberInvalid.Code);
    }

    [Fact]
    public void CentrallyNumberedShift_IsRejected()
    {
        var command = ValidCommand() with
        {
            Number = DocumentNumber.Create(DocumentType.CashierShift, 2026, 1),
        };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == ShiftErrors.NumberInvalid.Code);
    }

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = ValidCommand() with { LocationId = LocationId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == ShiftErrors.LocationRequired.Code);
    }

    [Fact]
    public void ZeroBusinessDate_IsRejected()
    {
        var command = ValidCommand() with { BusinessDate = default };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == ShiftErrors.BusinessDateRequired.Code);
    }

    [Fact]
    public void NegativeOpeningFloat_IsRejected()
    {
        var command = ValidCommand() with { OpeningFloat = -1m };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == ShiftErrors.OpeningFloatInvalid.Code);
    }

    private static OpenShiftCommand ValidCommand() => new(
        DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D01", 1),
        LocationId.New(),
        BusinessDate,
        100m);
}

/// <summary>Tests <see cref="CloseShiftCommandValidator"/>.</summary>
public sealed class CloseShiftCommandValidatorTests
{
    private readonly CloseShiftCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_IsAccepted()
    {
        _validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyShiftId_IsRejected()
    {
        var command = ValidCommand() with { ShiftId = CashierShiftId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ShiftRequired.Code);
    }

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = ValidCommand() with { LocationId = LocationId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.LocationRequired.Code);
    }

    [Fact]
    public void NegativeDeclaredCash_IsRejected()
    {
        var command = ValidCommand() with { DeclaredCash = -1m };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == "shift.cash_amount_invalid");
    }

    [Fact]
    public void NegativeCountedCash_IsRejected()
    {
        var command = ValidCommand() with { CountedCash = -1m };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == "shift.cash_amount_invalid");
    }

    private static CloseShiftCommand ValidCommand() => new(
        CashierShiftId.New(),
        LocationId.New(),
        500m,
        550m);
}

/// <summary>Tests <see cref="SuspendShiftCommandValidator"/>.</summary>
public sealed class SuspendShiftCommandValidatorTests
{
    private readonly SuspendShiftCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_IsAccepted()
    {
        _validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyShiftId_IsRejected()
    {
        var command = ValidCommand() with { ShiftId = CashierShiftId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ShiftRequired.Code);
    }

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = ValidCommand() with { LocationId = LocationId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.LocationRequired.Code);
    }

    private static SuspendShiftCommand ValidCommand() => new(
        CashierShiftId.New(),
        LocationId.New());
}

/// <summary>Tests <see cref="ResumeShiftCommandValidator"/>.</summary>
public sealed class ResumeShiftCommandValidatorTests
{
    private readonly ResumeShiftCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_IsAccepted()
    {
        _validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyShiftId_IsRejected()
    {
        var command = ValidCommand() with { ShiftId = CashierShiftId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ShiftRequired.Code);
    }

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = ValidCommand() with { LocationId = LocationId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.LocationRequired.Code);
    }

    private static ResumeShiftCommand ValidCommand() => new(
        CashierShiftId.New(),
        LocationId.New());
}

/// <summary>Tests <see cref="ReconcileShiftCommandValidator"/>.</summary>
public sealed class ReconcileShiftCommandValidatorTests
{
    private readonly ReconcileShiftCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_IsAccepted()
    {
        _validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyShiftId_IsRejected()
    {
        var command = ValidCommand() with { ShiftId = CashierShiftId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.ShiftRequired.Code);
    }

    [Fact]
    public void EmptyLocationId_IsRejected()
    {
        var command = ValidCommand() with { LocationId = LocationId.Empty };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == SaleErrors.LocationRequired.Code);
    }

    [Fact]
    public void NullReason_IsAccepted()
    {
        var command = ValidCommand() with { Reason = null };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ReasonAtMaxLength_IsAccepted()
    {
        var command = ValidCommand() with { Reason = new string('x', ReconcileShiftCommandValidator.ReasonMaxLength) };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ReasonOverMaxLength_IsRejected()
    {
        var command = ValidCommand() with { Reason = new string('x', ReconcileShiftCommandValidator.ReasonMaxLength + 1) };

        _validator.Validate(command)
            .Errors.Should().ContainSingle(e => e.ErrorCode == ShiftCommandErrors.ReconcileReasonTooLong(ReconcileShiftCommandValidator.ReasonMaxLength).Code);
    }

    private static ReconcileShiftCommand ValidCommand() => new(
        CashierShiftId.New(),
        LocationId.New(),
        "Shortage in drawer");
}