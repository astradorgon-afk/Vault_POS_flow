using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// The result of an expiry run — the movement groups posted to quarantine
/// the expired batches.
/// </summary>
/// <param name="LocationId">The location that was processed.</param>
/// <param name="RunNumber">The EXP document number allocated for this run.</param>
/// <param name="ProcessedAtUtc">When the run completed.</param>
/// <param name="ExpiredBatchesCount">How many batch buckets were moved to Expired.</param>
/// <param name="TotalQuantity">The total quantity moved from Available to Expired.</param>
/// <param name="TotalValue">The total monetary value moved.</param>
/// <param name="MovementGroupIds">The identifiers of the posted movement groups.</param>
public sealed record ExpiryRunResult(
    LocationId LocationId,
    DocumentNumber RunNumber,
    DateTimeOffset ProcessedAtUtc,
    int ExpiredBatchesCount,
    decimal TotalQuantity,
    decimal TotalValue,
    IReadOnlyList<MovementGroupId> MovementGroupIds);

/// <summary>
/// One expired batch bucket that will be moved to the Expired state.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch identifier.</param>
/// <param name="Quantity">The available quantity to quarantine.</param>
/// <param name="UnitCost">The weighted average cost per unit.</param>
/// <param name="ExpiresOn">The expiry date.</param>
public sealed record ExpiredBatchItem(
    ProductId ProductId,
    BatchId BatchId,
    decimal Quantity,
    decimal UnitCost,
    DateOnly ExpiresOn);

/// <summary>
/// One batch whose expiry is approaching, for alert and sale-blocking purposes.
/// </summary>
/// <param name="LocationId">The location holding the stock.</param>
/// <param name="ProductId">The product.</param>
/// <param name="ProductName">The product name, for display.</param>
/// <param name="BatchId">The batch identifier.</param>
/// <param name="LotNumber">The supplier's lot number.</param>
/// <param name="Quantity">The available quantity.</param>
/// <param name="ExpiresOn">The expiry date.</param>
/// <param name="DaysUntilExpiry">How many days until expiry.</param>
public sealed record ExpiringBatchSummary(
    LocationId LocationId,
    ProductId ProductId,
    string ProductName,
    BatchId BatchId,
    string LotNumber,
    decimal Quantity,
    DateOnly ExpiresOn,
    int DaysUntilExpiry);
