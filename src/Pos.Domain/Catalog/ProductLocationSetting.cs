using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// Whether and how a product is stocked at a location, and the replenishment
/// thresholds that drive ordering.
/// </summary>
public sealed class ProductLocationSetting
{
    internal ProductLocationSetting(
        ProductLocationSettingId id,
        ProductId productId,
        LocationId locationId,
        bool isStocked,
        decimal minimumStock,
        decimal reorderPoint,
        decimal targetStock,
        decimal maximumStock,
        decimal preferredReplenishmentQuantity)
    {
        Id = id;
        ProductId = productId;
        LocationId = locationId;
        IsStocked = isStocked;
        MinimumStock = minimumStock;
        ReorderPoint = reorderPoint;
        TargetStock = targetStock;
        MaximumStock = maximumStock;
        PreferredReplenishmentQuantity = preferredReplenishmentQuantity;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ProductLocationSetting()
    {
    }

    /// <summary>Gets the setting row identifier.</summary>
    public ProductLocationSettingId Id { get; }

    /// <summary>Gets the owning product.</summary>
    public ProductId ProductId { get; }

    /// <summary>Gets the location.</summary>
    public LocationId LocationId { get; }

    /// <summary>Gets whether this product is sold at the location.</summary>
    public bool IsStocked { get; private set; }

    /// <summary>Gets the minimum stock threshold.</summary>
    public decimal MinimumStock { get; private set; }

    /// <summary>Gets the reorder point.</summary>
    public decimal ReorderPoint { get; private set; }

    /// <summary>Gets the target stock level.</summary>
    public decimal TargetStock { get; private set; }

    /// <summary>Gets the maximum stock level.</summary>
    public decimal MaximumStock { get; private set; }

    /// <summary>Gets the preferred replenishment quantity.</summary>
    public decimal PreferredReplenishmentQuantity { get; private set; }

    /// <summary>Creates a per-location setting row.</summary>
    /// <returns>The setting, or a validation failure if the thresholds are inconsistent.</returns>
    public static Result<ProductLocationSetting> Create(
        ProductId productId,
        LocationId locationId,
        bool isStocked,
        decimal minimumStock,
        decimal reorderPoint,
        decimal targetStock,
        decimal maximumStock,
        decimal preferredReplenishmentQuantity)
    {
        if (locationId.IsEmpty)
        {
            return Result<ProductLocationSetting>.Failure(
                Error.Validation("product_location.location_required", "A location is required."));
        }

        Result valid = Validate(minimumStock, reorderPoint, targetStock, maximumStock, preferredReplenishmentQuantity);

        return valid.IsFailure
            ? Result<ProductLocationSetting>.Failure(valid.Errors)
            : Result<ProductLocationSetting>.Success(new ProductLocationSetting(
                ProductLocationSettingId.New(),
                productId,
                locationId,
                isStocked,
                minimumStock,
                reorderPoint,
                targetStock,
                maximumStock,
                preferredReplenishmentQuantity));
    }

    /// <summary>
    /// Replaces the stocking flag and thresholds in place, so the row keeps its
    /// identity and the one-row-per-product-and-location rule is never at risk.
    /// </summary>
    /// <returns>A success result, or a validation failure.</returns>
    internal Result Update(
        bool isStocked,
        decimal minimumStock,
        decimal reorderPoint,
        decimal targetStock,
        decimal maximumStock,
        decimal preferredReplenishmentQuantity)
    {
        Result valid = Validate(minimumStock, reorderPoint, targetStock, maximumStock, preferredReplenishmentQuantity);

        if (valid.IsFailure)
        {
            return valid;
        }

        IsStocked = isStocked;
        MinimumStock = minimumStock;
        ReorderPoint = reorderPoint;
        TargetStock = targetStock;
        MaximumStock = maximumStock;
        PreferredReplenishmentQuantity = preferredReplenishmentQuantity;
        return Result.Success();
    }

    private static Result Validate(
        decimal minimumStock,
        decimal reorderPoint,
        decimal targetStock,
        decimal maximumStock,
        decimal preferredReplenishmentQuantity)
    {
        if (minimumStock < 0m || preferredReplenishmentQuantity < 0m)
        {
            return Result.Failure(Error.Validation(
                "product_location.quantity_negative",
                "Stock thresholds and the replenishment quantity cannot be negative."));
        }

        return minimumStock <= reorderPoint
               && reorderPoint <= targetStock
               && targetStock <= maximumStock
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "product_location.thresholds_unordered",
                "Stock thresholds must satisfy minimum <= reorder <= target <= maximum."));
    }
}
