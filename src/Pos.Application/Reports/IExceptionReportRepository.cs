using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>Builds the inventory exception reports of ROADMAP §Phase 15.</summary>
public interface IExceptionReportRepository
{
    /// <summary>Summarises stock written off or corrected, by why.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, most units out first.</returns>
    Task<IReadOnlyList<AdjustmentSummaryRow>> GetAdjustmentsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken);

    /// <summary>Values the losses, by why.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, with its own total.</returns>
    Task<ShrinkageReport> GetShrinkageAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken);

    /// <summary>Lists the count lines that did not match the system.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="includeUncounted">
    /// Whether to keep lines nobody counted. Off by default, because the question
    /// the report usually answers is what was wrong — but a supervisor closing a
    /// count needs to see what was never looked at, and that is not a variance of
    /// zero.
    /// </param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    Task<CountVarianceReport> GetCountVariancesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        bool includeUncounted,
        int limit,
        CancellationToken cancellationToken);
}
