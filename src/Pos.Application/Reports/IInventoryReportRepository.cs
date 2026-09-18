using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>
/// What an inventory report is being asked for.
/// </summary>
/// <param name="Locations">The stores to include, or empty for every store.</param>
/// <param name="ProductId">One product, or null for all of them.</param>
/// <param name="IncludeEmpty">
/// Whether to keep a bucket holding nothing. Off by default: a catalogue of ten
/// thousand products at forty stores is four hundred thousand rows of zero, and
/// the one question this report answers is what is actually there.
/// </param>
/// <param name="Limit">The most rows to return.</param>
public sealed record InventoryReportQuery(
    IReadOnlyCollection<LocationId> Locations,
    ProductId? ProductId,
    bool IncludeEmpty,
    int Limit);

/// <summary>Builds the inventory reports of ROADMAP §Phase 15.</summary>
public interface IInventoryReportRepository
{
    /// <summary>What is on the shelf, by product and location.</summary>
    /// <param name="query">What to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, largest on-hand first.</returns>
    Task<IReadOnlyList<InventoryOnHandRow>> GetOnHandAsync(
        InventoryReportQuery query,
        CancellationToken cancellationToken);

    /// <summary>What the stock is worth.</summary>
    /// <param name="query">What to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The valuation, with its own total.</returns>
    Task<InventoryValuationReport> GetValuationAsync(
        InventoryReportQuery query,
        CancellationToken cancellationToken);

    /// <summary>How old the stock is, by product and location.</summary>
    /// <param name="asOf">The date age is measured to.</param>
    /// <param name="query">What to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, oldest stock first.</returns>
    Task<IReadOnlyList<InventoryAgeingRow>> GetAgeingAsync(
        DateOnly asOf,
        InventoryReportQuery query,
        CancellationToken cancellationToken);

    /// <summary>Stock that is not moving.</summary>
    /// <param name="fromUtc">The start of the window sales are counted over.</param>
    /// <param name="toUtc">The end of it.</param>
    /// <param name="query">What to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, longest unsold first.</returns>
    Task<InventoryDeadStockReport> GetDeadStockAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        InventoryReportQuery query,
        CancellationToken cancellationToken);

    /// <summary>Every ledger leg in a window, in the order the ledger wrote them.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of the window, inclusive.</param>
    /// <param name="query">The stores, the product and the row cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    Task<InventoryMovementReport> GetMovementsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        InventoryReportQuery query,
        CancellationToken cancellationToken);
}
