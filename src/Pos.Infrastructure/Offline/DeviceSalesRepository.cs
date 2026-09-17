using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Sales held on the device until they sync, and the catalogue a sale reads,
/// projected from the downloaded cache.
/// </summary>
/// <remarks>
/// <para>
/// It backs the same `CompleteSaleCommandHandler` the server runs (ADR-0008).
/// What made that possible is C37: the handler asks for a `SaleProduct` — the
/// fields a line needs, with the price resolved — rather than a `Product`
/// aggregate, which a device has no category, brand or unit of measure to
/// rebuild.
/// </para>
/// <para>
/// The members that answer questions about returns, refunds and reprints refuse
/// outright. Those use cases are not registered on a device, so reaching one is
/// a registration mistake rather than a runtime condition, and a repository that
/// improvised an answer would hide it.
/// </para>
/// </remarks>
/// <param name="context">The scoped device context.</param>
/// <param name="outbox">The upload queue this repository enqueues to.</param>
public sealed class DeviceSalesRepository(
    PosDeviceDbContext context,
    IDeviceOutbox outbox) : ISalesRepository
{
    /// <inheritdoc />
    public async Task<Result<SaleId>> AddAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);

        await context.LocalSales.AddAsync(sale, cancellationToken).ConfigureAwait(false);

        await outbox.EnqueueAsync(
            SyncEventType.SaleCompleted,
            new SaleSyncPayload(
                sale.Id.Value,
                sale.Number,
                sale.LocationId.Value,
                sale.DeviceId.Value,
                sale.CashierShiftId.Value,
                sale.CompletedByUserId.Value,
                sale.CustomerId?.Value,
                sale.NetTotal,
                sale.CompletedAtUtc,
                sale.BusinessDate),
            sale.LocationId,
            cancellationToken).ConfigureAwait(false);

        return Result<SaleId>.Success(sale.Id);
    }

    /// <inheritdoc />
    public async Task<Sale?> GetByIdAsync(SaleId saleId, CancellationToken cancellationToken)
        => await context.LocalSales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<Result<SaleId>> UpdateAsync(Sale sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        cancellationToken.ThrowIfCancellationRequested();

        // Tracked by the read that loaded it; the unit of work writes it.
        return Task.FromResult(Result<SaleId>.Success(sale.Id));
    }

    /// <inheritdoc />
    public async Task<SaleLocationFacts?> GetLocationAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        var cached = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => new { l.Kind, l.SettingsJson })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Settings the feed has not carried read as the strictest configuration.
        return cached is null
            ? null
            : new SaleLocationFacts(cached.Kind, LocationSettings.FromJson(cached.SettingsJson));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SaleProduct>> GetSaleProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        LocationId locationId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
        {
            return [];
        }

        List<DeviceCachedProduct> products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DeviceCachedProductPrice> prices = await context.ProductPrices
            .AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId)
                        && p.EffectiveFromUtc <= at
                        && (p.EffectiveToUtc == null || p.EffectiveToUtc > at)
                        && (p.LocationId == null || p.LocationId == locationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. products.Select(product =>
            {
                // The same precedence the aggregate applies: a row scoped to this
                // location beats a global one, and the latest start wins within each.
                DeviceCachedProductPrice? effective = prices
                    .Where(p => p.ProductId == product.Id)
                    .OrderByDescending(p => p.LocationId != null)
                    .ThenByDescending(p => p.EffectiveFromUtc)
                    .FirstOrDefault();

                return new SaleProduct(
                    product.Id,
                    product.Name,
                    product.IsVatExempt,
                    product.TracksBatches,
                    effective?.Id,
                    effective?.Amount);
            }),
        ];
    }

    /// <inheritdoc />
    public async Task<LocationId?> GetExternalCustomerLocationIdAsync(CancellationToken cancellationToken)
        => await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind == Pos.Domain.Locations.LocationKind.External
                        && l.Code == Pos.Domain.Locations.SystemLocationCodes.ExternalCustomer)
            .Select(l => (LocationId?)l.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredSaleBatch>> GetExpiredAvailableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        DateOnly today = businessDate;

        // Only what the device actually holds: a batch with no local balance is
        // stock this register does not have, whatever the catalogue says.
        return
        [
            .. await (from batch in context.Batches.AsNoTracking()
                      join balance in context.InventoryBalances.AsNoTracking()
                        on batch.Id equals balance.BatchKey
                      where batch.ProductId == productId
                            && balance.LocationId == locationId
                            && balance.ProductId == productId
                            && balance.State == InventoryState.Available
                            && balance.Quantity > 0
                            && batch.ExpiresOn != null
                            && batch.ExpiresOn < today
                      select new ExpiredSaleBatch(
                          batch.Id,
                          batch.LotNumber,
                          balance.Quantity,
                          batch.ExpiresOn,
                          batch.UnitCost))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
        ];
    }

    /// <inheritdoc />
    public Task<Result<SalesReturnId>> AddReturnAsync(SalesReturn salesReturn, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(AddReturnAsync));

    /// <inheritdoc />
    public Task<SalesReturn?> GetReturnByIdAsync(SalesReturnId salesReturnId, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetReturnByIdAsync));

    /// <inheritdoc />
    public Task<Result<RefundId>> AddRefundAsync(
        SalesReturnId salesReturnId,
        Refund refund,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(AddRefundAsync));

    /// <inheritdoc />
    public Task<Refund?> GetRefundByEventAsync(EventId eventId, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetRefundByEventAsync));

    /// <inheritdoc />
    public Task<Result<ReceiptPrintId>> AddReceiptPrintAsync(
        SaleReceiptPrint print,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(AddReceiptPrintAsync));

    /// <inheritdoc />
    public Task<IReadOnlyList<Product>> GetReturnProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetReturnProductsAsync));

    private static NotSupportedException NotOnADeviceYet(string member)
        => new(FormattableString.Invariant(
            $"{member} belongs to a use case that is not registered on a device; reaching it is a registration mistake, not a runtime condition."));
}

/// <summary>What the server is told about a sale that happened offline.</summary>
/// <param name="SaleId">The sale the device created.</param>
/// <param name="Number">The device-scoped SAL number printed on the receipt.</param>
/// <param name="LocationId">Where it was sold.</param>
/// <param name="DeviceId">The register.</param>
/// <param name="ShiftId">The shift it belongs to.</param>
/// <param name="CompletedByUserId">The cashier who completed it.</param>
/// <param name="CustomerId">The named customer, when there was one.</param>
/// <param name="NetTotal">What was actually taken.</param>
/// <param name="CompletedAtUtc">The device clock at completion.</param>
/// <param name="BusinessDate">The business date in the location's timezone.</param>
public sealed record SaleSyncPayload(
    Guid SaleId,
    string Number,
    Guid LocationId,
    Guid DeviceId,
    Guid ShiftId,
    Guid CompletedByUserId,
    Guid? CustomerId,
    decimal NetTotal,
    DateTimeOffset CompletedAtUtc,
    DateOnly BusinessDate);
