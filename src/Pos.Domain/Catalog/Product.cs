using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// The product master. Defined and curated at the Main Warehouse; the ledger
/// counts in <see cref="BaseUnitOfMeasureId"/> and every other unit is expressed
/// through <see cref="ProductUnitConversion"/>.
/// </summary>
/// <remarks>
/// <para>
/// The barcode collection is the only way a scannable code is attached, and the
/// price collection is the only way a price changes. Barcode uniqueness is
/// global and deliberate: a code already in use is rejected rather than
/// re-pointed, because a mis-pointed barcode silently corrupts both stock and
/// revenue for two products at once.
/// </para>
/// <para>
/// <see cref="BaseUnitOfMeasureId"/> is immutable by design once any movement
/// exists for a product; nothing in this aggregate changes it.
/// </para>
/// </remarks>
public sealed partial class Product : AggregateRoot<ProductId>
{
    private readonly List<ProductBarcode> _barcodes = [];
    private readonly List<ProductPrice> _prices = [];
    private readonly List<ProductUnitConversion> _unitConversions = [];
    private readonly List<ProductLocationSetting> _locationSettings = [];
    private readonly List<ProductSupplier> _suppliers = [];

    private Product(
        ProductId id,
        Sku sku,
        string name,
        string? description,
        CategoryId categoryId,
        BrandId? brandId,
        SupplierId? primarySupplierId,
        UnitOfMeasureId baseUnitOfMeasureId,
        string? taxCode,
        bool isVatExempt,
        decimal defaultPurchaseCost,
        bool tracksBatches,
        bool tracksExpiry,
        int? shelfLifeDays,
        string? imageRef,
        UserId createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Sku = sku;
        Name = name;
        Description = description;
        CategoryId = categoryId;
        BrandId = brandId;
        PrimarySupplierId = primarySupplierId;
        BaseUnitOfMeasureId = baseUnitOfMeasureId;
        TaxCode = string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim();
        IsVatExempt = isVatExempt;
        DefaultPurchaseCost = defaultPurchaseCost;
        TracksBatches = tracksBatches;
        TracksExpiry = tracksExpiry;
        ShelfLifeDays = shelfLifeDays;
        ImageRef = imageRef;
        IsActive = true;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Product()
    {
        Name = string.Empty;
        Sku = Sku.FromTrustedSource(string.Empty);
        CreatedByUserId = UserId.Empty;
    }

    /// <summary>Gets the stock-keeping unit code.</summary>
    public Sku Sku { get; private set; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets the free-text description.</summary>
    public string? Description { get; private set; }

    /// <summary>Gets the category.</summary>
    public CategoryId CategoryId { get; private set; }

    /// <summary>Gets the brand, if any.</summary>
    public BrandId? BrandId { get; private set; }

    /// <summary>Gets the primary supplier, if any.</summary>
    public SupplierId? PrimarySupplierId { get; private set; }

    /// <summary>Gets the base unit the ledger counts in.</summary>
    public UnitOfMeasureId BaseUnitOfMeasureId { get; }

    /// <summary>Gets the VAT / tax code applied to sales of this product.</summary>
    public string? TaxCode { get; private set; }

    /// <summary>Gets whether the product is exempt from VAT.</summary>
    public bool IsVatExempt { get; private set; }

    /// <summary>Gets the default purchase cost used before any supplier or batch cost is known.</summary>
    public decimal DefaultPurchaseCost { get; private set; }

    /// <summary>Gets whether the product is tracked by batch or lot.</summary>
    public bool TracksBatches { get; }

    /// <summary>Gets whether the product carries an expiry date.</summary>
    public bool TracksExpiry { get; }

    /// <summary>Gets the typical shelf life in days, where expiry is tracked.</summary>
    public int? ShelfLifeDays { get; }

    /// <summary>Gets a reference to an image, if any.</summary>
    public string? ImageRef { get; private set; }

    /// <summary>Gets whether the product may still be sold and received.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets when the product was discontinued, if ever.</summary>
    public DateOnly? DiscontinuedOn { get; private set; }

    /// <summary>Gets who created the product.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Gets when the product was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets when the product was last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Gets the attached barcodes.</summary>
    public IReadOnlyList<ProductBarcode> Barcodes => _barcodes.ToArray();

    /// <summary>Gets the effective-dated price rows.</summary>
    public IReadOnlyList<ProductPrice> Prices => _prices.ToArray();

    /// <summary>Gets the unit conversions.</summary>
    public IReadOnlyList<ProductUnitConversion> UnitConversions => _unitConversions.ToArray();

    /// <summary>Gets the per-location settings.</summary>
    public IReadOnlyList<ProductLocationSetting> LocationSettings => _locationSettings.ToArray();

    /// <summary>Gets the product-supplier links.</summary>
    public IReadOnlyList<ProductSupplier> Suppliers => _suppliers.ToArray();

    /// <summary>
    /// Records an actual purchase cost from a supplier on the product-supplier
    /// link, so replenishment planning sees the latest cost. Products that have
    /// no link for the supplier are untouched, as are receipt lines that deliver
    /// no quantity.
    /// </summary>
    /// <param name="supplierId">The supplier the goods were received from.</param>
    /// <param name="unitCost">The actual unit cost of the receipt.</param>
    /// <param name="receivedQuantity">The quantity received; zero skips the update.</param>
    internal void RecordSupplierReceipt(SupplierId supplierId, decimal unitCost, decimal receivedQuantity)
    {
        if (receivedQuantity <= 0m)
        {
            return;
        }

        ProductSupplier? link = _suppliers.FirstOrDefault(s => s.SupplierId == supplierId);
        link?.RecordPurchaseCost(unitCost);
    }

    /// <summary>
    /// Creates a product. The SKU is validated and normalised by
    /// <see cref="Sku"/>; the base unit of measure is immutable for the life of
    /// the product.
    /// </summary>
    /// <param name="sku">The stock-keeping unit code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="categoryId">The category.</param>
    /// <param name="baseUnitOfMeasureId">The unit the ledger counts in.</param>
    /// <param name="createdByUserId">Who is creating the product.</param>
    /// <param name="description">Free-text description, or <see langword="null"/>.</param>
    /// <param name="brandId">The brand, or <see langword="null"/>.</param>
    /// <param name="primarySupplierId">The primary supplier, or <see langword="null"/>.</param>
    /// <param name="taxCode">The VAT/tax code, or <see langword="null"/>.</param>
    /// <param name="isVatExempt">Whether the product is VAT-exempt.</param>
    /// <param name="defaultPurchaseCost">The default purchase cost per base unit.</param>
    /// <param name="tracksBatches">Whether the product is batch-tracked.</param>
    /// <param name="tracksExpiry">Whether the product carries an expiry date.</param>
    /// <param name="shelfLifeDays">Shelf life in days, where expiry is tracked.</param>
    /// <param name="imageRef">An image reference, or <see langword="null"/>.</param>
    /// <returns>The new product, or a validation failure.</returns>
    public static Result<Product> Create(
        string? sku,
        string? name,
        CategoryId categoryId,
        UnitOfMeasureId baseUnitOfMeasureId,
        UserId createdByUserId,
        string? description = null,
        BrandId? brandId = null,
        SupplierId? primarySupplierId = null,
        string? taxCode = null,
        bool isVatExempt = false,
        decimal defaultPurchaseCost = 0m,
        bool tracksBatches = false,
        bool tracksExpiry = false,
        int? shelfLifeDays = null,
        string? imageRef = null)
    {
        Result<Sku> skuResult = Sku.Create(sku);
        if (skuResult.IsFailure)
        {
            return Result<Product>.Failure(skuResult.Errors);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<Product>.Failure(Error.Validation(
                "product.name_required", "A product name is required."));
        }

        if (categoryId.IsEmpty)
        {
            return Result<Product>.Failure(Error.Validation(
                "product.category_required", "A category is required."));
        }

        if (baseUnitOfMeasureId.IsEmpty)
        {
            return Result<Product>.Failure(Error.Validation(
                "product.base_uom_required", "A base unit of measure is required."));
        }

        if (tracksExpiry && shelfLifeDays is null or <= 0)
        {
            return Result<Product>.Failure(Error.Validation(
                "product.shelf_life_required",
                "A shelf life in days is required when expiry is tracked."));
        }

        if (defaultPurchaseCost < 0m)
        {
            return Result<Product>.Failure(Error.Validation(
                "product.cost_negative", "Default purchase cost cannot be negative."));
        }

        Product product = new(
            ProductId.New(),
            skuResult.Value,
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            categoryId,
            brandId,
            primarySupplierId,
            baseUnitOfMeasureId,
            string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim(),
            isVatExempt,
            defaultPurchaseCost,
            tracksBatches,
            tracksExpiry,
            tracksExpiry ? shelfLifeDays : null,
            string.IsNullOrWhiteSpace(imageRef) ? null : imageRef.Trim(),
            createdByUserId,
            DateTimeOffset.UtcNow);

        return Result<Product>.Success(product);
    }

    /// <summary>
    /// Attaches a barcode. The code is validated and normalised by
    /// <see cref="Barcode.Create"/>; a code already attached to the product,
    /// retired or not, is rejected. Exactly one barcode per product stays
    /// primary: the first code attached becomes primary even when not asked.
    /// </summary>
    /// <param name="value">The raw barcode value.</param>
    /// <param name="unitOfMeasureId">The unit this code scans as.</param>
    /// <param name="packQuantity">How many base units the code represents.</param>
    /// <param name="isPrimary">Whether this becomes the primary code.</param>
    /// <param name="createdByUserId">Who is attaching the code.</param>
    /// <param name="requireChecksum">Whether to enforce a GTIN check digit.</param>
    /// <returns>A success result, or a validation/conflict failure.</returns>
    public Result AddBarcode(
        string? value,
        UnitOfMeasureId unitOfMeasureId,
        decimal packQuantity,
        bool isPrimary,
        UserId createdByUserId,
        bool requireChecksum = true)
    {
        Result<Barcode> barcodeResult = Barcode.Create(value, requireChecksum);
        if (barcodeResult.IsFailure)
        {
            return Result.Failure(barcodeResult.Errors);
        }

        Barcode barcode = barcodeResult.Value;

        ProductBarcode? existing = _barcodes.FirstOrDefault(b => b.Barcode == barcode);

        if (existing is not null)
        {
            return Result.Failure(existing.IsRetired
                ? CatalogErrors.BarcodeRetired(barcode.Value)
                : CatalogErrors.DuplicateBarcode(barcode.Value));
        }

        if (unitOfMeasureId.IsEmpty)
        {
            return Result.Failure(Error.Validation(
                "product.barcode_uom_required", "A barcode must reference a unit of measure."));
        }

        if (packQuantity <= 0m)
        {
            return Result.Failure(Error.Validation(
                "product.barcode_pack_quantity", "A barcode's pack quantity must be greater than zero."));
        }

        bool becomesPrimary = isPrimary || !_barcodes.Any(b => b.IsPrimary);

        if (becomesPrimary)
        {
            _barcodes.ForEach(b => b.DemotePrimary());
        }

        _barcodes.Add(new ProductBarcode(
            ProductBarcodeId.New(),
            Id,
            barcode,
            unitOfMeasureId,
            packQuantity,
            becomesPrimary,
            createdByUserId));

        Touch();
        return Result.Success();
    }

    /// <summary>Records a change to the aggregate.</summary>
    private void Touch() => UpdatedAtUtc = DateTimeOffset.UtcNow;
}