using FluentValidation;
using Pos.Domain.Catalog;

namespace Pos.Application.Catalog;

/// <summary>Validates <see cref="CreateCategoryCommand"/>.</summary>
public sealed class CreateCategoryCommandValidator : AbstractValidator<CreateCategoryCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateCategoryCommandValidator()
    {
        RuleFor(c => c.Code)
            .NotEmpty().WithErrorCode("category.code_invalid")
            .MaximumLength(ProductCategory.CodeMaxLength).WithErrorCode("category.code_invalid");

        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("category.name_invalid")
            .MaximumLength(ProductCategory.NameMaxLength).WithErrorCode("category.name_invalid");
    }
}

/// <summary>Validates <see cref="CreateBrandCommand"/>.</summary>
public sealed class CreateBrandCommandValidator : AbstractValidator<CreateBrandCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateBrandCommandValidator()
        => RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("brand.name_invalid")
            .MaximumLength(Brand.NameMaxLength).WithErrorCode("brand.name_invalid");
}

/// <summary>Validates <see cref="CreateUnitOfMeasureCommand"/>.</summary>
public sealed class CreateUnitOfMeasureCommandValidator : AbstractValidator<CreateUnitOfMeasureCommand>
{
    /// <summary>Initializes the validator.</summary>
    public CreateUnitOfMeasureCommandValidator()
    {
        RuleFor(c => c.Code)
            .NotEmpty().WithErrorCode("uom.code_invalid")
            .MaximumLength(UnitOfMeasure.CodeMaxLength).WithErrorCode("uom.code_invalid");

        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("uom.name_invalid")
            .MaximumLength(UnitOfMeasure.NameMaxLength).WithErrorCode("uom.name_invalid");

        RuleFor(c => c.DecimalPlaces)
            .InclusiveBetween(0, 6).WithErrorCode("uom.decimal_places_invalid");
    }
}