using FluentValidation;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>Validates <see cref="CompleteSaleCommand"/>.</summary>
public sealed class CompleteSaleCommandValidator : AbstractValidator<CompleteSaleCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CompleteSaleCommandValidator()
    {
        RuleFor(c => c.Number)
            .Must(n => n.DeviceShortCode is not null)
            .WithErrorCode(SaleCommandErrors.NumberInvalid.Code);

        RuleFor(c => c.EventId)
            .NotEqual(EventId.Empty)
            .WithErrorCode(SaleErrors.EventRequired.Code);

        RuleFor(c => c.LocationId)
            .NotEqual(LocationId.Empty)
            .WithErrorCode(SaleErrors.LocationRequired.Code);

        RuleFor(c => c.CashierShiftId)
            .NotEqual(CashierShiftId.Empty)
            .WithErrorCode(SaleErrors.ShiftRequired.Code);

        RuleFor(c => c.DeviceId)
            .NotEqual(DeviceId.Empty)
            .WithErrorCode(SaleErrors.DeviceRequired.Code);

        RuleFor(c => c.CashierId)
            .NotEqual(UserId.Empty)
            .WithErrorCode(SaleErrors.CashierRequired.Code);

        RuleFor(c => c.BusinessDate)
            .NotEqual(default(DateOnly))
            .WithErrorCode(SaleErrors.BusinessDateRequired.Code);

        RuleFor(c => c.CompletedAtUtc)
            .NotEqual(default(DateTimeOffset))
            .WithErrorCode(SaleErrors.CompletedAtRequired.Code);

        RuleFor(c => c.Lines)
            .NotEmpty()
            .WithErrorCode(SaleErrors.ItemsRequired.Code);

        RuleFor(c => c.Payments)
            .NotEmpty()
            .WithErrorCode(SaleErrors.PaymentsRequired.Code);

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId)
                .NotEqual(ProductId.Empty)
                .WithErrorCode(SaleErrors.ItemProductRequired.Code);

            line.RuleFor(l => l.Quantity)
                .GreaterThan(0m)
                .WithErrorCode(SaleErrors.ItemQuantityInvalid.Code);

            line.RuleFor(l => l.UnitOfMeasureId)
                .NotEqual(UnitOfMeasureId.Empty)
                .WithErrorCode("sale.item.uom_required");

            line.RuleFor(l => l.Discount)
                .GreaterThanOrEqualTo(0m)
                .WithErrorCode(SaleErrors.ItemDiscountInvalid.Code);

            line.RuleFor(l => l.UnitPriceOverride)
                .GreaterThanOrEqualTo(0m)
                .When(l => l.UnitPriceOverride is not null)
                .WithErrorCode(SaleErrors.ItemPriceNegative.Code);

            line.RuleFor(l => l.Barcode)
                .MaximumLength(SaleItem.BarcodeMaxLength)
                .WithErrorCode(SaleErrors.ItemBarcodeTooLong(SaleItem.BarcodeMaxLength).Code);

            line.When(l => l.Discount > 0m, () => line.RuleFor(l => l.DiscountAuthorizedByUserId)
                .NotNull()
                .WithErrorCode(SaleErrors.ItemDiscountAuthorizerRequired.Code));

            line.When(l => l.UnitPriceOverride is not null, () => line.RuleFor(l => l.PriceOverrideAuthorizedByUserId)
                .NotNull()
                .WithErrorCode(SaleErrors.ItemPriceOverrideAuthorizerRequired.Code));

            line.When(l => l.AllowExpiredOverride, () =>
            {
                line.RuleFor(l => l.ExpiredOverrideReason)
                    .NotEmpty()
                    .WithErrorCode(SaleCommandErrors.ExpiredOverrideReasonRequired.Code);

                line.RuleFor(l => l.ExpiredOverrideReason)
                    .MaximumLength(CompleteSaleLine.ExpiredOverrideReasonMaxLength)
                    .WithErrorCode(
                        SaleCommandErrors.ExpiredOverrideReasonTooLong(CompleteSaleLine.ExpiredOverrideReasonMaxLength).Code);
            });
        });

        RuleForEach(c => c.Payments).ChildRules(payment =>
        {
            payment.RuleFor(p => p.Method)
                .IsInEnum()
                .WithErrorCode(SaleErrors.PaymentMethodUnknown.Code);

            payment.RuleFor(p => p.Amount)
                .GreaterThan(0m)
                .WithErrorCode(SaleErrors.PaymentAmountInvalid.Code);

            payment.RuleFor(p => p.ProviderReference)
                .MaximumLength(Payment.ProviderReferenceMaxLength)
                .WithErrorCode(SaleErrors.PaymentReferenceTooLong(Payment.ProviderReferenceMaxLength).Code);

            payment.When(p => p.Method == PaymentMethod.Cash, () => payment.RuleFor(p => p.Tendered)
                .NotNull()
                .WithErrorCode(SaleErrors.PaymentTenderedRequired.Code));
        });
    }
}