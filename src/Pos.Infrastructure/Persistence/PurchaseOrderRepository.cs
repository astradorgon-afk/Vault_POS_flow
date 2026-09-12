using Microsoft.EntityFrameworkCore;
using Pos.Application.Purchasing;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Purchasing;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists purchase orders. Mutations opt into tracking explicitly because the
/// context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class PurchaseOrderRepository(PosDbContext context) : IPurchaseOrderRepository
{
    /// <inheritdoc />
    public async Task<Result<PurchaseOrderId>> AddAsync(
        PurchaseOrder order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        context.PurchaseOrders.Add(order);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<PurchaseOrderId>.Success(order.Id);
    }

    /// <inheritdoc />
    public Task<PurchaseOrder?> GetByIdAsync(PurchaseOrderId id, CancellationToken cancellationToken)
        => context.PurchaseOrders
            .AsTracking()
            .Include(o => o.Lines)
            .Include(o => o.Approvals)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsActiveSupplierAsync(SupplierId supplierId, CancellationToken cancellationToken)
        => context.Suppliers
            .AsNoTracking()
            .AnyAsync(s => s.Id == supplierId && s.IsActive, cancellationToken);

    /// <inheritdoc />
    public Task<UnitOfMeasureId?> GetActiveProductBaseUnitAsync(
        ProductId productId,
        CancellationToken cancellationToken)
        => context.Products
            .AsNoTracking()
            .Where(p => p.Id == productId && p.IsActive)
            .Select(p => (UnitOfMeasureId?)p.BaseUnitOfMeasureId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> CanReceiveGoodsAsync(LocationId locationId, CancellationToken cancellationToken)
        => context.Locations
            .AsNoTracking()
            .AnyAsync(l => l.Id == locationId && l.Kind != LocationKind.External, cancellationToken);

    /// <inheritdoc />
    public async Task<PurchaseReceivingContext?> GetReceivingContextAsync(
        PurchaseOrderId orderId,
        IReadOnlyCollection<(PurchaseOrderLineId Line, string? Lot)> requestedLots,
        CancellationToken cancellationToken)
    {
        PurchaseOrder? order = await GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false);

        if (order is null)
        {
            return null;
        }

        Location? external = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Code == SystemLocationCodes.ExternalSupplier, cancellationToken)
            .ConfigureAwait(false);

        Location? destination = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == order.DestinationLocationId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(ProductId Product, string Lot), BatchId> existingBatches = [];

        if (requestedLots.Count > 0)
        {
            ProductId[] lotProducts = [.. requestedLots
                .Where(r => !string.IsNullOrWhiteSpace(r.Lot))
                .Join(order.Lines, r => r.Line, l => l.Id, (r, l) => l.ProductId)
                .Distinct()];

            if (lotProducts.Length > 0)
            {
                List<Batch> batches = await context.Batches
                    .AsNoTracking()
                    .Where(b => lotProducts.Contains(b.ProductId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach ((PurchaseOrderLineId line, string? lot) in requestedLots)
                {
                    if (string.IsNullOrWhiteSpace(lot))
                    {
                        continue;
                    }

                    PurchaseOrderLine? orderLine = order.Lines.FirstOrDefault(l => l.Id == line);

                    if (orderLine is null)
                    {
                        continue;
                    }

                    Batch? match = batches.FirstOrDefault(
                        b => b.ProductId == orderLine.ProductId
                             && string.Equals(b.LotNumber, lot, StringComparison.Ordinal));

                    if (match is not null)
                    {
                        existingBatches[(orderLine.ProductId, match.LotNumber)] = match.Id;
                    }
                }
            }
        }

        // SQLite stores decimals as text, so the cumulative total is computed
        // in memory rather than translated to an SQL SUM.
        List<GoodsReceiptLine> postedLines = await context.GoodsReceipts
            .AsNoTracking()
            .Where(r => r.PurchaseOrderId == orderId && r.Status == GoodsReceiptStatus.Posted)
            .SelectMany(r => r.Lines)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<PurchaseOrderLineId, decimal> receivedByLine = postedLines
            .GroupBy(l => l.PurchaseOrderLineId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityReceived));

        return new PurchaseReceivingContext(
            order,
            external?.Id,
            destination?.TimeZoneId,
            destination?.Kind ?? LocationKind.External,
            existingBatches,
            receivedByLine);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetProductsForReceivingAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        return await context.Products
            .AsTracking()
            .Include(p => p.Suppliers)
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Result<GoodsReceiptId>> AddReceiptAsync(
        GoodsReceipt receipt,
        IReadOnlyList<Batch> newBatches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        context.GoodsReceipts.Add(receipt);

        if (newBatches.Count > 0)
        {
            context.Batches.AddRange(newBatches);
        }

        // The unit-of-work behaviour commits the surrounding transaction at the
        // end of the pipeline; staging here keeps the receipt, the ledger group
        // and the GRN counter atomic.
        return Task.FromResult(Result<GoodsReceiptId>.Success(receipt.Id));
    }
}