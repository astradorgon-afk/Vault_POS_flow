using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>Builds the audit, quarantine and expiry reports of ROADMAP §Phase 15.</summary>
public interface IAuditReportRepository
{
    /// <summary>Lists recorded actions in a window.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="userId">One actor, or null for all of them.</param>
    /// <param name="action">One action code, or null for all of them.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, newest first.</returns>
    Task<AuditActivityReport> GetActivityAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        UserId? userId,
        string? action,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Lists unauthorized-inventory incidents in a window.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="openOnly">Whether to keep only incidents nobody has closed.</param>
    /// <param name="asOfUtc">The instant open incidents are aged against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, longest open first.</returns>
    Task<IReadOnlyList<QuarantineIncidentRow>> GetQuarantineIncidentsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        bool openOnly,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);

    /// <summary>Lists stock that has expired or is about to, and is still held.</summary>
    /// <param name="asOf">The date expiry is measured to.</param>
    /// <param name="withinDays">
    /// How far ahead to look. Stock already past its date is always included,
    /// however far back it went: a batch that expired last month is more urgent
    /// than one expiring next week, not less.
    /// </param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, soonest to expire first.</returns>
    Task<IReadOnlyList<ExpiringStockRow>> GetExpiringStockAsync(
        DateOnly asOf,
        int withinDays,
        IReadOnlyCollection<LocationId> locations,
        int limit,
        CancellationToken cancellationToken);
}
