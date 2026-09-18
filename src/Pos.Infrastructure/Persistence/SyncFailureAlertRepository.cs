using Microsoft.EntityFrameworkCore;
using Pos.Application.Notifications;
using Pos.Domain.Devices;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Reads the uploaded events that still need a person, for durable notification
/// generation.
/// </summary>
/// <remarks>
/// The same source the failure list reads — <c>sync.processed_event</c>, which
/// carries the server's own verdicts. There is deliberately no second table: an
/// alert derived from anything but the decision itself could disagree with the
/// list somebody opens after reading it.
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class SyncFailureAlertRepository(PosDbContext context) : ISyncFailureAlertRepository
{
    private static readonly string[] Unresolved =
    [
        nameof(SyncOutcome.Rejected),
        nameof(SyncOutcome.RequiresReview),
        nameof(SyncOutcome.Conflict),
    ];

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncFailureAlert>> GetRecentAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from processed in context.ProcessedEvents.AsNoTracking()
            join device in context.Set<Device>().AsNoTracking()
                on processed.DeviceId equals device.Id
            where Unresolved.Contains(processed.Outcome) && processed.AppliedAtUtc >= sinceUtc
            orderby processed.AppliedAtUtc
            select new
            {
                processed.EventId,
                device.ShortCode,
                device.LocationId,
                processed.DeviceSequence,
                processed.Type,
                processed.Outcome,
                processed.ResultJson,
                processed.AppliedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new SyncFailureAlert(
                r.EventId,
                r.ShortCode,
                r.LocationId,
                r.DeviceSequence,
                r.Type,
                r.Outcome,
                r.ResultJson,
                r.AppliedAtUtc)),
        ];
    }
}
