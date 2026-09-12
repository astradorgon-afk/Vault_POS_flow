using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// Everything the receiving handler needs to plan one goods receipt: the order
/// in its current state, the counterparty and destination facts required to
/// post the ledger group, the lots already on file, and how much has been
/// received per line so far (which the receipt aggregate turns into expected
/// quantities).
/// </summary>
/// <param name="Order">The tracked purchase order, with lines and approvals.</param>
/// <param name="ExternalSupplierLocationId">The EXT-SUPPLIER counterparty location, when provisioned.</param>
/// <param name="DestinationTimeZoneId">The receiving location's IANA timezone, for the business date.</param>
/// <param name="DestinationKind">The kind of the receiving location, for ledger validation.</param>
/// <param name="ExistingBatches">Batches already on file for the requested lots, keyed by product and lot.</param>
/// <param name="CumulativeReceivedByLine">Total received to date per purchase order line.</param>
public sealed record PurchaseReceivingContext(
    PurchaseOrder Order,
    LocationId? ExternalSupplierLocationId,
    string? DestinationTimeZoneId,
    LocationKind DestinationKind,
    IReadOnlyDictionary<(ProductId Product, string Lot), BatchId> ExistingBatches,
    IReadOnlyDictionary<PurchaseOrderLineId, decimal> CumulativeReceivedByLine);

/// <summary>
/// The entry point the application layer uses to persist and load purchase
/// orders. Implemented by the infrastructure layer, which owns persistence.
/// </summary>
public interface IPurchaseOrderRepository
{
    /// <summary>Persists a newly created draft purchase order.</summary>
    /// <param name="order">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier.</returns>
    Task<Result<PurchaseOrderId>> AddAsync(PurchaseOrder order, CancellationToken cancellationToken);

    /// <summary>Loads a purchase order with its lines and decisions, tracked.</summary>
    /// <param name="id">The order identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The order, or <see langword="null"/> when it does not exist.</returns>
    Task<PurchaseOrder?> GetByIdAsync(PurchaseOrderId id, CancellationToken cancellationToken);

    /// <summary>Determines whether a supplier exists and is active.</summary>
    /// <param name="supplierId">The supplier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the supplier can be ordered from.</returns>
    Task<bool> IsActiveSupplierAsync(SupplierId supplierId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the base unit of measure of an active product, or <see langword="null"/>
    /// when the product does not exist or is inactive.
    /// </summary>
    /// <param name="productId">The product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The product's base unit of measure.</returns>
    Task<UnitOfMeasureId?> GetActiveProductBaseUnitAsync(ProductId productId, CancellationToken cancellationToken);

    /// <summary>Determines whether a location exists and can physically receive goods.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when goods may be delivered there.</returns>
    Task<bool> CanReceiveGoodsAsync(LocationId locationId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads everything the receiving handler needs in one tracked trip: the
    /// order, its lines and approvals, the EXT-SUPPLIER counterparty location,
    /// the destination's timezone and kind, the batches already on file for the
    /// requested lots, and the cumulative received quantity per line.
    /// </summary>
    /// <remarks>
    /// The product of a lot is resolved from the order's lines here, because
    /// the receiving handler only knows the line identifiers until the order
    /// is loaded.
    /// </remarks>
    /// <param name="orderId">The order being received against.</param>
    /// <param name="requestedLots">The (line, lot) pairs the receipt wants to post.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The context, or <see langword="null"/> when the order does not exist.</returns>
    Task<PurchaseReceivingContext?> GetReceivingContextAsync(
        PurchaseOrderId orderId,
        IReadOnlyCollection<(PurchaseOrderLineId Line, string? Lot)> requestedLots,
        CancellationToken cancellationToken);

    /// <summary>Loads the ordered products, tracked, with their supplier links.</summary>
    /// <param name="productIds">The products of the order's lines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products, in no particular order.</returns>
    Task<IReadOnlyList<Product>> GetProductsForReceivingAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages a goods receipt and any newly created batches on the context. The
    /// unit-of-work behaviour owns durability at the end of the pipeline.
    /// </summary>
    /// <param name="receipt">The planned receipt, with its lines and discrepancies.</param>
    /// <param name="newBatches">The batches created for lots not previously received.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted receipt identifier.</returns>
    Task<Result<GoodsReceiptId>> AddReceiptAsync(
        GoodsReceipt receipt,
        IReadOnlyList<Batch> newBatches,
        CancellationToken cancellationToken);
}