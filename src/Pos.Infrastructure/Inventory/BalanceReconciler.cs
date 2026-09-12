using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Replays the ledger into a fresh balance projection and compares it with the
/// stored one; on rebuild, replaces the stored projection with the replay.
/// </summary>
/// <remarks>
/// <para>
/// Replay is deterministic: movements are applied in the order the server
/// recorded them, which is exactly the order the ledger itself applied them, so
/// a healthy projection reproduces the stored buckets bit for bit. On
/// PostgreSQL the delete guard trigger (<c>trg_inventory_balance_no_delete</c>)
/// is temporarily disabled only for the DELETE phase of a rebuild (the
/// maintenance role bypass), then re-enabled before the INSERTs; the deferred
/// balance guard stays armed throughout and validates every rebuilt row at
/// commit.
/// </para>
/// <para>
/// A rebuild is a maintenance operation. It is gated in the API by explicit
/// configuration, and the ledger — the source of truth — is never touched, so
/// a mid-rebuild fault leaves the ledger intact and the projection wrong the
/// same way it was before, recoverable by running the rebuild again.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="logger">Logger.</param>
public sealed class BalanceReconciler(PosDbContext context, ILogger<BalanceReconciler> logger)
    : IBalanceReconciler
{
    private const string BalanceDeleteGuardTrigger = "trg_inventory_balance_no_delete";

    /// <inheritdoc />
    public Task<Result<ReconciliationReport>> DetectAsync(CancellationToken cancellationToken)
        => RunAsync(rebuild: false, cancellationToken);

    /// <inheritdoc />
    public Task<Result<ReconciliationReport>> RebuildAsync(CancellationToken cancellationToken)
        => RunAsync(rebuild: true, cancellationToken);

    private async Task<Result<ReconciliationReport>> RunAsync(bool rebuild, CancellationToken cancellationToken)
    {
        // The ledger, in the order it was recorded: this defines the projection.
        List<InventoryMovement> movements = await context.InventoryMovements
            .AsNoTracking()
            .OrderBy(m => m.RecordedAtUtc)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BucketKey, InventoryBalance> expected = Replay(movements);

        Dictionary<BucketKey, InventoryBalance> stored = await context.InventoryBalances
            .AsNoTracking()
            .ToDictionaryAsync(b => BucketKey.From(b), cancellationToken)
            .ConfigureAwait(false);

        List<BalanceDiscrepancy> discrepancies = Compare(expected, stored);
        int drifted = discrepancies.Count;

        bool wasRebuilt = false;
        int rebuiltCount = 0;

        if (rebuild && drifted > 0)
        {
            wasRebuilt = true;
            rebuiltCount = await RebuildAsync(expected, cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                "Rebuilt {BucketCount} inventory balance buckets from the ledger after detecting {DriftCount} discrepancies.",
                rebuiltCount,
                drifted);

            // The report describes the state after the rebuild.
            discrepancies = [];
        }

        int bucketCount = expected.Count;
        int discrepancyCount = discrepancies.Count;

        ReconciliationReport report = new(
            IsHealthy: discrepancyCount == 0,
            WasRebuilt: wasRebuilt,
            BucketCount: bucketCount,
            RebuiltBucketCount: rebuiltCount,
            DriftedBucketCount: drifted,
            Discrepancies: discrepancies);

        if (logger.IsEnabled(LogLevel.Information) && report.IsHealthy)
        {
            logger.LogInformation(
                "Inventory reconciliation healthy: {BucketCount} buckets agree with the ledger.",
                bucketCount);
        }
        else if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "Inventory reconciliation found {DriftCount} drifted buckets: {DiscrepancyCount} discrepancies.",
                drifted,
                discrepancyCount);
        }

        return Result<ReconciliationReport>.Success(report);
    }

    /// <summary>
    /// Drops the stored projection and replaces it with the one the ledger
    /// implies, in a single transaction, and only on PostgreSQL temporarily
    /// disables the delete guard trigger the maintenance role is meant to
    /// bypass.  The guard is re-enabled before the INSERTs so that the
    /// deferred balance guard remains armed for the rebuilt rows, and the
    /// single-transaction DDL ensures a failure rolls everything back.
    /// </summary>
    /// <param name="expected">The projection to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many buckets were written.</returns>
    private async Task<int> RebuildAsync(
        IReadOnlyDictionary<BucketKey, InventoryBalance> expected,
        CancellationToken cancellationToken)
    {
        bool isPostgres = context.Database.IsNpgsql();

        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Forget anything this context may still be tracking (for example a
        // bucket loaded earlier in a long-lived scope): the projection rows are
        // replaced wholesale, and AddRange below must be the only instances
        // with those keys.
        context.ChangeTracker.Clear();

        if (isPostgres)
        {
            // Disable the delete guard for the DELETE portion only.  Re-enable
            // it BEFORE the INSERTs so that the deferred balance guard (which
            // fires on INSERT, not DELETE) has not yet queued any pending trigger
            // events when we re-arm the trigger — this avoids the Postgres 55006
            // error ("cannot ALTER TABLE … because it has pending trigger events")
            // that would occur if ENABLE ran after the INSERTs.
            await context.Database
                .ExecuteSqlRawAsync(
                    FormattableString.Invariant(
                        $"ALTER TABLE {PosDbContext.InventorySchema}.inventory_balance DISABLE TRIGGER {BalanceDeleteGuardTrigger}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await context.InventoryBalances
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (isPostgres)
            {
                // Re-enable the guard before the INSERTs: no DELETE-trigger
                // events are pending (the trigger was off during the DELETE), so
                // ENABLE succeeds immediately.  The deferred constraint trigger
                // has not yet fired because no INSERT has run, which means there
                // are no pending trigger events for it either.
                await context.Database
                    .ExecuteSqlRawAsync(
                        FormattableString.Invariant(
                            $"ALTER TABLE {PosDbContext.InventorySchema}.inventory_balance ENABLE TRIGGER {BalanceDeleteGuardTrigger}"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            context.InventoryBalances.AddRange(expected.Values);
            int written = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // The projection rows are all new buckets; the ledger always
            // implies at least one movement per bucket, so every inserted row
            // counts as one bucket.
            return written;
        }
        catch
        {
            // Disposal rolls the transaction back; on PostgreSQL the trigger
            // disable rolls back with it, so the guard is never left off.
            throw;
        }
    }

    private static Dictionary<BucketKey, InventoryBalance> Replay(IEnumerable<InventoryMovement> movements)
    {
        Dictionary<BucketKey, InventoryBalance> buckets = [];

        foreach (InventoryMovement movement in movements)
        {
            BucketKey key = BucketKey.From(movement);

            if (!buckets.TryGetValue(key, out InventoryBalance? balance))
            {
                balance = InventoryBalance.CreateEmpty(
                    movement.LocationId, movement.ProductId, movement.BatchKey, movement.State);

                buckets[key] = balance;
            }

            balance.Apply(movement);
        }

        return buckets;
    }

    private static List<BalanceDiscrepancy> Compare(
        IReadOnlyDictionary<BucketKey, InventoryBalance> expected,
        IReadOnlyDictionary<BucketKey, InventoryBalance> stored)
    {
        List<BalanceDiscrepancy> discrepancies = [];
        static BucketReference Ref(BucketKey key)
            => new(key.LocationId.Value, key.ProductId.Value, key.BatchKey.Value, key.State);

        foreach ((BucketKey key, InventoryBalance want) in expected)
        {
            if (!stored.TryGetValue(key, out InventoryBalance? have))
            {
                discrepancies.Add(new BalanceDiscrepancy(
                    Ref(key),
                    BalanceDiscrepancyKind.Missing,
                    want.Quantity,
                    0m,
                    want.LastMovementId.Value,
                    null));
                continue;
            }

            if (have.Quantity != want.Quantity)
            {
                discrepancies.Add(new BalanceDiscrepancy(
                    Ref(key),
                    BalanceDiscrepancyKind.Quantity,
                    want.Quantity,
                    have.Quantity,
                    want.LastMovementId.Value,
                    have.LastMovementId.Value));
            }

            if (have.AverageUnitCost != want.AverageUnitCost)
            {
                discrepancies.Add(new BalanceDiscrepancy(
                    Ref(key),
                    BalanceDiscrepancyKind.AverageUnitCost,
                    want.AverageUnitCost,
                    have.AverageUnitCost,
                    want.LastMovementId.Value,
                    have.LastMovementId.Value));
            }

            if (have.TotalValue != want.TotalValue)
            {
                discrepancies.Add(new BalanceDiscrepancy(
                    Ref(key),
                    BalanceDiscrepancyKind.TotalValue,
                    want.TotalValue,
                    have.TotalValue,
                    want.LastMovementId.Value,
                    have.LastMovementId.Value));
            }

            if (have.LastMovementId != want.LastMovementId)
            {
                discrepancies.Add(new BalanceDiscrepancy(
                    Ref(key),
                    BalanceDiscrepancyKind.LastMovement,
                    0m,
                    0m,
                    want.LastMovementId.Value,
                    have.LastMovementId.Value));
            }
        }

        foreach ((BucketKey key, InventoryBalance have) in stored)
        {
            if (!expected.ContainsKey(key))
            {
                discrepancies.Add(new BalanceDiscrepancy(
                    Ref(key),
                    BalanceDiscrepancyKind.Unexpected,
                    0m,
                    have.Quantity,
                    null,
                    have.LastMovementId.Value));
            }
        }

        return discrepancies;
    }

    private readonly record struct BucketKey(
        LocationId LocationId,
        ProductId ProductId,
        BatchId BatchKey,
        InventoryState State)
    {
        public static BucketKey From(InventoryBalance balance)
            => new(balance.LocationId, balance.ProductId, balance.BatchKey, balance.State);

        public static BucketKey From(InventoryMovement movement)
            => new(movement.LocationId, movement.ProductId, movement.BatchKey, movement.State);
    }
}