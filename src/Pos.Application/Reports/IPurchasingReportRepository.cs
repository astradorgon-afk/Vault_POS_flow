using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>Builds the purchasing reports of ROADMAP §Phase 15.</summary>
public interface IPurchasingReportRepository
{
    /// <summary>Lists the purchase orders raised in a window.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The destination locations to include, or empty for every one.</param>
    /// <param name="supplierId">One supplier, or null for all of them.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    Task<PurchaseOrderReport> GetPurchaseOrdersAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        SupplierId? supplierId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Scores each supplier over a window.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The destination locations to include, or empty for every one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One row per supplier ordered from, most orders first.</returns>
    Task<IReadOnlyList<SupplierPerformanceRow>> GetSupplierPerformanceAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken);
}
