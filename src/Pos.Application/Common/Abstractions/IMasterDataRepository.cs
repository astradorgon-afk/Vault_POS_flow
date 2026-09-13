using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Organizations;

namespace Pos.Application.Common.Abstractions;

/// <summary>
/// The single entry point the application layer uses to persist master data.
/// Implemented by the infrastructure layer, which owns the persistence
/// concerns; keeping the surface here lets the use cases stay persistence-free
/// and unit-testable.
/// </summary>
public interface IMasterDataRepository
{
    /// <summary>Persists a newly created location.</summary>
    /// <param name="location">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier, or a conflict failure.</returns>
    Task<Result<LocationId>> CreateLocationAsync(Location location, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a location's operational settings. The location is loaded inside
    /// the caller's transaction, so the change applies atomically with the audit
    /// entry and any surrounding work.
    /// </summary>
    /// <param name="locationId">The location to change.</param>
    /// <param name="settings">The new settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifier, or a not-found/conflict failure.</returns>
    Task<Result<LocationId>> UpdateLocationSettingsAsync(
        LocationId locationId,
        LocationSettings settings,
        CancellationToken cancellationToken);

    /// <summary>Persists a newly created category.</summary>
    /// <param name="category">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier, or a conflict failure.</returns>
    Task<Result<CategoryId>> CreateCategoryAsync(ProductCategory category, CancellationToken cancellationToken);

    /// <summary>Persists a newly created brand.</summary>
    /// <param name="brand">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier, or a conflict failure.</returns>
    Task<Result<BrandId>> CreateBrandAsync(Brand brand, CancellationToken cancellationToken);

    /// <summary>Persists a newly created unit of measure.</summary>
    /// <param name="unit">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier, or a conflict failure.</returns>
    Task<Result<UnitOfMeasureId>> CreateUnitOfMeasureAsync(UnitOfMeasure unit, CancellationToken cancellationToken);

    /// <summary>Persists a newly created supplier.</summary>
    /// <param name="supplier">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier, or a conflict failure.</returns>
    Task<Result<SupplierId>> CreateSupplierAsync(Supplier supplier, CancellationToken cancellationToken);

    /// <summary>Persists a newly created product.</summary>
    /// <param name="product">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier, or a conflict failure.</returns>
    Task<Result<ProductId>> CreateProductAsync(Product product, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a product with every child collection, tracked, so changes made to
    /// it are saved by the surrounding unit of work.
    /// </summary>
    /// <param name="productId">The product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The product, or <see langword="null"/> when it does not exist.</returns>
    Task<Product?> GetProductForUpdateAsync(ProductId productId, CancellationToken cancellationToken);

    /// <summary>Determines whether any product, active or retired, holds a barcode value.</summary>
    /// <param name="value">The normalised barcode value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the value is taken.</returns>
    Task<bool> BarcodeInUseAsync(string value, CancellationToken cancellationToken);

    /// <summary>
    /// Checks that the master data a product change refers to exists. Locations
    /// must be real stocking locations, not external counterparties.
    /// </summary>
    /// <param name="references">The references to check; null members are skipped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first missing reference's error, or <see langword="null"/> when all exist.</returns>
    Task<Error?> FindMissingReferenceAsync(ProductReferences references, CancellationToken cancellationToken);
}

/// <summary>Master data referenced by a product change.</summary>
/// <param name="Category">A category, or null.</param>
/// <param name="Brand">A brand, or null.</param>
/// <param name="Supplier">A supplier, or null.</param>
/// <param name="Location">A stocking location, or null.</param>
/// <param name="Units">Units of measure; empty to skip.</param>
public sealed record ProductReferences(
    CategoryId? Category = null,
    BrandId? Brand = null,
    SupplierId? Supplier = null,
    LocationId? Location = null,
    IReadOnlyCollection<UnitOfMeasureId>? Units = null);