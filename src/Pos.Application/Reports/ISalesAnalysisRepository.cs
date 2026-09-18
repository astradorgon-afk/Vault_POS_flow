using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>
/// What a sales analysis is being asked for.
/// </summary>
/// <remarks>
/// The locations are resolved from the caller's own authority before the query is
/// built, never taken from the request. A report is a place where asking for
/// somebody else's store is a one-parameter change, so the parameter does not
/// exist: a caller narrows within what they may already see, and cannot widen.
/// </remarks>
/// <param name="FromDate">The first business date, inclusive.</param>
/// <param name="ToDate">The last business date, inclusive.</param>
/// <param name="GroupBy">What to cut the rows by.</param>
/// <param name="Locations">The stores to include, or empty for every store.</param>
/// <param name="Limit">The most rows to return.</param>
public sealed record SalesAnalysisQuery(
    DateOnly FromDate,
    DateOnly ToDate,
    SalesAnalysisGrouping GroupBy,
    IReadOnlyCollection<LocationId> Locations,
    int Limit);

/// <summary>Builds the sales analyses of ROADMAP §Phase 15.</summary>
public interface ISalesAnalysisRepository
{
    /// <summary>Aggregates completed sale lines over a period.</summary>
    /// <param name="query">What to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    Task<SalesAnalysisReport> GetSalesAnalysisAsync(
        SalesAnalysisQuery query,
        CancellationToken cancellationToken);

    /// <summary>Breaks the period's takings down by how they were paid.</summary>
    /// <param name="fromDate">The first business date, inclusive.</param>
    /// <param name="toDate">The last business date, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One row per method used, largest first.</returns>
    Task<IReadOnlyList<SalesPaymentMethodRow>> GetPaymentMethodBreakdownAsync(
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken);
}
