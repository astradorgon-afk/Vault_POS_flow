using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Compares the inventory balance projection with the ledger, and can rebuild
/// the projection from the ledger when they have drifted.
/// </summary>
/// <remarks>
/// The projection is not a source of truth: it exists so the POS can answer
/// quantity questions without summing the ledger. The ledger is authoritative,
/// so a discrepancy means the projection is wrong and the ledger is right.
/// </remarks>
public interface IBalanceReconciler
{
    /// <summary>
    /// Replays the ledger into a fresh projection and compares it with the
    /// stored one. Read-only: nothing is written.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A report of any drift found.</returns>
    Task<Result<ReconciliationReport>> DetectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rebuilds the stored projection to match the ledger when they have
    /// drifted. When no drift exists, nothing is written.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A report of what was found and rebuilt.</returns>
    Task<Result<ReconciliationReport>> RebuildAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of a reconciliation pass.</summary>
/// <param name="IsHealthy">True when the stored projection matches the ledger.</param>
/// <param name="WasRebuilt">True when the projection was rebuilt during this pass.</param>
/// <param name="BucketCount">How many buckets the ledger implies.</param>
/// <param name="RebuiltBucketCount">How many buckets were rewritten on a rebuild.</param>
/// <param name="DriftedBucketCount">How many buckets disagreed with the ledger before any rebuild.</param>
/// <param name="Discrepancies">The drift found, before any rebuild. Empty means no drift.</param>
public sealed record ReconciliationReport(
    bool IsHealthy,
    bool WasRebuilt,
    int BucketCount,
    int RebuiltBucketCount,
    int DriftedBucketCount,
    IReadOnlyList<BalanceDiscrepancy> Discrepancies);

/// <summary>One bucket whose stored projection disagrees with the ledger.</summary>
/// <param name="Bucket">The bucket.</param>
/// <param name="Kind">How it disagrees.</param>
/// <param name="Expected">The ledger-backed value: quantity, unit cost or value, per <paramref name="Kind"/>.</param>
/// <param name="Actual">The stored value. Zero for a missing bucket.</param>
/// <param name="ExpectedLastMovementId">The last movement the ledger implies, when known.</param>
/// <param name="ActualLastMovementId">The last movement recorded on the stored row, when it has one.</param>
public sealed record BalanceDiscrepancy(
    BucketReference Bucket,
    BalanceDiscrepancyKind Kind,
    decimal Expected,
    decimal Actual,
    Guid? ExpectedLastMovementId,
    Guid? ActualLastMovementId);

/// <summary>Identifies one inventory bucket for reporting.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="ProductId">The product.</param>
/// <param name="BatchKey">The batch key, or the empty identifier.</param>
/// <param name="State">The inventory state.</param>
public sealed record BucketReference(
    Guid LocationId,
    Guid ProductId,
    Guid BatchKey,
    InventoryState State);

/// <summary>How a stored bucket disagrees with the ledger.</summary>
public enum BalanceDiscrepancyKind
{
    /// <summary>The ledger has movements for the bucket but no row exists.</summary>
    Missing = 0,

    /// <summary>The stored quantity differs from the ledger.</summary>
    Quantity = 1,

    /// <summary>The stored weighted average unit cost differs from the ledger.</summary>
    AverageUnitCost = 2,

    /// <summary>The stored total value differs from the ledger.</summary>
    TotalValue = 3,

    /// <summary>The stored last-movement identifier differs from the ledger.</summary>
    LastMovement = 4,

    /// <summary>A row exists for a bucket the ledger has no movements for.</summary>
    Unexpected = 5,
}