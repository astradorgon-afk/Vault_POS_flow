using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>
/// What a dashboard request is asking for, once the caller's authority has been
/// resolved.
/// </summary>
/// <param name="Range">The named period.</param>
/// <param name="FromDate">The first business date it covers.</param>
/// <param name="ToDate">The last.</param>
/// <param name="TimeZoneId">The timezone those dates were resolved in.</param>
/// <param name="Locations">The stores in scope, or empty for every store.</param>
/// <param name="IncludeFinancial">
/// Whether the caller may see cost, margin and valuation. Passed rather than
/// re-derived, so the one place that answers "may they" is the endpoint and the
/// repository cannot accidentally answer it differently.
/// </param>
public sealed record DashboardQuery(
    DashboardRange Range,
    DateOnly FromDate,
    DateOnly ToDate,
    string TimeZoneId,
    IReadOnlyCollection<LocationId> Locations,
    bool IncludeFinancial);

/// <summary>Builds the owner dashboard of ROADMAP §Phase 16.</summary>
public interface IDashboardRepository
{
    /// <summary>Builds the overview: headline numbers, store comparison, stock panel.</summary>
    /// <param name="query">What to report on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The overview.</returns>
    Task<DashboardOverview> GetOverviewAsync(DashboardQuery query, CancellationToken cancellationToken);

    /// <summary>Builds the exception board.</summary>
    /// <param name="query">What to report on.</param>
    /// <param name="highValueThreshold">What counts as a high-value adjustment.</param>
    /// <param name="offlineAfter">How long without contact makes a register offline.</param>
    /// <param name="sampleSize">How many examples each panel carries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every panel, worst first, including the empty ones.</returns>
    Task<DashboardExceptions> GetExceptionsAsync(
        DashboardQuery query,
        decimal highValueThreshold,
        TimeSpan offlineAfter,
        int sampleSize,
        CancellationToken cancellationToken);
}
