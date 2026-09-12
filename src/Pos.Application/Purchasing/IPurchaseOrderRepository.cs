using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// The entry point the application layer uses to persist and load purchase
/// orders. Implemented by the infrastructure layer, which owns persistence.
/// </summary>
public interface IPurchaseOrderRepository
{
    /// <summary>Persists a newly created draft purchase order.</summary>
    /// <param name="order">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier.</returns>
    Task<Result<PurchaseOrderId>> AddAsync(PurchaseOrder order, CancellationToken cancellationToken);

    /// <summary>Loads a purchase order with its lines and decisions, tracked.</summary>
    /// <param name="id">The order identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The order, or <see langword="null"/> when it does not exist.</returns>
    Task<PurchaseOrder?> GetByIdAsync(PurchaseOrderId id, CancellationToken cancellationToken);

    /// <summary>Determines whether a supplier exists and is active.</summary>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the supplier can be ordered from.</returns>
    Task<bool> IsActiveSupplierAsync(SupplierId supplierId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the base unit of measure of an active product, or <see langword="null"/>
    /// when the product does not exist or is inactive.
    /// </summary>
    /// <param name="productId">The product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The product's base unit of measure.</returns>
    Task<UnitOfMeasureId?> GetActiveProductBaseUnitAsync(ProductId productId, CancellationToken cancellationToken);

    /// <summary>Determines whether a location exists and can physically receive goods.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when goods may be delivered there.</returns>
    Task<bool> CanReceiveGoodsAsync(LocationId locationId, CancellationToken cancellationToken);
}