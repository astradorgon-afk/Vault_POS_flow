using Microsoft.EntityFrameworkCore;
using Pos.Application.Inventory;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Customers, as a device knows them: it does not.
/// </summary>
/// <remarks>
/// The device caches no customer records yet, so a lookup answers null and a
/// sale naming a customer is refused offline while an anonymous cash sale is
/// not. That is the honest answer rather than a convenient one: a sale that
/// silently dropped its customer would lose the account the customer expects to
/// be credited. Customer create and edit are declared offline-capable in
/// OFFLINE_SYNC.md §1 and stay `Pending` until a `customer_lite` cache exists.
/// </remarks>
public sealed class DeviceCustomerRepository : ICustomerRepository
{
    /// <inheritdoc />
    public Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Customer?>(null);
    }

    /// <inheritdoc />
    public Task<Result<CustomerId>> AddAsync(Customer customer, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(AddAsync));

    /// <inheritdoc />
    public Task<Result<CustomerId>> UpdateAsync(Customer customer, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(UpdateAsync));

    /// <inheritdoc />
    public Task<CustomerSearchResult> SearchAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(SearchAsync));

    private static NotSupportedException NotOnADeviceYet(string member)
        => new(FormattableString.Invariant(
            $"{member} needs a customer cache the device does not carry; the use cases that would call it are not registered here."));
}

/// <summary>
/// The batches a device may sell from, first-expiry-first-out.
/// </summary>
/// <remarks>
/// It reads the downloaded batch cache joined to the device's own balances, so
/// it offers only stock this register actually holds — a batch the catalogue
/// knows about but the register has none of is not sellable here. Expired
/// batches are excluded; offering them is the expired-override path, which needs
/// a permission a device snapshot cannot carry.
/// </remarks>
/// <param name="context">The scoped device context.</param>
/// <param name="clock">The device clock, which decides what counts as expired.</param>
public sealed class DeviceExpiryService(
    PosDeviceDbContext context,
    Pos.Application.Common.Abstractions.ISystemClock clock) : IExpiryService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SellableBatchItem>> GetSellableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // Driven by what the register holds, not by what the batch cache knows.
        // The handler allocates every line first-expiry-first-out, batch-tracked
        // or not, so untracked stock has to come back too — as the one bucket it
        // lives in, with an empty batch key. Joining the batch cache instead
        // would silently return nothing for untracked products and read, to the
        // ledger, as no stock at all.
        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId
                        && b.ProductId == productId
                        && b.State == InventoryState.Available
                        && b.Quantity > 0m)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (balances.Count == 0)
        {
            return [];
        }

        bool tracksBatches = await context.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => p.TracksBatches)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        BatchId[] batchKeys = [.. balances.Where(b => !b.BatchKey.IsEmpty).Select(b => b.BatchKey).Distinct()];

        Dictionary<BatchId, DeviceCachedBatch> batchById = await context.Batches
            .AsNoTracking()
            .Where(b => batchKeys.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken)
            .ConfigureAwait(false);

        List<SellableBatchItem> items = [];

        foreach (InventoryBalance balance in balances)
        {
            if (balance.BatchKey.IsEmpty)
            {
                // A batch-tracked product always carries a batch bucket, so an
                // empty key on one is stock that cannot be identified and is not
                // offered for sale.
                if (!tracksBatches)
                {
                    items.Add(new SellableBatchItem(
                        balance.ProductId,
                        balance.BatchKey,
                        string.Empty,
                        balance.Quantity,
                        null,
                        balance.AverageUnitCost));
                }

                continue;
            }

            // A batch the register holds but has never been told about cannot be
            // sold: without its expiry date there is no way to know it is safe.
            if (!batchById.TryGetValue(balance.BatchKey, out DeviceCachedBatch? batch))
            {
                continue;
            }

            // Expired stock is the override path, which needs a permission a
            // device snapshot cannot carry.
            if (batch.ExpiresOn is { } expires && expires < today)
            {
                continue;
            }

            items.Add(new SellableBatchItem(
                balance.ProductId,
                batch.Id,
                batch.LotNumber,
                balance.Quantity,
                batch.ExpiresOn,
                batch.UnitCost));
        }

        // First expiry first out; undated stock last, oldest receipt first.
        return
        [
            .. items
                .OrderBy(i => i.ExpiresOn ?? DateOnly.MaxValue)
                .ThenBy(i => i.LotNumber, StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExpiredBatchItem>> GetExpiredBatchesAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetExpiredBatchesAsync));

    /// <inheritdoc />
    public Task<IReadOnlyList<ExpiringBatchSummary>> GetExpiringBatchesAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetExpiringBatchesAsync));

    /// <inheritdoc />
    public Task<Result<ExpiryRunResult>> PostExpiryRunAsync(
        LocationId locationId,
        LedgerActor actor,
        CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(PostExpiryRunAsync));

    /// <inheritdoc />
    public Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetExternalWriteOffLocationIdAsync));

    /// <inheritdoc />
    public Task<ExpiryLocationInfo?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetLocationAsync));

    /// <inheritdoc />
    public Task<IReadOnlyList<ExpiryLocationInfo>> GetAllStockingLocationsAsync(CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetAllStockingLocationsAsync));

    /// <inheritdoc />
    public Task<int> GetExpiryWarningDaysAsync(LocationId locationId, CancellationToken cancellationToken)
        => throw NotOnADeviceYet(nameof(GetExpiryWarningDaysAsync));

    private static NotSupportedException NotOnADeviceYet(string member)
        => new(FormattableString.Invariant(
            $"{member} belongs to the expiry run, which is a central use case; it is not registered on a device."));
}
