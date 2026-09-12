using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>
/// Persists transfer orders. Mutations opt into tracking explicitly because the
/// context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
public interface ITransferRepository
{
    /// <summary>Stages a new transfer on the context.</summary>
    /// <param name="transfer">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier.</returns>
    Task<Result<TransferOrderId>> AddAsync(Transfer transfer, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a transfer with its lines, allocations, discrepancies and custody
    /// events, tracked.
    /// </summary>
    /// <param name="id">The transfer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transfer, or <see langword="null"/> when it does not exist.</returns>
    Task<Transfer?> GetByIdAsync(TransferOrderId id, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the transfer that owns an arrival discrepancy, with its lines,
    /// allocations, discrepancies and custody events, tracked.
    /// </summary>
    /// <param name="discrepancyId">The discrepancy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The owning transfer, or <see langword="null"/> when the discrepancy does not exist.</returns>
    Task<Transfer?> GetByDiscrepancyAsync(
        TransferDiscrepancyId discrepancyId,
        CancellationToken cancellationToken);

    /// <summary>Loads a location's kind and timezone for ledger posting and location validation.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The location info, or <see langword="null"/> when the location does not exist.</returns>
    Task<TransferLocationInfo?> GetLocationInfoAsync(
        LocationId locationId,
        CancellationToken cancellationToken);

    /// <summary>Loads the products named on a transfer, untracked.</summary>
    /// <param name="productIds">The product identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products.</returns>
    Task<IReadOnlyList<Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the available stock at a location for the named products: one item
    /// per available bucket, with the batch's expiry and snapshot cost so the
    /// picking handler can enforce first-expiry-first-out.
    /// </summary>
    /// <param name="locationId">The source location.</param>
    /// <param name="productIds">The products being picked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The available buckets, ordered by product.</returns>
    Task<IReadOnlyList<PickableStockItem>> GetPickableStockAsync(
        LocationId locationId,
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>Loads the batches named on pick allocations, so the handler can
    /// match them to products and tracking flags.</summary>
    /// <param name="batchIds">The batch identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batches.</returns>
    Task<IReadOnlyList<Batch>> GetBatchesAsync(
        IReadOnlyCollection<BatchId> batchIds,
        CancellationToken cancellationToken);

    /// <summary>Loads the EXT-WRITEOFF counterparty, used to park unrecoverable variance.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counterparty location identifier, or <see langword="null"/> when not provisioned.</returns>
    Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken);
}

/// <summary>What a transfer handler needs to know about one location.</summary>
/// <param name="Kind">The location kind, used to validate ledger states.</param>
/// <param name="TimeZoneId">The IANA timezone, for the business date.</param>
public sealed record TransferLocationInfo(
    LocationKind Kind,
    string TimeZoneId);

/// <summary>
/// One available bucket a picking handler may allocate from, with the facts it
/// needs to enforce first-expiry-first-out and snapshot costs.
/// </summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchKey">The batch bucket; empty for products that do not track batches.</param>
/// <param name="AvailableQuantity">The available quantity in the bucket.</param>
/// <param name="ExpiresOn">The batch expiry, where the product tracks expiry.</param>
/// <param name="UnitCost">The snapshot unit cost: the batch cost, or the product default cost.</param>
public sealed record PickableStockItem(
    ProductId ProductId,
    BatchId BatchKey,
    decimal AvailableQuantity,
    DateOnly? ExpiresOn,
    decimal UnitCost);