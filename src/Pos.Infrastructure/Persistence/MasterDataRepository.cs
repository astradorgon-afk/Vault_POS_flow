using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Organizations;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists master-data aggregates. Uniqueness rules that the aggregates cannot
/// decide without the database (codes, names, SKUs) are checked here, inside
/// the same save that inserts the row, so a conflict can never be saved.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class MasterDataRepository(PosDbContext context) : IMasterDataRepository
{
    /// <inheritdoc />
    public async Task<Result<LocationId>> CreateLocationAsync(
        Location location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        bool codeTaken = await context.Locations
            .AnyAsync(l => l.OrganizationId == location.OrganizationId && l.Code == location.Code, cancellationToken)
            .ConfigureAwait(false);

        if (codeTaken)
        {
            return Result<LocationId>.Failure(LocationErrors.DuplicateCode(location.Code));
        }

        context.Locations.Add(location);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<LocationId>.Success(location.Id);
    }

    /// <inheritdoc />
    public async Task<Result<LocationId>> UpdateLocationSettingsAsync(
        LocationId locationId,
        LocationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Opted into tracking explicitly: the context defaults to NoTracking
        // because reads dominate, and a detached mutation would silently save
        // nothing. This is the same intent marker used on the identity write
        // paths.
        Location? location = await context.Locations
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return Result<LocationId>.Failure(LocationErrors.Unknown(locationId));
        }

        Result applied = location.UpdateSettings(settings);

        if (applied.IsFailure)
        {
            return Result<LocationId>.Failure(applied.Errors);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<LocationId>.Success(location.Id);
    }

    /// <inheritdoc />
    public async Task<Result<CategoryId>> CreateCategoryAsync(
        ProductCategory category,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);

        bool codeTaken = await context.Categories
            .AnyAsync(c => c.Code == category.Code, cancellationToken)
            .ConfigureAwait(false);

        if (codeTaken)
        {
            return Result<CategoryId>.Failure(CatalogErrors.CodeTaken("category", category.Code));
        }

        context.Categories.Add(category);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<CategoryId>.Success(category.Id);
    }

    /// <inheritdoc />
    public async Task<Result<BrandId>> CreateBrandAsync(
        Brand brand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brand);

        bool nameTaken = await context.Brands
            .AnyAsync(b => b.Name == brand.Name, cancellationToken)
            .ConfigureAwait(false);

        if (nameTaken)
        {
            return Result<BrandId>.Failure(CatalogErrors.CodeTaken("brand", brand.Name));
        }

        context.Brands.Add(brand);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<BrandId>.Success(brand.Id);
    }

    /// <inheritdoc />
    public async Task<Result<UnitOfMeasureId>> CreateUnitOfMeasureAsync(
        UnitOfMeasure unit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);

        bool codeTaken = await context.UnitsOfMeasure
            .AnyAsync(u => u.Code == unit.Code, cancellationToken)
            .ConfigureAwait(false);

        if (codeTaken)
        {
            return Result<UnitOfMeasureId>.Failure(CatalogErrors.CodeTaken("uom", unit.Code));
        }

        context.UnitsOfMeasure.Add(unit);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<UnitOfMeasureId>.Success(unit.Id);
    }

    /// <inheritdoc />
    public async Task<Result<SupplierId>> CreateSupplierAsync(
        Supplier supplier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        bool codeTaken = await context.Suppliers
            .AnyAsync(s => s.Code == supplier.Code, cancellationToken)
            .ConfigureAwait(false);

        if (codeTaken)
        {
            return Result<SupplierId>.Failure(CatalogErrors.CodeTaken("supplier", supplier.Code));
        }

        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<SupplierId>.Success(supplier.Id);
    }

    /// <inheritdoc />
    public async Task<Result<ProductId>> CreateProductAsync(
        Product product,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);

        bool skuTaken = await context.Products
            .AnyAsync(p => p.Sku == product.Sku, cancellationToken)
            .ConfigureAwait(false);

        if (skuTaken)
        {
            return Result<ProductId>.Failure(CatalogErrors.CodeTaken("product", product.Sku.Value));
        }

        if (product.Barcodes.Count > 0)
        {
            string[] values = [.. product.Barcodes.Select(b => b.Value)];

            List<string> taken = await context.ProductBarcodes
                .Where(b => values.Contains(b.Value))
                .Select(b => b.Value)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (taken.Count > 0)
            {
                return Result<ProductId>.Failure(CatalogErrors.DuplicateBarcode(taken[0]));
            }
        }

        context.Products.Add(product);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<ProductId>.Success(product.Id);
    }
}