namespace Pos.Web.Services;

/// <summary>A product master record as the catalogue screens show it.</summary>
/// <remarks>
/// <see cref="DefaultPurchaseCost"/> arrives null when the signed-in account may
/// not see costs, which is a different thing from a cost of zero.
/// </remarks>
public sealed record PosCatalogProduct(
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
    IReadOnlyList<string> Barcodes);

/// <summary>
/// One barcode attached to a product, retired codes included.
/// </summary>
/// <remarks>
/// A retired code keeps its row so history that scanned it still resolves, and
/// so the value stays reserved against being pointed at another product.
/// </remarks>
public sealed record PosProductBarcode(
    string Barcode,
    string Symbology,
    Guid UnitOfMeasureId,
    decimal PackQuantity,
    bool IsPrimary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RetiredAtUtc);

/// <summary>The body that creates a product master record.</summary>
public sealed record PosCreateProduct(
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

/// <summary>The body that attaches a barcode to a product.</summary>
/// <param name="Barcode">The scanned or typed code.</param>
/// <param name="UnitOfMeasureId">The unit this code scans as, or null for the product's base unit.</param>
/// <param name="PackQuantity">How many base units one scan of this code represents.</param>
/// <param name="IsPrimary">Whether this becomes the product's primary code.</param>
public sealed record PosAddProductBarcode(
    string Barcode,
    Guid? UnitOfMeasureId = null,
    decimal PackQuantity = 1m,
    bool IsPrimary = false);

/// <summary>The body that retires a barcode.</summary>
public sealed record PosRetireProductBarcode(string? Reason);

/// <summary>A product brand.</summary>
public sealed record PosBrand(Guid Id, string Name, bool IsActive);

/// <summary>A page of catalogue search results, with how many match in total.</summary>
public sealed record PosCatalogPage(
    IReadOnlyList<PosCatalogProduct> Items,
    int Total,
    int Offset,
    int Limit);
