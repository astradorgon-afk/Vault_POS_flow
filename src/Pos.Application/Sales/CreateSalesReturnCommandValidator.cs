using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="CreateSalesReturnCommand"/>.</summary>
public sealed class CreateSalesReturnCommandValidator : AbstractValidator<CreateSalesReturnCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateSalesReturnCommandValidator()
    {
        RuleFor(c => c.Number)
            .Must(n => n.DeviceShortCode is not null)
            .WithErrorCode(ReturnCommandErrors.NumberInvalid.Code);

        RuleFor(c => c.EventId)
            .NotEqual(EventId.Empty)
            .WithErrorCode(SalesReturnErrors.EventRequired.Code);

        RuleFor(c => c.SaleId)
            .NotEqual(SaleId.Empty)
            .WithErrorCode(SalesReturnErrors.SaleRequired.Code);

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

        RuleFor(c => c.Lines)
            .NotEmpty()
            .WithErrorCode(SalesReturnErrors.ItemsRequired.Code);

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId)
                .NotEqual(ProductId.Empty)
                .WithErrorCode(ReturnCommandErrors.ItemProductRequired.Code);

            line.RuleFor(l => l.Quantity)
                .GreaterThan(0m)
                .WithErrorCode(SalesReturnErrors.QuantityInvalid.Code);
        });
    }
}