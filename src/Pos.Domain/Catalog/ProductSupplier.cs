using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A link between a product and a supplier it can be purchased from, carrying
/// the supplier's own code and cost information.
/// </summary>
public sealed class ProductSupplier
{
    internal ProductSupplier(
        ProductSupplierId id,
        ProductId productId,
        SupplierId supplierId,
        string? supplierSku,
        decimal? lastCost,
        int leadTimeDays,
        decimal? minimumOrderQuantity,
        bool isPreferred)
    {
        Id = id;
        ProductId = productId;
        SupplierId = supplierId;
        SupplierSku = supplierSku;
        LastCost = lastCost;
        LeadTimeDays = leadTimeDays;
        MinimumOrderQuantity = minimumOrderQuantity;
        IsPreferred = isPreferred;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ProductSupplier()
    {
    }

    /// <summary>Gets the link row identifier.</summary>
    public ProductSupplierId Id { get; }

    /// <summary>Gets the owning product.</summary>
    public ProductId ProductId { get; }

    /// <summary>Gets the supplier.</summary>
    public SupplierId SupplierId { get; }

    /// <summary>Gets the supplier's own code for this product, if any.</summary>
    public string? SupplierSku { get; }

    /// <summary>Gets the last recorded unit cost from this supplier, if any.</summary>
    public decimal? LastCost { get; }

    /// <summary>Gets this supplier's typical lead time for the product.</summary>
    public int LeadTimeDays { get; }

    /// <summary>Gets the supplier's minimum order quantity, if any.</summary>
    public decimal? MinimumOrderQuantity { get; }

    /// <summary>Gets whether this is the preferred supplier for the product.</summary>
    public bool IsPreferred { get; }

    /// <summary>Creates a product-supplier link.</summary>
    /// <returns>The link, or a validation failure.</returns>
    public static Result<ProductSupplier> Create(
        ProductId productId,
        SupplierId supplierId,
        string? supplierSku,
        decimal? lastCost,
        int leadTimeDays,
        decimal? minimumOrderQuantity,
        bool isPreferred)
    {
        if (supplierId.IsEmpty)
        {
            return Result<ProductSupplier>.Failure(
                Error.Validation("product_supplier.supplier_required", "A supplier is required."));
        }

        if (lastCost is < 0m)
        {
            return Result<ProductSupplier>.Failure(
                Error.Validation("product_supplier.cost_negative", "Last cost cannot be negative."));
        }

        if (minimumOrderQuantity is < 0m)
        {
            return Result<ProductSupplier>.Failure(
                Error.Validation("product_supplier.minimum_order_negative", "Minimum order quantity cannot be negative."));
        }

        return Result<ProductSupplier>.Success(new ProductSupplier(
            ProductSupplierId.New(),
            productId,
            supplierId,
            string.IsNullOrWhiteSpace(supplierSku) ? null : supplierSku.Trim(),
            lastCost,
            Math.Max(0, leadTimeDays),
            minimumOrderQuantity,
            isPreferred));
    }
}