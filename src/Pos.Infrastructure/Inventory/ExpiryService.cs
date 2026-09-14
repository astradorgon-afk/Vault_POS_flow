using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Reads expired and expiring batches from the balance projection joined to
/// the batch master, applying the location's configured warning threshold.
/// </summary>
/// <remarks>
/// The balance projection carries quantity by (location, product, batch key,
/// state); the expiry date lives on the batch row. Only buckets in the
/// Available state that have a non-empty batch key and a recorded expiry date
/// participate — a product that does not track expiry has no bucket to guard.
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="repository">Repository for location-specific expiry facts.</param>
/// <param name="ledger">The inventory ledger for posting expiry runs.</param>
/// <param name="numbers">Document number generator for EXP runs.</param>
/// <param name="clock">System clock.</param>
public sealed class ExpiryService(
    PosDbContext context,
    IExpiryRepository repository,
    IInventoryLedger ledger,
    IDocumentNumberGenerator numbers,
    ISystemClock clock) : IExpiryService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredBatchItem>> GetExpiredBatchesAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        List<ExpiredBatchItem> items = [];

        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId
                && b.State == InventoryState.Available
                && !b.BatchKey.IsEmpty
                && b.Quantity > 0m)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (balances.Count == 0)
        {
            return items;
        }

        IReadOnlyList<Batch> batches = await LoadBatchesAsync(
            [.. balances.Select(b => b.BatchKey).Distinct()],
            cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BatchId, Batch> batchById = batches.ToDictionary(b => b.Id);

        foreach (InventoryBalance balance in balances)
        {
            if (!batchById.TryGetValue(balance.BatchKey, out Batch? batch)
                || batch.ExpiresOn is not { } expiresOn
                || expiresOn >= today)
            {
                continue;
            }

            items.Add(new ExpiredBatchItem(
                balance.ProductId,
                balance.BatchKey,
                balance.Quantity,
                balance.AverageUnitCost,
                expiresOn));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiringBatchSummary>> GetExpiringBatchesAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        int warningDays = await repository
            .GetExpiryWarningDaysAsync(locationId, cancellationToken)
            .ConfigureAwait(false);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly horizon = today.AddDays(warningDays);

        List<ExpiringBatchSummary> items = [];

        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId
                && b.State == InventoryState.Available
                && !b.BatchKey.IsEmpty
                && b.Quantity > 0m)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (balances.Count == 0)
        {
            return items;
        }

        IReadOnlyList<Batch> batches = await LoadBatchesAsync(
            [.. balances.Select(b => b.BatchKey).Distinct()],
            cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BatchId, Batch> batchById = batches.ToDictionary(b => b.Id);

        Dictionary<ProductId, Product> products = (await LoadProductsAsync(
            [.. balances.Select(b => b.ProductId).Distinct()],
            cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        foreach (InventoryBalance balance in balances)
        {
            if (!batchById.TryGetValue(balance.BatchKey, out Batch? batch)
                || batch.ExpiresOn is not { } expiresOn
                || expiresOn < today
                || expiresOn > horizon)
            {
                continue;
            }

            products.TryGetValue(balance.ProductId, out Product? product);

            items.Add(new ExpiringBatchSummary(
                locationId,
                balance.ProductId,
                product?.Name ?? string.Empty,
                balance.BatchKey,
                batch.LotNumber,
                balance.Quantity,
                expiresOn,
                expiresOn.DayNumber - today.DayNumber));
        }

        return [.. items.OrderBy(i => i.ExpiresOn).ThenBy(i => i.BatchId.Value)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SellableBatchItem>> GetSellableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

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

        List<Batch> batches = await LoadBatchesAsync(
            [.. balances.Where(b => !b.BatchKey.IsEmpty).Select(b => b.BatchKey).Distinct()],
            cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BatchId, Batch> batchById = batches.ToDictionary(b => b.Id);

        List<Product> products = await LoadProductsAsync([productId], cancellationToken)
            .ConfigureAwait(false);

        bool tracksBatches = products.Count == 0 ? false : products[0].TracksBatches;

        List<SellableBatchItem> items = [];

        foreach (InventoryBalance balance in balances)
        {
            if (balance.BatchKey.IsEmpty)
            {
                // No batch: sellable when the product is not batch-tracked. A
                // product that is batch-tracked always carries a batch bucket.
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

            if (!batchById.TryGetValue(balance.BatchKey, out Batch? batch))
            {
                continue;
            }

            // Sale blocking: past-expiry batches are excluded. The authorized
            // exception path is a separate, permissioned sale command decision.
            if (batch.ExpiresOn is { } expiresOn && expiresOn < today)
            {
                continue;
            }

            items.Add(new SellableBatchItem(
                balance.ProductId,
                balance.BatchKey,
                batch.LotNumber,
                balance.Quantity,
                batch.ExpiresOn,
                balance.AverageUnitCost));
        }

        return [.. items
            .OrderBy(i => i.ExpiresOn ?? DateOnly.MaxValue)
            .ThenBy(i => i.BatchId.Value)];
    }

    /// <inheritdoc />
    public async Task<Result<ExpiryRunResult>> PostExpiryRunAsync(
        LocationId locationId,
        LedgerActor actor,
        CancellationToken cancellationToken)
    {
        ExpiryLocationInfo? location = await repository
            .GetLocationAsync(locationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null || location.Kind == LocationKind.External)
        {
            return Result<ExpiryRunResult>.Failure(ExpiryErrors.LocationInvalid(locationId));
        }

        if (string.IsNullOrWhiteSpace(location.TimeZoneId))
        {
            return Result<ExpiryRunResult>.Failure(ExpiryErrors.LocationTimeZoneMissing(locationId));
        }

        IReadOnlyList<ExpiredBatchItem> expired = await GetExpiredBatchesAsync(locationId, cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return Result<ExpiryRunResult>.Failure(ExpiryErrors.NoExpiredBatches);
        }

        DocumentNumber runNumber = await numbers
            .NextAsync(DocumentType.ExpiryRun, cancellationToken)
            .ConfigureAwait(false);

        ExpiryRunRecordId runId = ExpiryRunRecordId.New();
        DateTimeOffset now = clock.UtcNow;
        List<MovementGroupId> movementGroupIds = [];
        decimal totalQuantity = 0m;
        decimal totalValue = 0m;

        foreach (ExpiredBatchItem item in expired)
        {
            MovementGroupSpec spec = new(
                EventId: EventId.New(),
                MovementType: InventoryMovementType.ExpiryQuarantine,
                ReferenceDocumentType: ReferenceDocumentType.ExpiryRun,
                ReferenceDocumentId: runId.Value,
                ReferenceNumber: runNumber.Value,
                Legs:
                [
                    new MovementLegSpec(
                        item.ProductId,
                        item.BatchId,
                        locationId,
                        location.Kind,
                        InventoryState.Available,
                        -item.Quantity,
                        item.UnitCost,
                        ProductTracksBatches: true),
                    new MovementLegSpec(
                        item.ProductId,
                        item.BatchId,
                        locationId,
                        location.Kind,
                        InventoryState.Expired,
                        item.Quantity,
                        item.UnitCost,
                        ProductTracksBatches: true),
                ],
                Actor: actor,
                OccurredAtUtc: now,
                BusinessDate: clock.BusinessDateFor(location.TimeZoneId),
                ReasonCode: AdjustmentReasonCode.Expired,
                Notes: "Expiry quarantine run.");

            Result<PostedMovementGroup> posted = await ledger
                .PostAsync(spec, cancellationToken)
                .ConfigureAwait(false);

            if (posted.IsFailure)
            {
                return Result<ExpiryRunResult>.Failure(
                    ExpiryErrors.LedgerPostFailed(posted.Error.Code, posted.Error.Message));
            }

            movementGroupIds.Add(posted.Value.MovementGroupId);
            totalQuantity += item.Quantity;
            totalValue += decimal.Round(item.Quantity * item.UnitCost, Money.StorageScale, Money.IntermediateRounding);
        }

        return Result<ExpiryRunResult>.Success(new ExpiryRunResult(
            locationId,
            runNumber,
            now,
            expired.Count,
            totalQuantity,
            totalValue,
            movementGroupIds));
    }

    private Task<List<Batch>> LoadBatchesAsync(
        IReadOnlyCollection<BatchId> batchIds,
        CancellationToken cancellationToken)
        => batchIds.Count == 0
            ? Task.FromResult(new List<Batch>())
            : context.Batches
                .AsNoTracking()
                .Where(b => batchIds.Contains(b.Id))
                .ToListAsync(cancellationToken);

    private Task<List<Product>> LoadProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
        => productIds.Count == 0
            ? Task.FromResult(new List<Product>())
            : context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToListAsync(cancellationToken);
}