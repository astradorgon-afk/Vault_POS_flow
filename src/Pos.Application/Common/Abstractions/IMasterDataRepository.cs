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
}