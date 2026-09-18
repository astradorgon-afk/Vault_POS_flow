using Pos.Domain.Common;
using Pos.Domain.Reports;

namespace Pos.Application.Reports;

/// <summary>Builds the transfer and distribution reports of ROADMAP §Phase 15.</summary>
public interface ITransferReportRepository
{
    /// <summary>Lists the transfers raised in a window.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">
    /// The stores to include, or empty for every store. A transfer is included
    /// when <b>either</b> end is in scope: it is as much the receiving store's
    /// business as the sending one's, and a manager who could not see what was
    /// sent to them could not chase it.
    /// </param>
    /// <param name="openOnly">Whether to keep only transfers that have not been received.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    Task<TransferReport> GetTransfersAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        bool openOnly,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Rolls dispatched transfers up by the lane they travelled.</summary>
    /// <param name="fromUtc">The start of the window, inclusive.</param>
    /// <param name="toUtc">The end of it, inclusive.</param>
    /// <param name="locations">The stores to include, or empty for every store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One row per lane, busiest first.</returns>
    Task<IReadOnlyList<DistributionLaneRow>> GetDistributionAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken);
}
