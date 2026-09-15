using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Builds the daily sales report (ROADMAP §Phase 11 "Daily sales summary"): the
/// store manager's end-of-day view of one location, one business date.
/// </summary>
public interface IDailySalesReportRepository
{
    /// <summary>
    /// Aggregates every completed sale, its payments, and every refund issued
    /// during the day at the location, plus the shifts that contributed to it.
    /// </summary>
    /// <param name="locationId">The location.</param>
    /// <param name="businessDate">The business date.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, or <see langword="null"/> when the location does not exist.</returns>
    Task<DailySalesReport?> GetDailySalesReportAsync(
        LocationId locationId,
        DateOnly businessDate,
        CancellationToken cancellationToken);
}