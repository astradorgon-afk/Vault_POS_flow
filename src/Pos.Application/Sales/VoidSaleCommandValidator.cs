using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="VoidSaleCommand"/>.</summary>
public sealed class VoidSaleCommandValidator : AbstractValidator<VoidSaleCommand>
{
    /// <summary>Initializes the validator.</summary>
    public VoidSaleCommandValidator()
    {
        RuleFor(c => c.EventId)
            .NotEqual(EventId.Empty)
            .WithErrorCode(SaleErrors.EventRequired.Code);

        RuleFor(c => c.SaleId)
            .NotEqual(SaleId.Empty)
            .WithErrorCode(VoidSaleCommandErrors.SaleRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);

        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SaleErrors.ShiftRequired.Code);

        RuleFor(c => c.DeviceId)
            .NotEqual(DeviceId.Empty)
            .WithErrorCode(SaleErrors.DeviceRequired.Code);

        RuleFor(c => c.BusinessDate)
            .NotEqual(default(DateOnly))
            .WithErrorCode(SaleErrors.BusinessDateRequired.Code);

        RuleFor(c => c.VoidedAtUtc)
            .NotEqual(default(DateTimeOffset))
            .WithErrorCode(SaleErrors.VoidStampRequired.Code);

        RuleFor(c => c.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason))
            .WithErrorCode(SaleErrors.VoidReasonInvalid(Sale.VoidReasonMaxLength).Code);

        RuleFor(c => c.Reason)
            .MaximumLength(Sale.VoidReasonMaxLength)
            .WithErrorCode(SaleErrors.VoidReasonInvalid(Sale.VoidReasonMaxLength).Code);
    }
}