using Pos.Domain.Catalog;

namespace Pos.Api.Endpoints;

/// <summary>The body of a product creation.</summary>
/// <param name="Sku">The stock-keeping unit code.</param>
/// <param name="Name">The display name.</param>
/// <param name="CategoryId">The category.</param>
/// <param name="BaseUnitOfMeasureId">The unit the ledger counts in.</param>
/// <param name="Description">Free-text description, or null.</param>
/// <param name="BrandId">The brand, or null.</param>
/// <param name="PrimarySupplierId">The primary supplier, or null.</param>
/// <param name="TaxCode">The VAT/tax code, or null.</param>
/// <param name="IsVatExempt">Whether the product is VAT-exempt.</param>
/// <param name="DefaultPurchaseCost">The default purchase cost per base unit.</param>
/// <param name="TracksBatches">Whether the product is batch-tracked.</param>
/// <param name="TracksExpiry">Whether the product carries an expiry date.</param>
/// <param name="ShelfLifeDays">Shelf life in days, where expiry is tracked.</param>
/// <param name="InitialBarcode">A barcode to attach at creation, or null.</param>
public sealed record CreateProductBody(
    string Sku,
    string Name,
    Guid CategoryId,
    Guid BaseUnitOfMeasureId,
    string? Description = null,
    Guid? BrandId = null,
    Guid? PrimarySupplierId = null,
    string? TaxCode = null,
    bool IsVatExempt = false,
    decimal DefaultPurchaseCost = 0m,
    bool TracksBatches = false,
    bool TracksExpiry = false,
    int? ShelfLifeDays = null,
    string? InitialBarcode = null);

/// <summary>The body of a category creation.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The display name.</param>
/// <param name="ParentId">The parent category, or null for a top-level category.</param>
/// <param name="SortOrder">Ordering within the parent.</param>
public sealed record CreateCategoryBody(string Code, string Name, Guid? ParentId = null, int SortOrder = 0);

/// <summary>The body of a brand creation.</summary>
/// <param name="Name">The display name.</param>
public sealed record CreateBrandBody(string Name);

/// <summary>The body of a unit-of-measure creation.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The display name.</param>
/// <param name="Kind">The measurement kind.</param>
/// <param name="DecimalPlaces">How many decimal places a quantity in this unit may carry.</param>
public sealed record CreateUnitOfMeasureBody(string Code, string Name, UnitKind Kind, int DecimalPlaces = 0);

/// <summary>The body of a supplier creation.</summary>
/// <param name="Code">The short unique code.</param>
/// <param name="Name">The display name.</param>
/// <param name="TaxId">Tax registration number, or null.</param>
/// <param name="PaymentTermsDays">Payment terms in days.</param>
/// <param name="LeadTimeDays">Typical lead time in days.</param>
public sealed record CreateSupplierBody(
    string Code,
    string Name,
    string? TaxId = null,
    int PaymentTermsDays = 30,
    int LeadTimeDays = 7);

/// <summary>
/// A product as listed for the catalogue. <see cref="DefaultPurchaseCost"/> is
/// null for callers who do not hold <c>product.cost.view</c>; <see cref="Barcodes"/>
/// lists the active codes, primary first.
/// </summary>
public sealed record ProductSummary(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    Guid CategoryId,
    Guid? BrandId,
    Guid? PrimarySupplierId,
    Guid BaseUnitOfMeasureId,
    string? TaxCode,
    bool IsVatExempt,
    decimal? DefaultPurchaseCost,
    bool TracksBatches,
    bool TracksExpiry,
    int? ShelfLifeDays,
    string? ImageRef,
    bool IsActive,
    DateOnly? DiscontinuedOn,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyCollection<string> Barcodes);

/// <summary>A category as listed for the catalogue.</summary>
public sealed record CategorySummary(
    Guid Id,
    Guid? ParentId,
    string Code,
    string Name,
    int SortOrder,
    bool IsActive);

/// <summary>A brand as listed for the catalogue.</summary>
public sealed record BrandSummary(Guid Id, string Name, bool IsActive);

/// <summary>A unit of measure as listed for the catalogue.</summary>
public sealed record UnitOfMeasureSummary(Guid Id, string Code, string Name, UnitKind Kind, int DecimalPlaces);

/// <summary>A supplier as listed for purchasing.</summary>
public sealed record SupplierSummary(
    Guid Id,
    string Code,
    string Name,
    string? TaxId,
    int PaymentTermsDays,
    int LeadTimeDays,
    bool IsActive);

