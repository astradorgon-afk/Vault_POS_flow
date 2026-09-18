using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Reports;
using Pos.Domain.Transfers;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="ITransferReportRepository" />
/// <remarks>
/// <para>
/// A transfer is in scope when <b>either</b> end is: it is as much the receiving
/// store's business as the sending one's, and a manager who could not see what
/// was sent to them could not chase it. This is the one report whose scope is not
/// a single location column, and getting it wrong in the other direction —
/// filtering on the source alone — would hide every incoming shipment from the
/// people waiting for it.
/// </para>
/// <para>
/// The quantities are read from the lines and allocations rather than recomputed
/// from the ledger. What was requested, what was picked and what was counted are
/// three separate records of three separate human acts, and the gaps between them
/// are the report.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class TransferReportRepository(PosDbContext context) : ITransferReportRepository
{
    /// <inheritdoc />
    public async Task<TransferReport> GetTransfersAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        bool openOnly,
        int limit,
        CancellationToken cancellationToken)
    {
        List<Transfer> transfers = await InScope(fromUtc, toUtc, locations)
            .Include(t => t.Lines)
            .Include(t => t.Allocations)
            .Include(t => t.Discrepancies)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, (string Code, string Name)> names =
            await NamesAsync(transfers, cancellationToken).ConfigureAwait(false);

        List<TransferReportRow> all =
        [
            .. transfers
                .Where(t => !openOnly || t.ReceivedAtUtc is null)
                .Select(t => Row(t, names, toUtc))

                // Longest in flight first, then newest. What is stuck is what
                // somebody opens this report to find.
                .OrderByDescending(r => r.DaysInFlight ?? -1)
                .ThenByDescending(r => r.CreatedAtUtc),
        ];

        List<TransferReportRow> rows = [.. all.Take(limit)];

        return new TransferReport(fromUtc, toUtc, rows, Truncated: all.Count > rows.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DistributionLaneRow>> GetDistributionAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken)
    {
        // Counted on dispatch, and over the dispatch date rather than the creation
        // date: the question is what the warehouse sent this month, not what it
        // was asked for last month and sent this one.
        IQueryable<Transfer> dispatched = context.Transfers
            .AsNoTracking()
            .Where(t => t.DispatchedAtUtc != null
                        && t.DispatchedAtUtc >= fromUtc
                        && t.DispatchedAtUtc <= toUtc);

        if (locations.Count > 0)
        {
            dispatched = dispatched.Where(t =>
                locations.Contains(t.SourceLocationId) || locations.Contains(t.DestinationLocationId));
        }

        List<Transfer> transfers = await dispatched
            .Include(t => t.Allocations)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, (string Code, string Name)> names =
            await NamesAsync(transfers, cancellationToken).ConfigureAwait(false);

        return
        [
            .. transfers
                .GroupBy(t => (t.SourceLocationId, t.DestinationLocationId))
                .Select(g => Lane(g, names))
                .OrderByDescending(r => r.DispatchedQuantity)
                .ThenBy(r => r.SourceCode, StringComparer.Ordinal)
                .ThenBy(r => r.DestinationCode, StringComparer.Ordinal),
        ];
    }

    private IQueryable<Transfer> InScope(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations)
    {
        IQueryable<Transfer> transfers = context.Transfers
            .AsNoTracking()
            .Where(t => t.CreatedAtUtc >= fromUtc && t.CreatedAtUtc <= toUtc);

        return locations.Count == 0
            ? transfers
            : transfers.Where(t =>
                locations.Contains(t.SourceLocationId) || locations.Contains(t.DestinationLocationId));
    }

    private async Task<Dictionary<Guid, (string Code, string Name)>> NamesAsync(
        List<Transfer> transfers,
        CancellationToken cancellationToken)
    {
        List<LocationId> ids =
        [
            .. transfers.Select(t => t.SourceLocationId)
                .Concat(transfers.Select(t => t.DestinationLocationId))
                .Distinct(),
        ];

        var rows = await context.Locations
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { Id = l.Id.Value, l.Code, l.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => (r.Code, r.Name));
    }

    private static TransferReportRow Row(
        Transfer transfer,
        Dictionary<Guid, (string Code, string Name)> names,
        DateTimeOffset asOf)
    {
        (string sourceCode, _) = Name(names, transfer.SourceLocationId);
        (string destinationCode, _) = Name(names, transfer.DestinationLocationId);

        // Only meaningful while the stock is still out there. A transfer sitting
        // dispatched for a fortnight is the row this report exists to surface.
        int? inFlight = transfer.DispatchedAtUtc is { } dispatched && transfer.ReceivedAtUtc is null
            ? (int)Math.Floor((asOf - dispatched).TotalDays)
            : null;

        return new TransferReportRow(
            transfer.Id.Value,
            transfer.Number,
            transfer.Status,
            transfer.Kind,
            transfer.Mode,
            transfer.SourceLocationId.Value,
            sourceCode,
            transfer.DestinationLocationId.Value,
            destinationCode,
            transfer.CreatedAtUtc,
            transfer.DispatchedAtUtc,
            transfer.ReceivedAtUtc,
            transfer.Lines.Count,
            transfer.Lines.Sum(l => l.RequestedQuantity),
            transfer.Allocations.Sum(a => a.Quantity),
            transfer.Allocations.Sum(a => a.ReceivedQuantity),
            transfer.Allocations.Sum(a => a.DamagedQuantity),
            transfer.Discrepancies.Count(d => d.ResolutionOutcome is null),
            inFlight);
    }

    private static DistributionLaneRow Lane(
        IGrouping<(LocationId Source, LocationId Destination), Transfer> g,
        Dictionary<Guid, (string Code, string Name)> names)
    {
        (string sourceCode, string sourceName) = Name(names, g.Key.Source);
        (string destinationCode, string destinationName) = Name(names, g.Key.Destination);

        decimal dispatched = g.Sum(t => t.Allocations.Sum(a => a.Quantity));
        decimal received = g.Sum(t => t.Allocations.Sum(a => a.ReceivedQuantity));
        decimal damaged = g.Sum(t => t.Allocations.Sum(a => a.DamagedQuantity));

        return new DistributionLaneRow(
            g.Key.Source.Value,
            sourceCode,
            sourceName,
            g.Key.Destination.Value,
            destinationCode,
            destinationName,
            g.Count(),
            dispatched,
            received,
            damaged,

            // What left and has not turned up, damaged or otherwise. Stock still
            // legitimately in transit counts here too, which is the point: the
            // number a lane is judged on is what has not arrived yet.
            dispatched - received - damaged);
    }

    /// <summary>
    /// A location's code and name, or empty strings where it has gone.
    /// </summary>
    /// <remarks>
    /// A store closed since the transfer still sent or received what it did.
    /// Dropping the row would quietly make a lane's totals disagree with the
    /// stock that actually moved.
    /// </remarks>
    private static (string Code, string Name) Name(
        Dictionary<Guid, (string Code, string Name)> names,
        LocationId id)
        => names.TryGetValue(id.Value, out (string Code, string Name) found)
            ? found
            : (string.Empty, string.Empty);
}
