using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="OpenShiftCommand"/>.</summary>
public sealed class OpenShiftCommandValidator : AbstractValidator<OpenShiftCommand>
{
    /// <summary>Initializes the validator.</summary>
    public OpenShiftCommandValidator()
    {
        RuleFor(c => c.Number)
            .Must(n => n.DeviceShortCode is not null)
            .WithErrorCode(ShiftErrors.NumberInvalid.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(ShiftErrors.LocationRequired.Code);

        RuleFor(c => c.BusinessDate)
            .NotEqual(default(DateOnly))
            .WithErrorCode(ShiftErrors.BusinessDateRequired.Code);

        RuleFor(c => c.OpeningFloat)
            .GreaterThanOrEqualTo(0m)
            .WithErrorCode(ShiftErrors.OpeningFloatInvalid.Code);
    }
}

/// <summary>Validates <see cref="CloseShiftCommand"/>.</summary>
public sealed class CloseShiftCommandValidator : AbstractValidator<CloseShiftCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CloseShiftCommandValidator()
    {
        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SaleErrors.ShiftRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);

        RuleFor(c => c.DeclaredCash)
            .GreaterThanOrEqualTo(0m)
            .WithErrorCode("shift.cash_amount_invalid");

        RuleFor(c => c.CountedCash)
            .GreaterThanOrEqualTo(0m)
            .WithErrorCode("shift.cash_amount_invalid");
    }
}

/// <summary>Validates <see cref="SuspendShiftCommand"/>.</summary>
public sealed class SuspendShiftCommandValidator : AbstractValidator<SuspendShiftCommand>
{
    /// <summary>Initializes the validator.</summary>
    public SuspendShiftCommandValidator()
    {
        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SaleErrors.ShiftRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);
    }
}

/// <summary>Validates <see cref="ResumeShiftCommand"/>.</summary>
public sealed class ResumeShiftCommandValidator : AbstractValidator<ResumeShiftCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ResumeShiftCommandValidator()
    {
        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SaleErrors.ShiftRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);
    }
}

/// <summary>Validates <see cref="ReconcileShiftCommand"/>.</summary>
public sealed class ReconcileShiftCommandValidator : AbstractValidator<ReconcileShiftCommand>
{
    /// <summary>The longest tolerated review reason.</summary>
    public const int ReasonMaxLength = 200;

    /// <summary>Initializes the validator.</summary>
    public ReconcileShiftCommandValidator()
    {
        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SaleErrors.ShiftRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);

        // Required-ness is a business rule enforced by the shift itself (only a
        // flagged variance needs one); the validator only bounds its length.
        RuleFor(c => c.Reason)
            .MaximumLength(ReasonMaxLength)
            .WithErrorCode(ShiftCommandErrors.ReconcileReasonTooLong(ReasonMaxLength).Code);
    }
}