/// <summary>The body of a product edit. Tracking flags, shelf life and the base unit are not editable.</summary>
/// <param name="Name">The display name.</param>
/// <param name="CategoryId">The category.</param>
/// <param name="Description">Free-text description, or null.</param>
/// <param name="BrandId">The brand, or null.</param>
/// <param name="PrimarySupplierId">The primary supplier, or null.</param>
/// <param name="TaxCode">The VAT/tax code, or null.</param>
/// <param name="IsVatExempt">Whether the product is VAT-exempt.</param>
/// <param name="DefaultPurchaseCost">The default purchase cost per base unit.</param>
/// <param name="ImageRef">An image reference, or null.</param>
public sealed record UpdateProductBody(
    string Name,
    Guid CategoryId,
    string? Description = null,
    Guid? BrandId = null,
    Guid? PrimarySupplierId = null,
    string? TaxCode = null,
    bool IsVatExempt = false,
    decimal DefaultPurchaseCost = 0m,
    string? ImageRef = null);

/// <summary>The body of a deactivation or reactivation.</summary>
/// <param name="Reason">Why; required to deactivate.</param>
public sealed record ProductActivationBody(string? Reason = null);

/// <summary>The body of a barcode attachment.</summary>
/// <param name="Barcode">The barcode value.</param>
/// <param name="UnitOfMeasureId">The unit the code scans as, or null for the base unit.</param>
/// <param name="PackQuantity">How many base units the code represents.</param>
/// <param name="IsPrimary">Whether this becomes the primary code.</param>
public sealed record AddProductBarcodeBody(
    string Barcode,
    Guid? UnitOfMeasureId = null,
    decimal PackQuantity = 1m,
    bool IsPrimary = false);

/// <summary>The body of a barcode retirement.</summary>
/// <param name="Reason">Why the code is retired.</param>
public sealed record RetireProductBarcodeBody(string? Reason);

/// <summary>The body of a price change.</summary>
/// <param name="Amount">The selling price.</param>
/// <param name="Reason">Why the price changes.</param>
/// <param name="LocationId">The location, or null for every location.</param>
/// <param name="EffectiveFromUtc">When the price starts, or null for now.</param>
/// <param name="EffectiveToUtc">When a temporary price ends, or null for no end.</param>
public sealed record ScheduleProductPriceBody(
    decimal Amount,
    string? Reason,
    Guid? LocationId = null,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveToUtc = null);

/// <summary>The body of a per-location stocking setting.</summary>
/// <param name="IsStocked">Whether the product is sold at the location.</param>
/// <param name="MinimumStock">The minimum stock threshold.</param>
/// <param name="ReorderPoint">The reorder point.</param>
/// <param name="TargetStock">The target stock level.</param>
/// <param name="MaximumStock">The maximum stock level.</param>
/// <param name="PreferredReplenishmentQuantity">The preferred replenishment quantity.</param>
public sealed record ProductLocationSettingBody(
    bool IsStocked,
    decimal MinimumStock,
    decimal ReorderPoint,
    decimal TargetStock,
    decimal MaximumStock,
    decimal PreferredReplenishmentQuantity);

/// <summary>The body of a unit conversion.</summary>
/// <param name="FromUnitId">The source unit.</param>
/// <param name="ToUnitId">The target unit.</param>
/// <param name="Factor">How many source units make one target unit.</param>
public sealed record AddProductUnitConversionBody(Guid FromUnitId, Guid ToUnitId, decimal Factor);

/// <summary>The body of a product-supplier link.</summary>
/// <param name="SupplierSku">The supplier's own code, or null.</param>
/// <param name="LeadTimeDays">The supplier's lead time for the product.</param>
/// <param name="MinimumOrderQuantity">The supplier's minimum order quantity, or null.</param>
/// <param name="IsPreferred">Whether this becomes the preferred supplier.</param>
public sealed record ProductSupplierBody(
    string? SupplierSku = null,
    int LeadTimeDays = 0,
    decimal? MinimumOrderQuantity = null,
    bool IsPreferred = false);

/// <summary>A barcode as listed on a product, retired codes included.</summary>
public sealed record ProductBarcodeView(
    string Barcode,
    string Symbology,
    Guid UnitOfMeasureId,
    decimal PackQuantity,
    bool IsPrimary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RetiredAtUtc);

/// <summary>A price row as listed on a product; <see cref="IsCurrent"/> marks the row in effect now.</summary>
public sealed record ProductPriceView(
    Guid Id,
    Guid? LocationId,
    decimal Amount,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    string? Reason,
    DateTimeOffset CreatedAtUtc,
    bool IsCurrent);

/// <summary>A per-location stocking setting as listed on a product.</summary>
public sealed record ProductLocationSettingView(
    Guid LocationId,
    bool IsStocked,
    decimal MinimumStock,
    decimal ReorderPoint,
    decimal TargetStock,
    decimal MaximumStock,
    decimal PreferredReplenishmentQuantity);

/// <summary>A unit conversion as listed on a product.</summary>
public sealed record ProductUnitConversionView(Guid Id, Guid FromUnitId, Guid ToUnitId, decimal Factor);

/// <summary>A supplier link as listed on a product.</summary>
public sealed record ProductSupplierView(
    Guid SupplierId,
    string? SupplierSku,
    decimal? LastCost,
    int LeadTimeDays,
    decimal? MinimumOrderQuantity,
    bool IsPreferred);
