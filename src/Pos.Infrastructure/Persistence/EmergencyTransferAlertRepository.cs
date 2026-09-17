using Microsoft.EntityFrameworkCore;
using Pos.Application.Notifications;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence;

/// <summary>Reads committed emergency transfers for durable notification generation.</summary>
/// <param name="context">The database context.</param>
public sealed class EmergencyTransferAlertRepository(PosDbContext context) : IEmergencyTransferAlertRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EmergencyTransferAlert>> GetRecentAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        List<Transfer> transfers = await context.Transfers
            .AsNoTracking()
            .Include(t => t.Lines)
            .Where(t => t.Mode == TransferMode.EmergencyOffline
                && t.EmergencyLedgerGroupId != null
                && t.CreatedAtUtc >= sinceUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return transfers
            .Select(t => new EmergencyTransferAlert(
                t.Id,
                t.Number,
                t.SourceLocationId,
                t.DestinationLocationId,
                t.CreatedAtUtc,
                t.Lines.Count,
                t.Lines.Sum(l => l.RequestedQuantity)))
            .OrderBy(a => a.CreatedAtUtc)
            .ToList();
    }
}
