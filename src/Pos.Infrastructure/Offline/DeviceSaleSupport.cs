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
                            && (batch.ExpiresOn == null || batch.ExpiresOn >= today)
                      orderby batch.ExpiresOn ?? DateOnly.MaxValue, batch.ReceivedOn
                      select new SellableBatchItem(
                          batch.ProductId,
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
