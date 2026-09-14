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
    /// Loads the facts a sale depends on for a location.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The facts, or <see langword="null"/> when the location does not exist.</returns>
    Task<SaleLocationFacts?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the products being sold, with their price rows, so the handler can
    /// resolve the effective price and VAT class for every line.
    /// </summary>
    /// <param name="productIds">The products sold.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products, possibly not containing every requested identifier.</returns>
    Task<IReadOnlyList<Product>> GetSaleProductsAsync(
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