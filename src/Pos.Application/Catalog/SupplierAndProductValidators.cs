using FluentValidation;
using Pos.Domain.Catalog;

namespace Pos.Application.Catalog;

/// <summary>Validates <see cref="CreateSupplierCommand"/>.</summary>
public sealed class CreateSupplierCommandValidator : AbstractValidator<CreateSupplierCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateSupplierCommandValidator()
    {
        RuleFor(c => c.Code)
            .NotEmpty().WithErrorCode("supplier.code_invalid")
            .MaximumLength(Supplier.CodeMaxLength).WithErrorCode("supplier.code_invalid");

        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("supplier.name_invalid")
            .MaximumLength(Supplier.NameMaxLength).WithErrorCode("supplier.name_invalid");

        RuleFor(c => c.PaymentTermsDays)
            .InclusiveBetween(0, 365).WithErrorCode("supplier.payment_terms_invalid");

        RuleFor(c => c.LeadTimeDays)
            .InclusiveBetween(0, 365).WithErrorCode("supplier.lead_time_invalid");
    }
}

/// <summary>Validates <see cref="CreateProductCommand"/>.</summary>
public sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateProductCommandValidator()
    {
        RuleFor(c => c.Sku)
            .NotEmpty().WithErrorCode("catalog.sku_required")
            .MaximumLength(Sku.MaxLength).WithErrorCode("catalog.sku_too_long");

        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("product.name_required");

        RuleFor(c => c.CategoryId)
            .Must(id => !id.IsEmpty).WithErrorCode("product.category_required");

        RuleFor(c => c.BaseUnitOfMeasureId)
            .Must(id => !id.IsEmpty).WithErrorCode("product.base_uom_required");

        RuleFor(c => c.DefaultPurchaseCost)
            .GreaterThanOrEqualTo(0m).WithErrorCode("product.cost_negative");

        When(c => c.TracksExpiry, () =>
        {
            RuleFor(c => c.ShelfLifeDays)
                .NotNull().WithErrorCode("product.shelf_life_required")
                .GreaterThan(0).WithErrorCode("product.shelf_life_required");
        });
    }
}