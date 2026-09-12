using Microsoft.EntityFrameworkCore;
using Pos.Application.Purchasing;
using Pos.Domain.Common;
using Pos.Domain.Locations;
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
}