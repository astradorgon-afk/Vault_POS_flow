using Microsoft.EntityFrameworkCore;
using Pos.Application.Inventory;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Compares available balances with per-location stocking thresholds.
/// </summary>
/// <remarks>
/// Thresholds and quantities are compared in memory: the settings set is small,
/// and decimal comparison and aggregation do not translate on every provider.
/// A reorder point of zero means the thresholds were never configured, so those
/// rows never alert.
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class LowStockRepository(PosDbContext context) : ILowStockRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(CancellationToken cancellationToken)
    {
        List<LocationId> locationIds = await context.Locations
            .AsNoTracking()
            .Where(l => l.IsActive && l.Kind != LocationKind.External)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ProductLocationSetting> settings = await context.ProductLocationSettings
            .AsNoTracking()
            .Where(s => s.IsStocked && locationIds.Contains(s.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        settings.RemoveAll(s => s.ReorderPoint <= 0m);
        if (settings.Count == 0)
        {
            return [];
        }

        List<ProductId> productIds = settings.Select(s => s.ProductId).Distinct().ToList();

        Dictionary<ProductId, string> activeProducts = await context.Products
            .AsNoTracking()
            .Where(p => p.IsActive && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken)
            .ConfigureAwait(false);

        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.State == InventoryState.Available && productIds.Contains(b.ProductId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(LocationId, ProductId), decimal> available = [];
        foreach (InventoryBalance balance in balances)
        {
            (LocationId, ProductId) key = (balance.LocationId, balance.ProductId);
            available[key] = available.GetValueOrDefault(key) + balance.Quantity;
        }

        return settings
            .Where(s => activeProducts.ContainsKey(s.ProductId))
            .Select(s => new LowStockItem(
                s.LocationId,
                s.ProductId,
                activeProducts[s.ProductId],
                available.GetValueOrDefault((s.LocationId, s.ProductId)),
                s.MinimumStock,
                s.ReorderPoint))
            .Where(i => i.Available <= i.ReorderPoint)
            .OrderBy(i => i.LocationId.Value)
            .ThenBy(i => i.ProductId.Value)
            .ToList();
    }
}
