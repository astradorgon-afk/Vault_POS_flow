using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Supplies the per-location inventory policies the ledger must honour.
/// </summary>
/// <remarks>
/// Separated from the ledger so that location settings (Phase 3) can supply
/// real values without the ledger taking a dependency on the location
/// aggregate. The default implementation returns the strictest policy, which is
/// the correct behaviour for any location whose settings have not been loaded.
/// </remarks>
public interface ILedgerPolicyProvider
{
    /// <summary>Gets the negative-stock policy in force at a location.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The policy.</returns>
    Task<NegativeStockPolicy> GetNegativeStockPolicyAsync(
        LocationId locationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The conservative default policy provider: negative stock is prohibited
/// everywhere until a location explicitly configures otherwise.
/// </summary>
public sealed class StrictLedgerPolicyProvider : ILedgerPolicyProvider
{
    /// <inheritdoc />
    public Task<NegativeStockPolicy> GetNegativeStockPolicyAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
        => Task.FromResult(NegativeStockPolicy.Prohibit);
}

/// <summary>
/// The two tables the ledger writes, on whichever database it is running
/// against.
/// </summary>
/// <remarks>
/// The ledger is the one piece of this system that must behave identically on a
/// server and on a device — a sale rung up offline and the same sale rung up
/// online are the same business event, and a second implementation would drift
/// invisibly until inventory disagreed (ADR-0008). So there is one ledger, and
/// this is what it runs against: PostgreSQL through <c>PosDbContext</c>, the
/// device's encrypted SQLite file through <c>PosDeviceDbContext</c>.
/// </remarks>
public interface ILedgerStore
{
    /// <summary>Gets the append-only movement legs.</summary>
    DbSet<InventoryMovement> InventoryMovements { get; }

    /// <summary>Gets the balance projection the legs are applied to.</summary>
    DbSet<InventoryBalance> InventoryBalances { get; }

    /// <summary>Gets this store as its context, for transactions and saves.</summary>
    /// <returns>The underlying context.</returns>
    DbContext AsDbContext();

    /// <summary>
    /// Opens the window in which this store accepts ledger writes, until the
    /// handle is disposed.
    /// </summary>
    /// <returns>The handle that closes the window.</returns>
    /// <remarks>
    /// PostgreSQL does not need this: its balance guard is a deferred constraint
    /// trigger that checks the arithmetic at commit, when every row is in place,
    /// and a least-privilege role stops anything else writing. SQLite has neither
    /// deferred triggers nor roles, so the device's triggers instead ask who is
    /// writing — the same mechanism that protects its downloaded caches — and
    /// this is how the ledger identifies itself. The server's store returns a
    /// handle that does nothing.
    /// </remarks>
    IDisposable BeginLedgerWrite();
}

/// <summary>
/// The only component in the system that writes stock.
/// </summary>
/// <remarks>
/// <para>
/// A post appends immutable legs and applies them to the balance projection
/// inside one transaction. If any part fails, none of it happened.
/// </para>
/// <para>
/// Availability and policy are checked here rather than in the domain because
/// they need the current balances; everything decidable from the event alone is
/// already enforced by <see cref="InventoryMovementGroup.Create"/>.
/// </para>
/// <para>
/// Optimistic concurrency: the balance projection carries a version that is
/// bumped on every apply, so a writer that read a bucket before a concurrent
/// writer committed is refused instead of silently overwriting the newer
/// projection. <see cref="PostAsync"/> retries the whole posting against the
/// projection as it now stands; only when the retries themselves are exhausted
/// does the caller see <see cref="InventoryErrors.BalanceContention"/>.
/// </para>
/// </remarks>
/// <param name="store">The ledger tables, on whichever database this is.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="policies">Per-location inventory policies.</param>
/// <param name="logger">Logger, supplied by the container; tests may pass none.</param>
/// <param name="attempts">
/// Collects refused draws for the pipeline to record after the transaction ends;
/// supplied by the container, tests may pass none.
/// </param>
public sealed class InventoryLedger(
    ILedgerStore store,
    ISystemClock clock,
    ILedgerPolicyProvider policies,
    ILogger<InventoryLedger>? logger = null,
    INegativeStockAttemptRecorder? attempts = null) : IInventoryLedger
{
    // With N concurrent writers each reading the same empty bucket and racing to
    // insert, the worst case needs N attempts: every other writer may commit once
    // between this writer's stage-read and save.  The sync processor has at most
    // one writer thread per node; a manual API post can overlap too.  Ten is well
    // above any realistic fan-in (two sync nodes + a handful of API posts)
    // without adding measurable cost, because on each iteration the hot-path
    // SELECT is served from shared buffers and the write is a single-row UPDATE.
    private const int MaxStandaloneAttempts = 10;

    /// <inheritdoc />
    public async Task<Result<PostedMovementGroup>> PostAsync(
        MovementGroupSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (store.AsDbContext().Database.CurrentTransaction is not null)
        {
            // Ambient transaction (unit-of-work behaviour, synchronization
            // processor): stage once, and let the caller own durability,
            // rollback and the contention mapping. Retrying here would fight
            // the caller's transaction, which must be rolled back as a whole.
            return await StageAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        // Standalone posting (unit tests, migration utilities): the ledger owns
        // the transaction and retries on optimistic-concurrency contention,
        // because a concurrent writer may have committed a newer projection
        // between this read and this write.
        for (int attempt = 1; ; attempt++)
        {
            await using IDbContextTransaction transaction =
                await store.AsDbContext().Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            Result<PostedMovementGroup> staged = await StageAsync(spec, cancellationToken).ConfigureAwait(false);

            if (staged.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return staged;
            }

            try
            {
                using (store.BeginLedgerWrite())
                {
                    await store.AsDbContext().SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return staged;
            }
            catch (DbUpdateException ex) when (attempt < MaxStandaloneAttempts && IsBalanceCompetition(ex))
            {
                // Another writer got there first: a stale-version update was
                // refused, or a bucket both writers had seen as missing collided
                // on the primary key. Forget everything this context read and try
                // again against the projection as it now stands.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                store.AsDbContext().ChangeTracker.Clear();

                logger?.LogWarning(
                    "Ledger posting for event {EventId} contended with a concurrent writer; retrying (attempt {Attempt} of {Max}).",
                    spec.EventId,
                    attempt,
                    MaxStandaloneAttempts);
            }
            catch (DbUpdateException ex) when (attempt >= MaxStandaloneAttempts && IsBalanceCompetition(ex))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                store.AsDbContext().ChangeTracker.Clear();

                logger?.LogError(
                    "Ledger posting for event {EventId} exhausted {Max} attempts under balance contention.",
                    spec.EventId,
                    MaxStandaloneAttempts);

                return Result<PostedMovementGroup>.Failure(InventoryErrors.BalanceContention);
            }
        }
    }

    /// <summary>
    /// Tells a balance-versus-writer competition apart from an ordinary save
    /// failure. A stale-version update is the concurrency exception proper; two
    /// writers that both saw the bucket missing race to insert the same row, and
    /// the loser collides on the primary key instead. Both mean the same thing:
    /// retry against the projection as it now stands.
    /// </summary>
    /// <param name="ex">The save failure.</param>
    /// <returns>True when the failure is balance contention.</returns>
    private static bool IsBalanceCompetition(DbUpdateException ex)
    {
        if (ex is DbUpdateConcurrencyException)
        {
            return true;
        }

        return ex.Entries.Any(e => e.Entity is InventoryBalance)
            && ex.InnerException is Exception inner
            && inner switch
            {
                SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT: the balance table has no other constraints, so this is the primary key
                PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
                _ => false,
            };
    }

    /// <inheritdoc />
    public async Task<decimal> GetQuantityAsync(
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state,
        CancellationToken cancellationToken)
    {
        InventoryBalance? balance = await store.InventoryBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.LocationId == locationId
                     && b.ProductId == productId
                     && b.BatchKey == batchKey
                     && b.State == state,
                cancellationToken)
            .ConfigureAwait(false);

        return balance?.Quantity ?? 0m;
    }

    /// <summary>
    /// Validates the event, checks availability and stages the movement legs and
    /// the balance updates on the context. Persistence is deliberately left to
    /// the caller: see <see cref="PostAsync"/> for the transaction story.
    /// </summary>
    /// <param name="spec">The intended event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The posted group, or the reason it was refused.</returns>
    private async Task<Result<PostedMovementGroup>> StageAsync(
        MovementGroupSpec spec,
        CancellationToken cancellationToken)
    {
        // 1. Idempotency. A device that retried after a network timeout must get
        //    the original outcome, not a second posting.
        InventoryMovement? existing = await store.InventoryMovements
            .AsNoTracking()
            .Where(m => m.EventId == spec.EventId)
            .OrderBy(m => m.LegNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            int legCount = await store.InventoryMovements
                .AsNoTracking()
                .CountAsync(m => m.MovementGroupId == existing.MovementGroupId, cancellationToken)
                .ConfigureAwait(false);

            return Result<PostedMovementGroup>.Success(new PostedMovementGroup(
                existing.MovementGroupId,
                existing.EventId,
                legCount,
                WasDuplicate: true,
                existing.RecordedAtUtc));
        }

        // 2. Structural and rule validation, in the domain.
        DateTimeOffset recordedAt = clock.UtcNow;
        Result<InventoryMovementGroup> built = InventoryMovementGroup.Create(spec, recordedAt);

        if (built.IsFailure)
        {
            return Result<PostedMovementGroup>.Failure(built.Errors);
        }

        InventoryMovementGroup group = built.Value;

        // 3. Availability, which needs the current balances.
        Dictionary<BucketKey, InventoryBalance> buckets =
            await LoadBucketsAsync(group, cancellationToken).ConfigureAwait(false);

        Result availability = await CheckAvailabilityAsync(group, buckets, cancellationToken).ConfigureAwait(false);

        if (availability.IsFailure)
        {
            return Result<PostedMovementGroup>.Failure(availability.Errors);
        }

        // 4. Append the legs and apply them to the projection. Both are staged
        //    for the caller's save, and the applied buckets carry the version
        //    they were read with so a concurrent writer cannot be overwritten.
        foreach (InventoryMovement movement in group.Movements)
        {
            store.InventoryMovements.Add(movement);

            BucketKey key = BucketKey.From(movement);

            if (!buckets.TryGetValue(key, out InventoryBalance? balance))
            {
                balance = InventoryBalance.CreateEmpty(
                    movement.LocationId, movement.ProductId, movement.BatchKey, movement.State);

                store.InventoryBalances.Add(balance);
                buckets[key] = balance;
            }

            balance.Apply(movement);
        }

        return Result<PostedMovementGroup>.Success(new PostedMovementGroup(
            group.Id,
            spec.EventId,
            group.Movements.Count,
            WasDuplicate: false,
            recordedAt));
    }

    private async Task<Dictionary<BucketKey, InventoryBalance>> LoadBucketsAsync(
        InventoryMovementGroup group,
        CancellationToken cancellationToken)
    {
        LocationId[] locations = [.. group.Movements.Select(m => m.LocationId).Distinct()];
        ProductId[] products = [.. group.Movements.Select(m => m.ProductId).Distinct()];

        List<InventoryBalance> loaded = await store.InventoryBalances
            .AsTracking()
            .Where(b => locations.Contains(b.LocationId) && products.Contains(b.ProductId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<BucketKey, InventoryBalance> buckets = [];

        foreach (InventoryBalance balance in loaded)
        {
            buckets[new BucketKey(balance.LocationId, balance.ProductId, balance.BatchKey, balance.State)] = balance;
        }

        return buckets;
    }

    private async Task<Result> CheckAvailabilityAsync(
        InventoryMovementGroup group,
        IReadOnlyDictionary<BucketKey, InventoryBalance> buckets,
        CancellationToken cancellationToken)
    {
        List<Error> errors = [];

        // Aggregate by bucket first: a group may take from the same bucket twice,
        // and each draw on its own could look affordable while the total is not.
        IEnumerable<IGrouping<BucketKey, InventoryMovement>> draws = group.Movements
            .Where(m => !m.IsIncrease && m.State != InventoryState.External)
            .GroupBy(BucketKey.From);

        foreach (IGrouping<BucketKey, InventoryMovement> draw in draws)
        {
            decimal requested = Math.Abs(draw.Sum(m => m.QuantityDelta));
            decimal available = buckets.TryGetValue(draw.Key, out InventoryBalance? balance) ? balance.Quantity : 0m;

            if (available >= requested)
            {
                continue;
            }

            NegativeStockPolicy policy = await policies
                .GetNegativeStockPolicyAsync(draw.Key.LocationId, cancellationToken)
                .ConfigureAwait(false);

            bool permitted = policy switch
            {
                NegativeStockPolicy.Prohibit => false,

                // The permission itself is checked by the authorization pipeline
                // before the command reaches the ledger; reaching here under this
                // policy means the caller was allowed to oversell.
                NegativeStockPolicy.AllowWithPermission => true,

                // Only a device-originated sale may run negative, and only when
                // the event is already marked for central review.
                NegativeStockPolicy.AllowOfflineWithReview =>
                    group.Spec.ServerProcessingStatus == Domain.Sync.ServerProcessingStatus.RequiresReview
                    && group.Spec.MovementType == InventoryMovementType.PosSale,

                _ => false,
            };

            if (!permitted)
            {
                errors.Add(InventoryErrors.InsufficientStock(
                    draw.Key.ProductId, draw.Key.LocationId, available, requested));

                attempts?.Record(NegativeStockAttempt.Record(
                    group.Spec,
                    draw.Key.LocationId,
                    draw.Key.ProductId,
                    draw.Key.BatchKey,
                    draw.Key.State,
                    requested,
                    available,
                    policy,
                    clock.UtcNow));
            }
        }

        return errors.Count > 0 ? Result.Failure(errors) : Result.Success();
    }

    private readonly record struct BucketKey(
        LocationId LocationId,
        ProductId ProductId,
        BatchId BatchKey,
        InventoryState State)
    {
        public static BucketKey From(InventoryMovement movement)
            => new(movement.LocationId, movement.ProductId, movement.BatchKey, movement.State);
    }
}