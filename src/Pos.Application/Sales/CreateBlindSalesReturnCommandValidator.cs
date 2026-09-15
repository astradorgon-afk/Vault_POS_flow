using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="CreateBlindSalesReturnCommand"/>.</summary>
public sealed class CreateBlindSalesReturnCommandValidator : AbstractValidator<CreateBlindSalesReturnCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateBlindSalesReturnCommandValidator()
    {
        RuleFor(c => c.EventId)
            .NotEqual(EventId.Empty)
            .WithErrorCode(SalesReturnErrors.EventRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SalesReturnErrors.LocationRequired.Code);

        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SalesReturnErrors.ShiftRequired.Code);

        RuleFor(c => c.DeviceId)
            .NotEqual(DeviceId.Empty)
            .WithErrorCode(SalesReturnErrors.DeviceRequired.Code);

        RuleFor(c => c.BusinessDate)
            .NotEqual(default(DateOnly))
            .WithErrorCode(SalesReturnErrors.BusinessDateRequired.Code);

        RuleFor(c => c.ReturnedAtUtc)
            .NotEqual(default(DateTimeOffset))
            .WithErrorCode(SalesReturnErrors.ReturnedAtRequired.Code);

        RuleFor(c => c.ReturnedByUserId)
            .NotEqual(UserId.Empty)
            .WithErrorCode(SalesReturnErrors.ReturnedByRequired.Code);

        RuleFor(c => c.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason))
            .WithErrorCode(SalesReturnErrors.BlindReasonRequired.Code);

        RuleFor(c => c.Reason)
            .MaximumLength(SalesReturn.BlindReasonMaxLength)
            .WithErrorCode(SalesReturnErrors.BlindReasonTooLong(SalesReturn.BlindReasonMaxLength).Code);

        RuleFor(c => c.Lines)
            .NotNull()
            .NotEmpty()
            .WithErrorCode(SalesReturnErrors.ItemsRequired.Code);
    }
}