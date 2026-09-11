using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Persistence;

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
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="policies">Per-location inventory policies.</param>
public sealed class InventoryLedger(
    PosDbContext context,
    ISystemClock clock,
    ILedgerPolicyProvider policies) : IInventoryLedger
{
    /// <inheritdoc />
    public async Task<Result<PostedMovementGroup>> PostAsync(
        MovementGroupSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // 1. Idempotency. A device that retried after a network timeout must get
        //    the original outcome, not a second posting.
        InventoryMovement? existing = await context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.EventId == spec.EventId)
            .OrderBy(m => m.LegNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            int legCount = await context.InventoryMovements
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

        // 4. Append the legs and apply them to the projection. Both happen in the
        //    caller's transaction, opened by the unit-of-work behaviour.
        foreach (InventoryMovement movement in group.Movements)
        {
            context.InventoryMovements.Add(movement);

            BucketKey key = BucketKey.From(movement);

            if (!buckets.TryGetValue(key, out InventoryBalance? balance))
            {
                balance = InventoryBalance.CreateEmpty(
                    movement.LocationId, movement.ProductId, movement.BatchKey, movement.State);

                context.InventoryBalances.Add(balance);
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

    /// <inheritdoc />
    public async Task<decimal> GetQuantityAsync(
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state,
        CancellationToken cancellationToken)
    {
        InventoryBalance? balance = await context.InventoryBalances
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

    private async Task<Dictionary<BucketKey, InventoryBalance>> LoadBucketsAsync(
        InventoryMovementGroup group,
        CancellationToken cancellationToken)
    {
        LocationId[] locations = [.. group.Movements.Select(m => m.LocationId).Distinct()];
        ProductId[] products = [.. group.Movements.Select(m => m.ProductId).Distinct()];

        List<InventoryBalance> loaded = await context.InventoryBalances
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
