using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// Curation of an existing product: master fields, activation, barcode
/// retirement, effective-dated pricing, per-location stocking, unit conversions
/// and supplier links.
/// </summary>
/// <remarks>
/// Batch tracking, expiry tracking, shelf life and the base unit are not editable
/// here. Each one changes how existing stock and ledger history are read, so
/// they are fixed when the product is created.
/// </remarks>
public sealed partial class Product
{
    /// <summary>The longest product name the catalogue stores.</summary>
    public const int NameMaxLength = 128;

    /// <summary>The longest description the catalogue stores.</summary>
    public const int DescriptionMaxLength = 1024;

    /// <summary>The longest tax code the catalogue stores.</summary>
    public const int TaxCodeMaxLength = 16;

    /// <summary>The longest image reference the catalogue stores.</summary>
    public const int ImageRefMaxLength = 256;

    /// <summary>
    /// How far before the server's clock a price may start and still count as
    /// "now". It absorbs the delay between a client choosing the instant and the
    /// server applying it; anything earlier is refused as backdating.
    /// </summary>
    public static readonly TimeSpan PriceBackdatingTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Replaces the editable master fields.</summary>
    /// <param name="name">The display name.</param>
    /// <param name="description">Free-text description, or null.</param>
    /// <param name="categoryId">The category.</param>
    /// <param name="brandId">The brand, or null.</param>
    /// <param name="primarySupplierId">The primary supplier, or null.</param>
    /// <param name="taxCode">The VAT/tax code, or null.</param>
    /// <param name="isVatExempt">Whether the product is VAT-exempt.</param>
    /// <param name="defaultPurchaseCost">The default purchase cost per base unit.</param>
    /// <param name="imageRef">An image reference, or null.</param>
    /// <returns>A success result, or a validation failure.</returns>
    public Result UpdateDetails(
        string? name,
        string? description,
        CategoryId categoryId,
        BrandId? brandId,
        SupplierId? primarySupplierId,
        string? taxCode,
        bool isVatExempt,
        decimal defaultPurchaseCost,
        string? imageRef)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure(Error.Validation("product.name_required", "A product name is required."));
        }

        if (categoryId.IsEmpty)
        {
            return Result.Failure(Error.Validation("product.category_required", "A category is required."));
        }

        if (defaultPurchaseCost < 0m)
        {
            return Result.Failure(Error.Validation(
                "product.cost_negative", "Default purchase cost cannot be negative."));
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        CategoryId = categoryId;
        BrandId = brandId;
        PrimarySupplierId = primarySupplierId;
        TaxCode = string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim();
        IsVatExempt = isVatExempt;
        DefaultPurchaseCost = defaultPurchaseCost;
        ImageRef = string.IsNullOrWhiteSpace(imageRef) ? null : imageRef.Trim();

        Touch();
        return Result.Success();
    }

    /// <summary>Stops the product being sold or received, recording when it was discontinued.</summary>
    /// <param name="discontinuedOn">The discontinuation date.</param>
    /// <returns>A success result, or a conflict when it is already inactive.</returns>
    public Result Deactivate(DateOnly discontinuedOn)
    {
        if (!IsActive)
        {
            return Result.Failure(CatalogErrors.ActivationUnchanged(active: false));
        }

        IsActive = false;
        DiscontinuedOn = discontinuedOn;
        Touch();
        return Result.Success();
    }

    /// <summary>Returns a deactivated product to sale and receiving.</summary>
    /// <returns>A success result, or a conflict when it is already active.</returns>
    public Result Activate()
    {
        if (IsActive)
        {
            return Result.Failure(CatalogErrors.ActivationUnchanged(active: true));
        }

        IsActive = true;
        DiscontinuedOn = null;
        Touch();
        return Result.Success();
    }

    /// <summary>
    /// Retires a barcode so it no longer scans. The row is kept and the value
    /// stays reserved. Retiring the primary code promotes the longest-attached
    /// remaining code, so a product with any active code always has a primary.
    /// </summary>
    /// <param name="value">The barcode value.</param>
    /// <param name="retiredByUserId">Who is retiring the code.</param>
    /// <param name="retiredAtUtc">When the code is retired.</param>
    /// <returns>A success result, or a not-found/conflict failure.</returns>
    public Result RetireBarcode(string? value, UserId retiredByUserId, DateTimeOffset retiredAtUtc)
    {
        Result<ProductBarcode> found = FindActiveBarcode(value);

        if (found.IsFailure)
        {
            return Result.Failure(found.Errors);
        }

        ProductBarcode barcode = found.Value;
        bool wasPrimary = barcode.IsPrimary;
        barcode.Retire(retiredByUserId, retiredAtUtc.ToUniversalTime());

        if (wasPrimary)
        {
            _barcodes
                .Where(b => !b.IsRetired)
                .OrderBy(b => b.CreatedAtUtc)
                .FirstOrDefault()?
                .PromotePrimary();
        }

        Touch();
        return Result.Success();
    }

    /// <summary>Makes an active barcode the product's primary code.</summary>
    /// <param name="value">The barcode value.</param>
    /// <returns>A success result, or a not-found/conflict failure.</returns>
    public Result SetPrimaryBarcode(string? value)
    {
        Result<ProductBarcode> found = FindActiveBarcode(value);

        if (found.IsFailure)
        {
            return Result.Failure(found.Errors);
        }

        if (!found.Value.IsPrimary)
        {
            _barcodes.ForEach(b => b.DemotePrimary());
            found.Value.PromotePrimary();
            Touch();
        }

        return Result.Success();
    }

    /// <summary>
    /// Schedules a selling price for a product and scope (ADR-0029).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A price never starts in the past. When the new period overlaps nothing it
    /// is simply added. When it overlaps exactly one earlier-starting price, that
    /// price hands over: its end is closed at the new start, and if the new price
    /// is temporary and ends before the old one would have, the old amount
    /// resumes in a continuation row from the new end. Anything else - a period
    /// that would replace a later-starting price, or that spans several - is
    /// refused, because it would silently cancel a price someone scheduled.
    /// </para>
    /// <para>The database exclusion constraint is the backstop against concurrent writers.</para>
    /// </remarks>
    /// <param name="locationId">The location scope, or null for every location.</param>
    /// <param name="amount">The amount, in the organization's currency.</param>
    /// <param name="effectiveFromUtc">When the price starts, inclusive.</param>
    /// <param name="effectiveToUtc">When the price ends, exclusive, or null for no end.</param>
    /// <param name="createdByUserId">Who is setting the price.</param>
    /// <param name="reason">The recorded reason.</param>
    /// <param name="nowUtc">The authoritative current time.</param>
    /// <returns>The new price row's identifier, or a validation/conflict failure.</returns>
    public Result<ProductPriceId> SchedulePrice(
        LocationId? locationId,
        decimal amount,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc,
        UserId createdByUserId,
        string? reason,
        DateTimeOffset nowUtc)
    {
        DateTimeOffset from = effectiveFromUtc.ToUniversalTime();
        DateTimeOffset? to = effectiveToUtc?.ToUniversalTime();

        if (amount < 0m)
        {
            return Result<ProductPriceId>.Failure(Error.Validation(
                "product.price_negative", "A selling price cannot be negative."));
        }

        if (from < nowUtc - PriceBackdatingTolerance)
        {
            return Result<ProductPriceId>.Failure(CatalogErrors.PriceBackdated);
        }

        if (to is { } end && end <= from)
        {
            return Result<ProductPriceId>.Failure(Error.Validation(
                "product.price_effective_to", "A price must end after it starts."));
        }

        string? trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        List<ProductPrice> overlapping = [.. _prices.Where(p => p.LocationId == locationId && p.Overlaps(from, to))];

        if (overlapping.Count > 1 || (overlapping.Count == 1 && overlapping[0].EffectiveFromUtc >= from))
        {
            return Result<ProductPriceId>.Failure(CatalogErrors.OverlappingPrice(Id));
        }

        if (overlapping.Count == 1)
        {
            ProductPrice predecessor = overlapping[0];
            DateTimeOffset? predecessorEnd = predecessor.EffectiveToUtc;
            predecessor.Close(from);

            if (to is { } temporaryEnd && (predecessorEnd is null || temporaryEnd < predecessorEnd))
            {
                AddPriceRow(
                    locationId,
                    predecessor.Price.Amount,
                    temporaryEnd,
                    predecessorEnd,
                    createdByUserId,
                    "Resumes the price in effect before a temporary price.");
            }
        }

        ProductPriceId id = AddPriceRow(locationId, amount, from, to, createdByUserId, trimmedReason);

        Touch();
        return Result<ProductPriceId>.Success(id);
    }

    /// <summary>
    /// Finds the price in effect at an instant: the location's own price when it
    /// has one, otherwise the price for every location.
    /// </summary>
    /// <param name="locationId">The location, or null for the organization-wide price.</param>
    /// <param name="atUtc">The instant.</param>
    /// <returns>The price row, or null when no price applies.</returns>
    public ProductPrice? PriceAt(LocationId? locationId, DateTimeOffset atUtc)
    {
        ProductPrice? InScope(LocationId? scope) => _prices.FirstOrDefault(p =>
            p.LocationId == scope
            && p.EffectiveFromUtc <= atUtc
            && (p.EffectiveToUtc is null || atUtc < p.EffectiveToUtc));

        return (locationId is null ? null : InScope(locationId)) ?? InScope(null);
    }

    /// <summary>Sets whether and how the product is stocked at a location, replacing any earlier setting.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="isStocked">Whether the product is sold at the location.</param>
    /// <param name="minimumStock">The minimum stock threshold.</param>
    /// <param name="reorderPoint">The reorder point.</param>
    /// <param name="targetStock">The target stock level.</param>
    /// <param name="maximumStock">The maximum stock level.</param>
    /// <param name="preferredReplenishmentQuantity">The preferred replenishment quantity.</param>
    /// <returns>A success result, or a validation failure.</returns>
    public Result SetLocationSetting(
        LocationId locationId,
        bool isStocked,
        decimal minimumStock,
        decimal reorderPoint,
        decimal targetStock,
        decimal maximumStock,
        decimal preferredReplenishmentQuantity)
    {
        ProductLocationSetting? existing = _locationSettings.FirstOrDefault(s => s.LocationId == locationId);

        if (existing is not null)
        {
            Result updated = existing.Update(
                isStocked, minimumStock, reorderPoint, targetStock, maximumStock, preferredReplenishmentQuantity);

            if (updated.IsFailure)
            {
                return updated;
            }
        }
        else
        {
            Result<ProductLocationSetting> created = ProductLocationSetting.Create(
                Id, locationId, isStocked, minimumStock, reorderPoint, targetStock, maximumStock,
                preferredReplenishmentQuantity);

            if (created.IsFailure)
            {
                return Result.Failure(created.Errors);
            }

            _locationSettings.Add(created.Value);
        }

        Touch();
        return Result.Success();
    }

    /// <summary>Adds a conversion between two units for this product.</summary>
    /// <param name="fromUnitId">The source unit.</param>
    /// <param name="toUnitId">The target unit.</param>
    /// <param name="factor">How many source units make one target unit.</param>
    /// <returns>The conversion's identifier, or a validation/conflict failure.</returns>
    public Result<ProductUnitConversionId> AddUnitConversion(
        UnitOfMeasureId fromUnitId,
        UnitOfMeasureId toUnitId,
        decimal factor)
    {
        if (_unitConversions.Any(c => c.FromUnitId == fromUnitId && c.ToUnitId == toUnitId))
        {
            return Result<ProductUnitConversionId>.Failure(CatalogErrors.ConversionExists);
        }

        Result<ProductUnitConversion> created = ProductUnitConversion.Create(Id, fromUnitId, toUnitId, factor);

        if (created.IsFailure)
        {
            return Result<ProductUnitConversionId>.Failure(created.Errors);
        }

        _unitConversions.Add(created.Value);
        Touch();
        return Result<ProductUnitConversionId>.Success(created.Value.Id);
    }

    /// <summary>
    /// Removes a unit conversion. Documents already entered keep the quantities
    /// they were converted with; only future entry is affected.
    /// </summary>
    /// <param name="conversionId">The conversion.</param>
    /// <returns>A success result, or a not-found failure.</returns>
    public Result RemoveUnitConversion(ProductUnitConversionId conversionId)
    {
        ProductUnitConversion? conversion = _unitConversions.FirstOrDefault(c => c.Id == conversionId);

        if (conversion is null)
        {
            return Result.Failure(CatalogErrors.ConversionUnknown);
        }

        _unitConversions.Remove(conversion);
        Touch();
        return Result.Success();
    }

    /// <summary>
    /// Links a supplier to the product, or updates the existing link's terms.
    /// Marking a link preferred makes it the only preferred supplier.
    /// </summary>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="supplierSku">The supplier's own code, or null.</param>
    /// <param name="leadTimeDays">The supplier's lead time for the product.</param>
    /// <param name="minimumOrderQuantity">The supplier's minimum order quantity, or null.</param>
    /// <param name="isPreferred">Whether this becomes the preferred supplier.</param>
    /// <returns>A success result, or a validation failure.</returns>
    public Result LinkSupplier(
        SupplierId supplierId,
        string? supplierSku,
        int leadTimeDays,
        decimal? minimumOrderQuantity,
        bool isPreferred)
    {
        ProductSupplier? link = _suppliers.FirstOrDefault(s => s.SupplierId == supplierId);

        if (link is not null)
        {
            Result updated = link.Update(supplierSku, leadTimeDays, minimumOrderQuantity, isPreferred);

            if (updated.IsFailure)
            {
                return updated;
            }
        }
        else
        {
            Result<ProductSupplier> created = ProductSupplier.Create(
                Id, supplierId, supplierSku, lastCost: null, leadTimeDays, minimumOrderQuantity, isPreferred);

            if (created.IsFailure)
            {
                return Result.Failure(created.Errors);
            }

            link = created.Value;
            _suppliers.Add(link);
        }

        if (isPreferred)
        {
            foreach (ProductSupplier other in _suppliers.Where(s => s.SupplierId != supplierId))
            {
                other.DemotePreferred();
            }
        }

        Touch();
        return Result.Success();
    }

    /// <summary>Removes a supplier link.</summary>
    /// <param name="supplierId">The supplier.</param>
    /// <returns>A success result, or a not-found failure.</returns>
    public Result UnlinkSupplier(SupplierId supplierId)
    {
        ProductSupplier? link = _suppliers.FirstOrDefault(s => s.SupplierId == supplierId);

        if (link is null)
        {
            return Result.Failure(CatalogErrors.SupplierNotLinked);
        }

        _suppliers.Remove(link);
        Touch();
        return Result.Success();
    }

    private Result<ProductBarcode> FindActiveBarcode(string? value)
    {
        Result<Barcode> parsed = Barcode.Create(value);

        if (parsed.IsFailure)
        {
            return Result<ProductBarcode>.Failure(parsed.Errors);
        }

        ProductBarcode? barcode = _barcodes.FirstOrDefault(b => b.Barcode == parsed.Value);

        if (barcode is null)
        {
            return Result<ProductBarcode>.Failure(CatalogErrors.BarcodeNotAttached(parsed.Value.Value));
        }

        return barcode.IsRetired
            ? Result<ProductBarcode>.Failure(CatalogErrors.BarcodeRetired(barcode.Value))
            : Result<ProductBarcode>.Success(barcode);
    }

    private ProductPriceId AddPriceRow(
        LocationId? locationId,
        decimal amount,
        DateTimeOffset from,
        DateTimeOffset? to,
        UserId createdByUserId,
        string? reason)
    {
        ProductPrice price = new(
            ProductPriceId.New(),
            Id,
            locationId,
            new Money(amount, "PHP"),
            from,
            to,
            createdByUserId,
            reason);

        _prices.Add(price);
        return price.Id;
    }
}
