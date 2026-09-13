using FluentValidation;
using Pos.Domain.Catalog;

namespace Pos.Application.Catalog;

/// <summary>Limits shared by the product curation validators.</summary>
internal static class ProductCurationRules
{
    /// <summary>The longest reason recorded with a change.</summary>
    internal const int ReasonMaxLength = 512;

    /// <summary>The shortest reason that says anything.</summary>
    internal const int ReasonMinLength = 5;

    /// <summary>The longest supplier code the catalogue stores.</summary>
    internal const int SupplierSkuMaxLength = 64;
}

/// <summary>Validates <see cref="UpdateProductCommand"/>.</summary>
public sealed class UpdateProductCommandValidator : AbstractValidator<UpdateProductCommand>
{
    /// <summary>Initializes the validator.</summary>
    public UpdateProductCommandValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty().WithErrorCode("product.name_required")
            .MaximumLength(Product.NameMaxLength).WithErrorCode("product.name_too_long");

        RuleFor(c => c.Description)
            .MaximumLength(Product.DescriptionMaxLength).WithErrorCode("product.description_too_long");

        RuleFor(c => c.CategoryId)
            .Must(id => !id.IsEmpty).WithErrorCode("product.category_required");

        RuleFor(c => c.TaxCode)
            .MaximumLength(Product.TaxCodeMaxLength).WithErrorCode("product.tax_code_too_long");

        RuleFor(c => c.ImageRef)
            .MaximumLength(Product.ImageRefMaxLength).WithErrorCode("product.image_ref_too_long");

        RuleFor(c => c.DefaultPurchaseCost)
            .GreaterThanOrEqualTo(0m).WithErrorCode("product.cost_negative");
    }
}

/// <summary>Validates <see cref="SetProductActivationCommand"/>.</summary>
public sealed class SetProductActivationCommandValidator : AbstractValidator<SetProductActivationCommand>
{
    /// <summary>Initializes the validator.</summary>
    public SetProductActivationCommandValidator()
    {
        When(c => !c.Active, () =>
            RuleFor(c => c.Reason)
                .NotEmpty().WithErrorCode("catalog.reason_required")
                .MinimumLength(ProductCurationRules.ReasonMinLength).WithErrorCode("catalog.reason_required"));

        RuleFor(c => c.Reason)
            .MaximumLength(ProductCurationRules.ReasonMaxLength).WithErrorCode("catalog.reason_too_long");
    }
}

/// <summary>Validates <see cref="AddProductBarcodeCommand"/>.</summary>
public sealed class AddProductBarcodeCommandValidator : AbstractValidator<AddProductBarcodeCommand>
{
    /// <summary>Initializes the validator.</summary>
    public AddProductBarcodeCommandValidator()
    {
        RuleFor(c => c.Barcode)
            .NotEmpty().WithErrorCode("catalog.barcode_required")
            .MaximumLength(Barcode.MaxLength).WithErrorCode("catalog.barcode_too_long");

        RuleFor(c => c.PackQuantity)
            .GreaterThan(0m).WithErrorCode("product.barcode_pack_quantity");
    }
}

/// <summary>Validates <see cref="RetireProductBarcodeCommand"/>.</summary>
public sealed class RetireProductBarcodeCommandValidator : AbstractValidator<RetireProductBarcodeCommand>
{
    /// <summary>Initializes the validator.</summary>
    public RetireProductBarcodeCommandValidator()
    {
        RuleFor(c => c.Barcode)
            .NotEmpty().WithErrorCode("catalog.barcode_required");

        RuleFor(c => c.Reason)
            .NotEmpty().WithErrorCode("catalog.reason_required")
            .MinimumLength(ProductCurationRules.ReasonMinLength).WithErrorCode("catalog.reason_required")
            .MaximumLength(ProductCurationRules.ReasonMaxLength).WithErrorCode("catalog.reason_too_long");
    }
}

