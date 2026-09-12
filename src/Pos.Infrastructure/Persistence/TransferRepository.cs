using Microsoft.EntityFrameworkCore;
using Pos.Application.Transfers;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists transfer orders. Mutations opt into tracking explicitly because the
/// context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class TransferRepository(PosDbContext context) : ITransferRepository
{
    /// <inheritdoc />
    public async Task<Result<TransferOrderId>> AddAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);

        context.Transfers.Add(transfer);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<TransferOrderId>.Success(transfer.Id);
    }

    /// <inheritdoc />
    public Task<Transfer?> GetByIdAsync(TransferOrderId id, CancellationToken cancellationToken)
        => context.Transfers
            .AsTracking()
            .Include(t => t.Lines)
            .Include(t => t.Allocations)
            .Include(t => t.Discrepancies)
            .Include(t => t.CustodyEvents)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Transfer?> GetByDiscrepancyAsync(
        TransferDiscrepancyId discrepancyId,
        CancellationToken cancellationToken)
        => context.Transfers
            .AsTracking()
            .Include(t => t.Lines)
            .Include(t => t.Allocations)
            .Include(t => t.Discrepancies)
            .Include(t => t.CustodyEvents)
            .FirstOrDefaultAsync(t => t.Discrepancies.Any(d => d.Id == discrepancyId), cancellationToken);

    /// <inheritdoc />
    public async Task<TransferLocationInfo?> GetLocationInfoAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null ? null : new TransferLocationInfo(location.Kind, location.TimeZoneId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        return await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PickableStockItem>> GetPickableStockAsync(
        LocationId locationId,
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        // SQLite stores decimals as text, so costs are resolved from in-memory
        // lookups rather than translated to a SQL join.
        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId
                && productIds.Contains(b.ProductId)
                && b.State == InventoryState.Available
                && b.Quantity > 0m)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BatchId, Batch> batchById = (await GetBatchesAsync(
            [.. balances.Where(b => !b.BatchKey.IsEmpty).Select(b => b.BatchKey).Distinct()],
            cancellationToken).ConfigureAwait(false))
            .ToDictionary(b => b.Id);

        Dictionary<ProductId, Product> productById = (await GetProductsAsync(productIds, cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        List<PickableStockItem> items = [];

        foreach (InventoryBalance balance in balances)
        {
            if (balance.BatchKey.IsEmpty)
            {
                if (!productById.TryGetValue(balance.ProductId, out Product? product))
                {
                    continue;
                }

                items.Add(new PickableStockItem(
                    balance.ProductId,
                    balance.BatchKey,
                    balance.Quantity,
                    null,
                    product.DefaultPurchaseCost));
            }
            else if (batchById.TryGetValue(balance.BatchKey, out Batch? batch))
            {
                // Batch buckets are the snapshot cost: expiry-aware picking must
                // consume at the cost the batch was received at, not the current
                // product default.
                items.Add(new PickableStockItem(
                    balance.ProductId,
                    balance.BatchKey,
                    balance.Quantity,
                    batch.ExpiresOn,
                    batch.UnitCost));
            }
        }

        return items.OrderBy(i => i.ProductId).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Batch>> GetBatchesAsync(
        IReadOnlyCollection<BatchId> batchIds,
        CancellationToken cancellationToken)
    {
        if (batchIds.Count == 0)
        {
            return [];
        }

        return await context.Batches
            .AsNoTracking()
            .Where(b => batchIds.Contains(b.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken)
        => context.Locations
            .AsNoTracking()
            .Where(l => l.Code == SystemLocationCodes.ExternalWriteOff)
            .Select(l => (LocationId?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);
}