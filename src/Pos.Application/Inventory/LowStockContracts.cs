using Pos.Domain.Common;

namespace Pos.Application.Inventory;

/// <summary>Reads stocked products whose available quantity has fallen to their reorder point.</summary>
public interface ILowStockRepository
{
    /// <summary>
    /// Finds every active, stocked product at an active, non-external location
    /// whose available quantity is at or below a positive reorder point.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The low-stock items, ordered by location then product.</returns>
    Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(CancellationToken cancellationToken);
}

/// <summary>A stocked product at or below its reorder point.</summary>
/// <param name="LocationId">The location running low.</param>
/// <param name="ProductId">The product.</param>
/// <param name="ProductName">The product's display name.</param>
/// <param name="Available">The available quantity summed across batches.</param>
/// <param name="MinimumStock">The configured minimum stock.</param>
/// <param name="ReorderPoint">The configured reorder point.</param>
public sealed record LowStockItem(
    LocationId LocationId,
    ProductId ProductId,
    string ProductName,
    decimal Available,
    decimal MinimumStock,
    decimal ReorderPoint);
