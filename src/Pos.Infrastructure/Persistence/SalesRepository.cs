using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Reads the facts a completed sale is re-derived from, and persists the sale.
/// Mutations opt into tracking explicitly because the context defaults to
/// NoTracking; a detached change would silently save nothing.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class SalesRepository(PosDbContext context) : ISalesRepository
{
    /// <inheritdoc />
    public async Task<Result<SaleId>> AddAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);

        context.Sales.Add(sale);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<SaleId>.Success(sale.Id);
    }

    /// <inheritdoc />
    public async Task<SaleLocationFacts?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null
            ? null
            : new SaleLocationFacts(location.Kind, location.Settings ?? LocationSettings.Default);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetSaleProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
        {
            return [];
        }

        return await context.Products
            .AsNoTracking()
            .Include(p => p.Prices)
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<LocationId?> GetExternalCustomerLocationIdAsync(CancellationToken cancellationToken)
        => context.Locations
            .AsNoTracking()
            .Where(l => l.Code == SystemLocationCodes.ExternalCustomer)
            .Select(l => (LocationId?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredSaleBatch>> GetExpiredAvailableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        // Same shape as the sellable-shelf query in ExpiryService, but the
        // fence is the sale's business date instead of "today" and the batches
        // returned are the ones *past* it, so a sale being completed against
        // an earlier business date cannot silently draw on a batch that was
        // still sellable on the shelf that day.
        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId
                && b.ProductId == productId
                && b.State == InventoryState.Available
                && b.BatchKey != BatchId.Empty
                && b.Quantity > 0m)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (balances.Count == 0)
        {
            return [];
        }

        List<BatchId> batchIds = [.. balances.Select(b => b.BatchKey).Distinct()];

        List<Batch> batches = await context.Batches
            .AsNoTracking()
            .Where(b => batchIds.Contains(b.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BatchId, Batch> batchById = batches.ToDictionary(b => b.Id);

        List<ExpiredSaleBatch> items = [];

        foreach (InventoryBalance balance in balances)
        {
            if (!batchById.TryGetValue(balance.BatchKey, out Batch? batch))
            {
                continue;
            }

            // The business date is the cutoff: a batch expiring on or after it
            // was still sellable that day and belongs to the normal shelf.
            if (batch.ExpiresOn is not { } expiresOn || expiresOn >= businessDate)
            {
                continue;
            }

            items.Add(new ExpiredSaleBatch(
                balance.BatchKey,
                string.IsNullOrWhiteSpace(batch.LotNumber) ? null : batch.LotNumber,
                balance.Quantity,
                expiresOn,
                balance.AverageUnitCost));
        }

        return [.. items
            .OrderBy(i => i.ExpiresOn ?? DateOnly.MaxValue)
            .ThenBy(i => i.BatchId?.Value ?? Guid.Empty)];
    }
}