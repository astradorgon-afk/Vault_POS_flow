using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// The events head office had to turn away or set aside, and the one thing that
/// can be done about them.
/// </summary>
/// <remarks>
/// <para>
/// The list is read from <c>sync.processed_event</c> rather than from a second
/// table a device reports into. Everything here is something the server itself
/// decided, so the record already exists and a reporting round-trip could only
/// add a way for the two to disagree. What the server genuinely cannot see are
/// the events that never reached it at all; those are on the register, and its
/// own banner counts them (POS.md §6).
/// </para>
/// <para>
/// Retrying is not re-sending the same question. A refused event is refused
/// because of something true at the time — a withdrawn product, a cashier
/// without authority, a price that did not settle — and asking again unchanged
/// earns the same answer. It becomes worth asking when a person changes that
/// thing, and this records that they did, as a directive on the register's own
/// feed. A register that is offline cannot be told anything; the feed is
/// already what it comes back to.
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class SyncFailureService(PosDbContext context, ISystemClock clock)
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new StronglyTypedIdJsonConverter() },
    };

    private static readonly string[] Unresolved =
    [
        nameof(SyncOutcome.Rejected),
        nameof(SyncOutcome.RequiresReview),
        nameof(SyncOutcome.Conflict),
    ];

    /// <summary>Lists what still needs a person, newest first.</summary>
    /// <param name="locationId">One store, or null for every store.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The failures.</returns>
    public async Task<IReadOnlyList<SyncFailureView>> ListAsync(
        LocationId? locationId,
        int limit,
        CancellationToken cancellationToken)
    {
        int take = Math.Clamp(limit, 1, 500);

        var rows = await (
            from processed in context.ProcessedEvents.AsNoTracking()
            join device in context.Set<Device>().AsNoTracking()
                on processed.DeviceId equals device.Id
            where Unresolved.Contains(processed.Outcome)
                  && (locationId == null || device.LocationId == locationId)
            orderby processed.AppliedAtUtc descending
            select new
            {
                processed.EventId,
                processed.DeviceId,
                device.ShortCode,
                device.LocationId,
                processed.DeviceSequence,
                processed.Type,
                processed.Outcome,
                processed.ResultJson,
                processed.AppliedAtUtc,
            })
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new SyncFailureView(
                r.EventId.Value,
                r.DeviceId.Value,
                r.ShortCode,
                r.LocationId.Value,
                r.DeviceSequence,
                r.Type,
                r.Outcome,
                r.ResultJson,
                r.AppliedAtUtc)),
        ];
    }

    /// <summary>
    /// Asks the register holding an event to send it again.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether there was such an event to ask about.</returns>
    /// <remarks>
    /// The directive is scoped to the device that holds the event, and the
    /// device only reopens an event that had actually stopped moving. An
    /// accepted event is never reopened: asking a register to send a sale head
    /// office already holds is how a day's takings get counted twice.
    /// </remarks>
    public async Task<bool> RequestRetryAsync(EventId eventId, CancellationToken cancellationToken)
    {
        var failed = await context.ProcessedEvents
            .AsNoTracking()
            .Where(e => e.EventId == eventId && Unresolved.Contains(e.Outcome))
            .Select(e => new { e.DeviceId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (failed is null)
        {
            return false;
        }

        LocationId scope = await context.Set<Device>()
            .AsNoTracking()
            .Where(d => d.Id == failed.DeviceId)
            .Select(d => d.LocationId)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

        long sequence = await ChangeFeedSequenceAllocator
            .NextAsync(context, cancellationToken)
            .ConfigureAwait(false);

        context.ChangeFeed.Add(new ChangeFeedEntry(
            sequence,
            nameof(SyncRetryRequested),
            scope.Value,
            JsonSerializer.Serialize(
                new SyncRetryRequested(sequence, failed.DeviceId, eventId), PayloadOptions),
            clock.UtcNow));

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
