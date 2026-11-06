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

/// <summary>A catalogue change held for the owner's approval.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="ProductId">The product being changed.</param>
/// <param name="ProductName">The product's name.</param>
/// <param name="ProductSku">The product's SKU.</param>
/// <param name="ChangeType">The kind of change held.</param>
/// <param name="Summary">A one-line description of the change.</param>
/// <param name="Reason">Why the requester says it is needed.</param>
/// <param name="Status">Where the request has got to.</param>
/// <param name="RequestedBy">Who raised it.</param>
/// <param name="RequestedByName">Their display name.</param>
/// <param name="RequestedAtUtc">When it was raised.</param>
/// <param name="DecidedByName">Who decided, when decided.</param>
/// <param name="DecidedAtUtc">When it was decided.</param>
/// <param name="DecisionNote">The approver's note.</param>
/// <param name="FailureReason">Why applying it failed, when it did.</param>
public sealed record PosProductChangeRequest(
    Guid RequestId,
    Guid ProductId,
    string ProductName,
    string ProductSku,
    string ChangeType,
    string Summary,
    string Reason,
    string Status,
    Guid RequestedBy,
    string? RequestedByName,
    DateTimeOffset RequestedAtUtc,
    string? DecidedByName,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    string? FailureReason);

/// <summary>The note recorded with an approval decision.</summary>
/// <param name="Note">Why; required when rejecting.</param>
public sealed record PosProductChangeDecision(string? Note);
