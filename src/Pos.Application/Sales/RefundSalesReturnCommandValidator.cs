using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="RefundSalesReturnCommand"/>.</summary>
public sealed class RefundSalesReturnCommandValidator : AbstractValidator<RefundSalesReturnCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RefundSalesReturnCommandValidator()
    {
        RuleFor(c => c.EventId)
            .NotEqual(EventId.Empty)
            .WithErrorCode(SalesReturnErrors.RefundEventRequired.Code);

        RuleFor(c => c.SaleId)
            .NotEqual(SaleId.Empty)
            .WithErrorCode(RefundCommandErrors.SaleRequired.Code);

        RuleFor(c => c.SalesReturnId)
            .NotEqual(SalesReturnId.Empty)
            .WithErrorCode(RefundCommandErrors.ReturnRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(RefundCommandErrors.LocationRequired.Code);

        RuleFor(c => c.ShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SalesReturnErrors.RefundShiftRequired.Code);

        RuleFor(c => c.DeviceId)
            .NotEqual(DeviceId.Empty)
            .WithErrorCode(SalesReturnErrors.RefundDeviceRequired.Code);

        RuleFor(c => c.Method)
            .IsInEnum()
            .WithErrorCode(SalesReturnErrors.RefundMethodUnknown.Code);

        RuleFor(c => c.Amount)
            .GreaterThan(0m)
            .WithErrorCode(SalesReturnErrors.RefundAmountInvalid.Code);

        RuleFor(c => c.ProviderReference)
            .MaximumLength(Refund.ProviderReferenceMaxLength)
            .WithErrorCode(SalesReturnErrors.RefundReferenceTooLong(Refund.ProviderReferenceMaxLength).Code);

        RuleFor(c => c.RefundedAtUtc)
            .NotEqual(default(DateTimeOffset))
            .WithErrorCode(SalesReturnErrors.RefundStampRequired.Code);

        RuleFor(c => c.RefundedByUserId)
            .NotEqual(UserId.Empty)
            .WithErrorCode(SalesReturnErrors.RefundByRequired.Code);

        RuleFor(c => c.Tendered)
            .NotNull()
            .When(c => c.Method == PaymentMethod.Cash)
            .WithErrorCode(SalesReturnErrors.RefundTenderedRequired.Code);
    }
}