using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Application.Catalog;

/// <summary>
/// Replaces a product's editable master fields. Batch and expiry tracking, shelf
/// life and the base unit are fixed at creation and are not part of the command.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">Free-text description, or null.</param>
/// <param name="CategoryId">The category.</param>
/// <param name="BrandId">The brand, or null.</param>
/// <param name="PrimarySupplierId">The primary supplier, or null.</param>
/// <param name="TaxCode">The VAT/tax code, or null.</param>
/// <param name="IsVatExempt">Whether the product is VAT-exempt.</param>
/// <param name="DefaultPurchaseCost">The default purchase cost per base unit.</param>
/// <param name="ImageRef">An image reference, or null.</param>
public sealed record UpdateProductCommand(
    ProductId ProductId,
    string? Name,
    string? Description,
    CategoryId CategoryId,
    BrandId? BrandId,
    SupplierId? PrimarySupplierId,
    string? TaxCode,
    bool IsVatExempt,
    decimal DefaultPurchaseCost,
    string? ImageRef)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}

/// <summary>Deactivates or reactivates a product.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Active">The requested state.</param>
/// <param name="Reason">Why; required to deactivate.</param>
public sealed record SetProductActivationCommand(ProductId ProductId, bool Active, string? Reason)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Disable;
}

/// <summary>Attaches a barcode to a product.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Barcode">The barcode value.</param>
/// <param name="UnitOfMeasureId">The unit the code scans as, or null for the base unit.</param>
/// <param name="PackQuantity">How many base units the code represents.</param>
/// <param name="IsPrimary">Whether this becomes the primary code.</param>
public sealed record AddProductBarcodeCommand(
    ProductId ProductId,
    string? Barcode,
    UnitOfMeasureId? UnitOfMeasureId,
    decimal PackQuantity,
    bool IsPrimary)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ManageBarcodes;
}

/// <summary>Retires a product's barcode so it no longer scans.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Barcode">The barcode value.</param>
/// <param name="Reason">Why the code is retired.</param>
public sealed record RetireProductBarcodeCommand(ProductId ProductId, string? Barcode, string? Reason)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ManageBarcodes;
}

/// <summary>Makes one of a product's active barcodes its primary code.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Barcode">The barcode value.</param>
public sealed record SetPrimaryProductBarcodeCommand(ProductId ProductId, string? Barcode)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ManageBarcodes;
}

/// <summary>Schedules an effective-dated selling price (ADR-0029).</summary>
/// <param name="ProductId">The product.</param>
/// <param name="LocationId">The location scope, or null for every location.</param>
/// <param name="Amount">The amount, in the organization's currency.</param>
/// <param name="EffectiveFromUtc">When the price starts, or null for now.</param>
/// <param name="EffectiveToUtc">When a temporary price ends, or null for no end.</param>
/// <param name="Reason">Why the price changes.</param>
public sealed record ScheduleProductPriceCommand(
    ProductId ProductId,
    LocationId? LocationId,
    decimal Amount,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    string? Reason)
    : ICommand<ProductPriceId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ManagePrices;
}

/// <summary>Cancels a selling price that has not yet taken effect (ADR-0029).</summary>
/// <param name="ProductId">The product.</param>
/// <param name="PriceId">The price row to cancel.</param>
/// <param name="Reason">Why the price is cancelled.</param>
public sealed record CancelScheduledProductPriceCommand(
    ProductId ProductId,
    ProductPriceId PriceId,
    string? Reason)
    : ICommand<ProductPriceId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.ManagePrices;
}

/// <summary>Sets whether and how a product is stocked at one location.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="LocationId">The location.</param>
/// <param name="IsStocked">Whether the product is sold at the location.</param>
/// <param name="MinimumStock">The minimum stock threshold.</param>
/// <param name="ReorderPoint">The reorder point.</param>
/// <param name="TargetStock">The target stock level.</param>
/// <param name="MaximumStock">The maximum stock level.</param>
/// <param name="PreferredReplenishmentQuantity">The preferred replenishment quantity.</param>
public sealed record SetProductLocationSettingCommand(
    ProductId ProductId,
    LocationId LocationId,
    bool IsStocked,
    decimal MinimumStock,
    decimal ReorderPoint,
    decimal TargetStock,
    decimal MaximumStock,
    decimal PreferredReplenishmentQuantity)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}

/// <summary>Adds a unit conversion to a product.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="FromUnitId">The source unit.</param>
/// <param name="ToUnitId">The target unit.</param>
/// <param name="Factor">How many source units make one target unit.</param>
public sealed record AddProductUnitConversionCommand(
    ProductId ProductId,
    UnitOfMeasureId FromUnitId,
    UnitOfMeasureId ToUnitId,
    decimal Factor)
    : ICommand<ProductUnitConversionId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}

/// <summary>Removes a unit conversion from a product.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="ConversionId">The conversion.</param>
public sealed record RemoveProductUnitConversionCommand(ProductId ProductId, ProductUnitConversionId ConversionId)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}

/// <summary>Links a supplier to a product, or updates the link's terms.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="SupplierId">The supplier.</param>
/// <param name="SupplierSku">The supplier's own code, or null.</param>
/// <param name="LeadTimeDays">The supplier's lead time for the product.</param>
/// <param name="MinimumOrderQuantity">The supplier's minimum order quantity, or null.</param>
/// <param name="IsPreferred">Whether this becomes the preferred supplier.</param>
public sealed record LinkProductSupplierCommand(
    ProductId ProductId,
    SupplierId SupplierId,
    string? SupplierSku,
    int LeadTimeDays,
    decimal? MinimumOrderQuantity,
    bool IsPreferred)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}

/// <summary>Removes a supplier link from a product.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="SupplierId">The supplier.</param>
public sealed record UnlinkProductSupplierCommand(ProductId ProductId, SupplierId SupplierId)
    : ICommand<ProductId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Catalog.Edit;
}
