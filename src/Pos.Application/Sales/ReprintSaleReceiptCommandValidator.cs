using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="ReprintSaleReceiptCommand"/>.</summary>
public sealed class ReprintSaleReceiptCommandValidator : AbstractValidator<ReprintSaleReceiptCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ReprintSaleReceiptCommandValidator()
    {
        RuleFor(c => c.SaleId)
            .NotEqual(SaleId.Empty)
            .WithErrorCode(ReprintCommandErrors.SaleRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);

        RuleFor(c => c.DeviceId)
            .NotEqual(DeviceId.Empty)
            .WithErrorCode(SaleErrors.DeviceRequired.Code);

        RuleFor(c => c.ReprintedAtUtc)
            .NotEqual(default(DateTimeOffset))
            .WithErrorCode(SaleReceiptErrors.PrintedAtRequired.Code);

        RuleFor(c => c.ReprintedByUserId)
            .NotEqual(UserId.Empty)
            .WithErrorCode(SaleReceiptErrors.PrintedByRequired.Code);

        RuleFor(c => c.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason))
            .WithErrorCode(SaleReceiptErrors.ReasonRequired.Code);

        RuleFor(c => c.Reason)
            .MaximumLength(SaleReceiptPrint.ReasonMaxLength)
            .WithErrorCode(SaleReceiptErrors.ReasonTooLong(SaleReceiptPrint.ReasonMaxLength).Code);
    }
}