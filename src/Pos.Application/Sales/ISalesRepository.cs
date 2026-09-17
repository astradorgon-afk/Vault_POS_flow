using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Reads the facts a completed sale is re-derived from, and persists the sale.
/// The repository never performs domain validation — it loads, saves and returns
/// the aggregate exactly as the domain produced it.
/// </summary>
public interface ISalesRepository
{
    /// <summary>
    /// Persists a completed sale with its lines and payments.
    /// </summary>
    /// <param name="sale">The sale to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sale identifier.</returns>
    Task<Result<SaleId>> AddAsync(Sale sale, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a sale with its lines and payments, for review before an update
    /// such as a void.
    /// </summary>
    /// <param name="saleId">The sale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sale, or <see langword="null"/> when it does not exist.</returns>
    Task<Sale?> GetByIdAsync(SaleId saleId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a changed sale. A returned-quantity accumulation updates a line
    /// and a void updates the scalar state; nothing else changes once a sale is
    /// completed.
    /// </summary>
    /// <param name="sale">The sale to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sale identifier.</returns>
    Task<Result<SaleId>> UpdateAsync(Sale sale, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the price rows a sale's lines say they were quoted from.
    /// </summary>
    /// <remarks>
    /// The device sends identifiers and the server reads the amounts, so a
    /// register can name which of head office's own prices it charged but can
    /// never assert what that price was. A row that is superseded or expired is
    /// still returned: that a quoted price is no longer effective is exactly the
    /// case the caller has to recognise, and it cannot recognise what it is not
    /// given.
    /// </remarks>
    /// <param name="priceIds">The price rows named by the lines that name one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows found, possibly fewer than were asked for.</returns>
    Task<IReadOnlyList<QuotedPrice>> GetQuotedPricesAsync(
        IReadOnlyCollection<ProductPriceId> priceIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the facts a sale depends on for a location.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The facts, or <see langword="null"/> when the location does not exist.</returns>
    Task<SaleLocationFacts?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads what pricing a sale line needs about each product, with the
    /// effective price already resolved for the location and moment of the sale.
    /// </summary>
    /// <param name="productIds">The products sold.</param>
    /// <param name="locationId">The location the sale happens at.</param>
    /// <param name="at">The moment the sale happens.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products, possibly not containing every requested identifier.</returns>
    /// <remarks>
    /// It returns a read model rather than the <see cref="Product"/> aggregate on
    /// purpose. The handler reads five fields; loading an aggregate to get them
    /// meant every implementation had to be able to produce a whole
    /// <see cref="Product"/>, which a device cannot — it mirrors master data
    /// thinly and has no category, brand or unit-of-measure to rebuild one from.
    /// Asking for what the use case actually needs lets the server project from
    /// its catalogue and a device project from its cache, with the same handler
    /// above both.
    /// </remarks>
    Task<IReadOnlyList<SaleProduct>> GetSaleProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        LocationId locationId,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads whole products for a blind return, which needs barcodes, the base
    /// unit of measure and the default purchase cost to register goods it has no
    /// sale for.
    /// </summary>
    /// <param name="productIds">The products being returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products, possibly not containing every requested identifier.</returns>
    /// <remarks>
    /// This one keeps the aggregate, and can: <c>sale.return_blind</c> is not an
    /// offline-capable permission, so a blind return only ever runs on the
    /// server, where the whole catalogue is at hand. Splitting it from the sale's
    /// read model is what lets the sale path stay within what a device can
    /// mirror.
    /// </remarks>
    Task<IReadOnlyList<Product>> GetReturnProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the EXT-CUSTOMER counterparty location that the sold goods are
    /// posted to, mirroring the sellable-stock leg at the store.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The location identifier, or <see langword="null"/> when not provisioned.</returns>
    Task<LocationId?> GetExternalCustomerLocationIdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Finds the available-stock buckets for a product whose batches are past
    /// expiry at the location, as of the business date. Returned for the expired
    /// override path only — never for normal selling (<see cref="SaleErrors.ExpiredOnly"/>).
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="productId">The product.</param>
    /// <param name="businessDate">The business date; batches expiring on or after it are not past expiry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The expired available batches, or an empty list when none cover the product.</returns>
    Task<IReadOnlyList<ExpiredSaleBatch>> GetExpiredAvailableBatchesAsync(
        LocationId locationId,
        ProductId productId,
        DateOnly businessDate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a customer return with its return lines.
    /// </summary>
    /// <param name="salesReturn">The return to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The return identifier.</returns>
    Task<Result<SalesReturnId>> AddReturnAsync(SalesReturn salesReturn, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a customer return with its lines and refunds, for review before a
    /// refund is issued against it.
    /// </summary>
    /// <param name="salesReturnId">The return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The return, or <see langword="null"/> when it does not exist.</returns>
    Task<SalesReturn?> GetReturnByIdAsync(SalesReturnId salesReturnId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a refund issued against a return.
    /// </summary>
    /// <param name="salesReturnId">The return the refund is issued against.</param>
    /// <param name="refund">The refund to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refund identifier.</returns>
    Task<Result<RefundId>> AddRefundAsync(SalesReturnId salesReturnId, Refund refund, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the refund recorded for an event, so a retried refund replays its
    /// outcome instead of issuing twice.
    /// </summary>
    /// <param name="eventId">The refund's event identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refund, or <see langword="null"/> when the event has not been refunded.</returns>
    Task<Refund?> GetRefundByEventAsync(EventId eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends an entry to the sale receipt print log. The log is append-only:
    /// a reprint never rewrites history, it extends it.
    /// </summary>
    /// <param name="print">The print entry to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The print identifier.</returns>
    Task<Result<ReceiptPrintId>> AddReceiptPrintAsync(SaleReceiptPrint print, CancellationToken cancellationToken);
}

/// <summary>The facts a completed sale depends on.</summary>
/// <param name="Kind">The location kind.</param>
/// <param name="Settings">The location's trading configuration.</param>
public sealed record SaleLocationFacts(LocationKind Kind, LocationSettings Settings);

/// <summary>
/// One expired but still available bucket that may cover a sale line under an
/// expired override.
/// </summary>
/// <param name="BatchId">The batch, or <see langword="null"/> for products that do not track batches.</param>
/// <param name="LotNumber">The lot number, when tracked.</param>
/// <param name="Quantity">The available quantity.</param>
/// <param name="ExpiresOn">The expiry date, which is before the business date.</param>
/// <param name="UnitCost">The weighted average cost per unit.</param>
public sealed record ExpiredSaleBatch(
    BatchId? BatchId,
    string? LotNumber,
    decimal Quantity,
    DateOnly? ExpiresOn,
    decimal UnitCost);

/// <summary>
/// What a sale or blind-return line needs to know about one product, with its
/// price already resolved for that location and moment.
/// </summary>
/// <param name="Id">The product.</param>
/// <param name="Name">The name printed on the receipt line.</param>
/// <param name="IsVatExempt">Whether the line is exempt from VAT.</param>
/// <param name="TracksBatches">Whether the line must be allocated to batches.</param>
/// <param name="EffectivePriceId">
/// The price row in force, or <see langword="null"/> when the product has no
/// price at this location and moment. A line without an override cannot proceed
/// on a null; one with an override records it as the version that would have
/// applied.
/// </param>
/// <param name="EffectiveUnitPrice">The amount of that price row.</param>
public sealed record SaleProduct(
    ProductId Id,
    string Name,
    bool IsVatExempt,
    bool TracksBatches,
    ProductPriceId? EffectivePriceId,
    decimal? EffectiveUnitPrice);

/// <summary>
/// A price row a sale line was quoted from, read back by identifier.
/// </summary>
/// <param name="Id">The price row.</param>
/// <param name="ProductId">The product it prices, checked against the line.</param>
/// <param name="LocationId">The location it is scoped to, or null for every location.</param>
/// <param name="Amount">The amount, as the server holds it.</param>
public sealed record QuotedPrice(
    ProductPriceId Id,
    ProductId ProductId,
    LocationId? LocationId,
    decimal Amount);
