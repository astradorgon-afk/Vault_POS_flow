using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Application.Inventory;

/// <summary>
/// Persists stock adjustments and inventory counts, and reads the ledger facts
/// they need. Documents are loaded tracked so the unit of work saves changes.
/// </summary>
public interface IInventoryControlRepository
{
    /// <summary>Stages a new adjustment.</summary>
    /// <param name="adjustment">The adjustment.</param>
    void AddAdjustment(StockAdjustment adjustment);

    /// <summary>Loads an adjustment with its lines, tracked.</summary>
    /// <param name="id">The adjustment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The adjustment, or null.</returns>
    Task<StockAdjustment?> GetAdjustmentAsync(StockAdjustmentId id, CancellationToken cancellationToken);

    /// <summary>Stages a new count.</summary>
    /// <param name="count">The count.</param>
    void AddCount(InventoryCount count);

    /// <summary>Loads a count with its lines, tracked.</summary>
    /// <param name="id">The count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The count, or null.</returns>
    Task<InventoryCount?> GetCountAsync(InventoryCountId id, CancellationToken cancellationToken);

    /// <summary>Loads a location's kind and time zone.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The facts, or null when the location does not exist.</returns>
    Task<InventoryControlLocation?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken);

    /// <summary>Loads the EXT-WRITEOFF counterparty.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Its identifier, or null when not provisioned.</returns>
    Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken);

    /// <summary>Loads products, untracked.</summary>
    /// <param name="productIds">The products.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products that exist.</returns>
    Task<IReadOnlyList<Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>Loads every product, active or not, in the given categories.</summary>
    /// <param name="categoryIds">The categories.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products.</returns>
    Task<IReadOnlyList<Product>> GetProductsInCategoriesAsync(
        IReadOnlyCollection<CategoryId> categoryIds,
        CancellationToken cancellationToken);

    /// <summary>Loads batches, untracked.</summary>
    /// <param name="batchIds">The batches.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batches that exist.</returns>
    Task<IReadOnlyList<Batch>> GetBatchesAsync(IReadOnlyCollection<BatchId> batchIds, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the balance buckets at a location, in the given states, for the given
    /// products — or for every product when none are named.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="productIds">The products, or null for all.</param>
    /// <param name="states">The states.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The buckets that exist.</returns>
    Task<IReadOnlyList<BucketSnapshot>> GetBucketsAsync(
        LocationId locationId,
        IReadOnlyCollection<ProductId>? productIds,
        IReadOnlyCollection<InventoryState> states,
        CancellationToken cancellationToken);

    /// <summary>Reads the ledger legs posted under a document, for reversal.</summary>
    /// <param name="documentType">The document type.</param>
    /// <param name="documentId">The document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The legs, in posting order.</returns>
    Task<IReadOnlyList<PostedLeg>> GetPostedLegsAsync(
        ReferenceDocumentType documentType,
        Guid documentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds which of the given products showed a non-zero variance on another
    /// count posted at the location since a point in time.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="productIds">The products to check.</param>
    /// <param name="sinceUtc">The start of the look-back window.</param>
    /// <param name="excluding">The count being submitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products that varied before.</returns>
    Task<IReadOnlySet<ProductId>> GetProductsWithPriorVarianceAsync(
        LocationId locationId,
        IReadOnlyCollection<ProductId> productIds,
        DateTimeOffset sinceUtc,
        InventoryCountId excluding,
        CancellationToken cancellationToken);
}

/// <summary>A location's facts for posting.</summary>
/// <param name="Kind">The location kind.</param>
/// <param name="TimeZoneId">The IANA time zone, or null when not configured.</param>
public sealed record InventoryControlLocation(LocationKind Kind, string? TimeZoneId);

/// <summary>One balance bucket as it stands.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchKey">The batch key; empty for products not tracked by batch.</param>
/// <param name="State">The state.</param>
/// <param name="Quantity">The quantity.</param>
/// <param name="AverageUnitCost">The weighted average cost.</param>
public sealed record BucketSnapshot(
    ProductId ProductId,
    BatchId BatchKey,
    InventoryState State,
    decimal Quantity,
    decimal AverageUnitCost);

/// <summary>One posted ledger leg, as needed to reverse it.</summary>
/// <param name="MovementGroupId">The group the leg belongs to.</param>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch, if any.</param>
/// <param name="LocationId">The location.</param>
/// <param name="State">The state.</param>
/// <param name="QuantityDelta">The signed quantity.</param>
/// <param name="UnitCost">The unit cost it was posted at.</param>
public sealed record PostedLeg(
    MovementGroupId MovementGroupId,
    ProductId ProductId,
    BatchId? BatchId,
    LocationId LocationId,
    InventoryState State,
    decimal QuantityDelta,
    decimal UnitCost);
