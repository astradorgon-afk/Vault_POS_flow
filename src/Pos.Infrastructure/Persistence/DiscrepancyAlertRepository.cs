using Microsoft.EntityFrameworkCore;
using Pos.Application.Notifications;
using Pos.Domain.Purchasing;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Reads recent documents with unresolved discrepancies for alerting.
/// </summary>
/// <remarks>
/// The document filters run in SQL; quantities are totalled in memory because
/// decimal aggregation does not translate on every provider.
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class DiscrepancyAlertRepository(PosDbContext context) : IDiscrepancyAlertRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceivingDiscrepancyAlert>> GetOpenReceivingDiscrepanciesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        List<GoodsReceipt> receipts = await context.GoodsReceipts
            .AsNoTracking()
            .Include(r => r.Discrepancies)
            .Where(r => r.Status == GoodsReceiptStatus.Posted
                && r.ReceivedAtUtc >= sinceUtc
                && r.Discrepancies.Any(d => d.ResolutionOutcome == null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return receipts
            .Select(r => new ReceivingDiscrepancyAlert(
                r.Id,
                r.Number,
                r.DestinationLocationId,
                r.ReceivedAtUtc,
                r.Discrepancies
                    .Where(d => d.ResolutionOutcome is null)
                    .OrderBy(d => d.LineNo)
                    .Select(d => new ReceivingDiscrepancyAlertLine(d.Kind, d.Quantity, d.ValueImpact))
                    .ToList()))
            .OrderBy(a => a.ReceivedAtUtc)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransferShortageAlert>> GetOpenTransferShortagesAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        List<Transfer> transfers = await context.Transfers
            .AsNoTracking()
            .Include(t => t.Discrepancies)
            .Where(t => t.ReceivedAtUtc != null
                && t.ReceivedAtUtc >= sinceUtc
                && t.Discrepancies.Any(d => d.ResolutionOutcome == null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return transfers
            .Select(t =>
            {
                List<TransferDiscrepancy> open = t.Discrepancies.Where(d => !d.IsResolved).ToList();
                return new TransferShortageAlert(
                    t.Id,
                    t.Number,
                    t.SourceLocationId,
                    t.DestinationLocationId,
                    t.ReceivedAtUtc!.Value,
                    open.Count,
                    open.Sum(d => d.Quantity));
            })
            .OrderBy(a => a.ReceivedAtUtc)
            .ToList();
    }
}
