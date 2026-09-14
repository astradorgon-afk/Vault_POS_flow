using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Inventory;

/// <summary>
/// Queries the inventory for expired and expiring batches, and provides the
/// FEFO allocation service used by both transfer picking and POS sale blocking.
/// </summary>
public interface IExpiryService
{
    /// <summary>
    /// Finds all available-stock buckets at a location whose batches are past
    /// their expiry date, grouped by product and batch for posting.
    /// </summary>
    /// <param name="locationId">The location to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The expired items, or an empty list when nothing is past expiry.</returns>
    Task<IReadOnlyList<ExpiredBatchItem>> GetExpiredBatchesAsync(
        LocationId locationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds batches approaching their expiry date, filtered by the location's
    /// configured warning threshold. Used for alerts and the sale-blocking query.
    /// </summary>
    /// <param name="locationId">The location to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batches within the warning window, ordered by days-until-expiry ascending.</returns>
    Task<IReadOnlyList<ExpiringBatchSummary>> GetExpiringBatchesAsync(
        LocationId locationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the sellable batches for a product at a location — available stock
    /// whose batches are not past expiry. Expired overrides are handled at the
    /// sale command level, not here.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="productId">The product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batches ordered by expiry (FEFO), then by batch key.</returns>
    Task<IReadOnlyList<SellableBatchItem>> GetSellableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds all available stock whose batches are past expiry at a location,
    /// allocates an EXP document number, and posts ExpiryQuarantine movements to
    /// shift each batch into the Expired state.
    /// </summary>
    /// <param name="locationId">The location to process.</param>
    /// <param name="actor">Who is performing the run (system or user).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the expiry run, or a failure if posting failed.</returns>
    Task<Result<ExpiryRunResult>> PostExpiryRunAsync(
        LocationId locationId,
        LedgerActor actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists expiry run records and reads the inventory facts the expiry
/// system needs.
/// </summary>
public interface IExpiryRepository
{
    /// <summary>Gets the EXT-WRITEOFF counterparty location.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The location identifier, or null when not provisioned.</returns>
    Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken);

    /// <summary>Loads a location's kind and time zone.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The facts, or null when the location does not exist.</returns>
    Task<ExpiryLocationInfo?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken);

    /// <summary>Loads every active stocking location.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The locations.</returns>
    Task<IReadOnlyList<ExpiryLocationInfo>> GetAllStockingLocationsAsync(CancellationToken cancellationToken);

    /// <summary>Loads the expiry warning threshold for a location.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The warning threshold in days, or the default (90) if not configured.</returns>
    Task<int> GetExpiryWarningDaysAsync(LocationId locationId, CancellationToken cancellationToken);
}

/// <summary>A location's facts for expiry processing.</summary>
/// <param name="LocationId">The location identifier.</param>
/// <param name="Kind">The location kind.</param>
/// <param name="TimeZoneId">The IANA time zone, or null when not configured.</param>
/// <param name="ExpiryWarningDays">The configured warning threshold.</param>
public sealed record ExpiryLocationInfo(
    LocationId LocationId,
    Domain.Locations.LocationKind Kind,
    string? TimeZoneId,
    int ExpiryWarningDays);

/// <summary>
/// One sellable batch for a product at a location, ordered by FEFO.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch key, or the empty identifier.</param>
/// <param name="LotNumber">The supplier's lot number, if batch-tracked.</param>
/// <param name="Quantity">The available quantity.</param>
/// <param name="ExpiresOn">The expiry date, or null.</param>
/// <param name="UnitCost">The weighted average cost per unit.</param>
public sealed record SellableBatchItem(
    ProductId ProductId,
    BatchId BatchId,
    string LotNumber,
    decimal Quantity,
    DateOnly? ExpiresOn,
    decimal UnitCost);