/// <summary>Validates <see cref="SetPrimaryProductBarcodeCommand"/>.</summary>
public sealed class SetPrimaryProductBarcodeCommandValidator : AbstractValidator<SetPrimaryProductBarcodeCommand>
{
    /// <summary>Initializes the validator.</summary>
    public SetPrimaryProductBarcodeCommandValidator()
        => RuleFor(c => c.Barcode).NotEmpty().WithErrorCode("catalog.barcode_required");
}

/// <summary>Validates <see cref="ScheduleProductPriceCommand"/>.</summary>
public sealed class ScheduleProductPriceCommandValidator : AbstractValidator<ScheduleProductPriceCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ScheduleProductPriceCommandValidator()
    {
        RuleFor(c => c.Amount)
            .GreaterThanOrEqualTo(0m).WithErrorCode("product.price_negative");

        RuleFor(c => c.LocationId)
            .Must(id => id is null || !id.Value.IsEmpty).WithErrorCode("catalog.location_unknown");

        RuleFor(c => c.EffectiveToUtc)
            .Must((command, to) => to is null || to > (command.EffectiveFromUtc ?? DateTimeOffset.MinValue))
            .WithErrorCode("product.price_effective_to");

        RuleFor(c => c.Reason)
            .NotEmpty().WithErrorCode("catalog.reason_required")
            .MinimumLength(ProductCurationRules.ReasonMinLength).WithErrorCode("catalog.reason_required")
            .MaximumLength(ProductCurationRules.ReasonMaxLength).WithErrorCode("catalog.reason_too_long");
    }
}

/// <summary>Validates <see cref="SetProductLocationSettingCommand"/>.</summary>
public sealed class SetProductLocationSettingCommandValidator : AbstractValidator<SetProductLocationSettingCommand>
{
    /// <summary>Initializes the validator.</summary>
    public SetProductLocationSettingCommandValidator()
    {
        RuleFor(c => c.LocationId)
            .Must(id => !id.IsEmpty).WithErrorCode("product_location.location_required");

        RuleFor(c => c.MinimumStock)
            .GreaterThanOrEqualTo(0m).WithErrorCode("product_location.quantity_negative");

        RuleFor(c => c.PreferredReplenishmentQuantity)
            .GreaterThanOrEqualTo(0m).WithErrorCode("product_location.quantity_negative");
    }
}

/// <summary>Validates <see cref="AddProductUnitConversionCommand"/>.</summary>
public sealed class AddProductUnitConversionCommandValidator : AbstractValidator<AddProductUnitConversionCommand>
{
    /// <summary>Initializes the validator.</summary>
    public AddProductUnitConversionCommandValidator()
    {
        RuleFor(c => c.FromUnitId)
            .Must(id => !id.IsEmpty).WithErrorCode("conversion.units_required");

        RuleFor(c => c.ToUnitId)
            .Must(id => !id.IsEmpty).WithErrorCode("conversion.units_required");

        RuleFor(c => c.Factor)
            .GreaterThan(0m).WithErrorCode("conversion.factor_positive");
    }
}

/// <summary>Validates <see cref="LinkProductSupplierCommand"/>.</summary>
public sealed class LinkProductSupplierCommandValidator : AbstractValidator<LinkProductSupplierCommand>
{
    /// <summary>Initializes the validator.</summary>
    public LinkProductSupplierCommandValidator()
    {
        RuleFor(c => c.SupplierId)
            .Must(id => !id.IsEmpty).WithErrorCode("product_supplier.supplier_required");

        RuleFor(c => c.SupplierSku)
            .MaximumLength(ProductCurationRules.SupplierSkuMaxLength).WithErrorCode("product_supplier.sku_too_long");

        RuleFor(c => c.LeadTimeDays)
            .InclusiveBetween(0, 365).WithErrorCode("product_supplier.lead_time_invalid");

        RuleFor(c => c.MinimumOrderQuantity)
            .GreaterThanOrEqualTo(0m).When(c => c.MinimumOrderQuantity is not null)
            .WithErrorCode("product_supplier.minimum_order_negative");
    }
}
