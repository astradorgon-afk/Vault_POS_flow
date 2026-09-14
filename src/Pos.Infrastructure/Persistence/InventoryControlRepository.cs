using Microsoft.EntityFrameworkCore;
using Pos.Application.Inventory;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists stock adjustments and inventory counts. Documents are staged or loaded
/// tracked; the unit of work saves them with the ledger postings.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class InventoryControlRepository(PosDbContext context) : IInventoryControlRepository
{
    /// <inheritdoc />
    public void AddAdjustment(StockAdjustment adjustment) => context.StockAdjustments.Add(adjustment);

    /// <inheritdoc />
    public Task<StockAdjustment?> GetAdjustmentAsync(StockAdjustmentId id, CancellationToken cancellationToken)
        => context.StockAdjustments
            .AsTracking()
            .Include(a => a.Lines)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    /// <inheritdoc />
    public void AddCount(InventoryCount count) => context.InventoryCounts.Add(count);

    /// <inheritdoc />
    public Task<InventoryCount?> GetCountAsync(InventoryCountId id, CancellationToken cancellationToken)
        => context.InventoryCounts
            .AsTracking()
            .Include(c => c.Lines)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<InventoryControlLocation?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null
            ? null
            : new InventoryControlLocation(location.Kind, string.IsNullOrWhiteSpace(location.TimeZoneId) ? null : location.TimeZoneId);
    }

    /// <inheritdoc />
    public Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken)
        => context.Locations
            .AsNoTracking()
            .Where(l => l.Code == SystemLocationCodes.ExternalWriteOff)
            .Select(l => (LocationId?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
        => productIds.Count == 0
            ? []
            : await context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetProductsInCategoriesAsync(
        IReadOnlyCollection<CategoryId> categoryIds,
        CancellationToken cancellationToken)
        => categoryIds.Count == 0
            ? []
            : await context.Products
                .AsNoTracking()
                .Where(p => categoryIds.Contains(p.CategoryId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Batch>> GetBatchesAsync(
        IReadOnlyCollection<BatchId> batchIds,
        CancellationToken cancellationToken)
        => batchIds.Count == 0
            ? []
            : await context.Batches
                .AsNoTracking()
                .Where(b => batchIds.Contains(b.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<BucketSnapshot>> GetBucketsAsync(
        LocationId locationId,
        IReadOnlyCollection<ProductId>? productIds,
        IReadOnlyCollection<InventoryState> states,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryBalance> query = context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId && states.Contains(b.State));

        if (productIds is not null)
        {
            if (productIds.Count == 0)
            {
                return [];
            }

            query = query.Where(b => productIds.Contains(b.ProductId));
        }

        // Quantities are compared in memory: SQLite stores decimals as text.
        return [.. (await query.ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(b => new BucketSnapshot(b.ProductId, b.BatchKey, b.State, b.Quantity, b.AverageUnitCost))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostedLeg>> GetPostedLegsAsync(
        ReferenceDocumentType documentType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        List<InventoryMovement> movements = await context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.ReferenceDocumentType == documentType
                        && m.ReferenceDocumentId == documentId
                        && m.MovementType != InventoryMovementType.Reversal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. movements
            .OrderBy(m => m.MovementGroupId)
            .ThenBy(m => m.LegNumber)
            .Select(m => new PostedLeg(m.MovementGroupId, m.ProductId, m.BatchId, m.LocationId, m.State, m.QuantityDelta, m.UnitCost))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<ProductId>> GetProductsWithPriorVarianceAsync(
        LocationId locationId,
        IReadOnlyCollection<ProductId> productIds,
        DateTimeOffset sinceUtc,
        InventoryCountId excluding,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new HashSet<ProductId>();
        }

        var lines = await context.InventoryCounts
            .AsNoTracking()
            .Where(c => c.LocationId == locationId
                        && c.Id != excluding
                        && c.Status == InventoryCountStatus.Posted
                        && c.PostedAtUtc >= sinceUtc)
            .SelectMany(c => c.Lines)
            .Where(l => productIds.Contains(l.ProductId))
            .Select(l => new { l.ProductId, l.SystemQuantity, l.PhysicalQuantity })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return lines
            .Where(l => l.PhysicalQuantity is { } physical && physical != l.SystemQuantity)
            .Select(l => l.ProductId)
            .ToHashSet();
    }
}
