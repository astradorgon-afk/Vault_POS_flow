using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Offline;

/// <summary>Sends one upload batch to head office and returns its answer.</summary>
/// <param name="request">The batch.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Head office's per-event answer.</returns>
public delegate Task<SyncPushResponse> DeviceUploadTransport(SyncPushRequest request, CancellationToken cancellationToken);

/// <summary>What an upload achieved.</summary>
/// <param name="Delivered">Events head office accepted.</param>
/// <param name="Flagged">Events head office received but parked, refused or disputed; they are on its sync-failure queue.</param>
/// <param name="Remaining">Events still waiting on this register.</param>
/// <param name="WaitingForOthers">
/// Of those, events that belong to someone else. Head office only takes a
/// person's events in a session of their own, and the queue goes in order, so
/// they wait until that person signs in here while connected.
/// </param>
/// <param name="WaitingFor">The display names of the people those events wait for.</param>
public sealed record DeviceUploadResult(
    int Delivered,
    int Flagged,
    int Remaining,
    int WaitingForOthers,
    IReadOnlyList<string> WaitingFor);

/// <summary>
/// Delivers the outbox to head office (OFFLINE_SYNC.md §3): in device-sequence
/// order, at most 100 events a batch, one result per event.
/// </summary>
/// <remarks>
/// <para>
/// Only the signed-in person's events are sent. Head office refuses an event
/// whose user is not the authenticated one, and it takes events strictly in
/// sequence, so the upload stops at the first event that belongs to someone
/// else: sending past it would only earn a deferral.
/// </para>
/// <para>
/// Every event keeps the identity it was queued with, so an upload interrupted
/// after head office committed is recognised on the next attempt and reported
/// as a duplicate, never applied twice. An event head office has answered for
/// — accepted, parked, refused or disputed — is never sent again; the refused
/// and disputed ones are on head office's sync-failure queue with their full
/// payload, which is where they are resolved.
/// </para>
/// </remarks>
/// <param name="database">The encrypted device store.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceOutboxUploader(DeviceDatabaseInitializer database, ISystemClock clock)
{
    /// <summary>The most events head office accepts in one batch.</summary>
    public const int BatchSize = 100;

    /// <summary>Uploads everything the signed-in person has waiting, batch by batch.</summary>
    /// <param name="userId">The person head office authenticated.</param>
    /// <param name="transport">Sends a batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was delivered and what is still waiting.</returns>
    /// <exception cref="Exception">
    /// Whatever <paramref name="transport"/> throws. The batch in flight is put
    /// back in the queue first, so nothing is lost.
    /// </exception>
    public async Task<DeviceUploadResult> UploadAsync(
        UserId userId,
        DeviceUploadTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        int delivered = 0;
        int flagged = 0;

        while (true)
        {
            (SyncPushRequest? request, List<OutboxEvent> batch) =
                await PrepareBatchAsync(userId, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                break;
            }

            SyncPushResponse response;
            try
            {
                response = await transport(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ReturnToQueueAsync(batch, ex.Message, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            (int accepted, int parked, bool progressed) =
                await RecordAsync(batch, response, cancellationToken).ConfigureAwait(false);
            delivered += accepted;
            flagged += parked;

            if (!progressed)
            {
                // A deferral or a refusal with nothing answered: retrying at once
                // would only get the same answer.
                break;
            }
        }

        return await GetBacklogAsync(userId, delivered, flagged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reports what is waiting without sending anything.</summary>
    /// <param name="userId">The signed-in person, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The backlog.</returns>
    public Task<DeviceUploadResult> GetBacklogAsync(UserId? userId, CancellationToken cancellationToken = default)
        => GetBacklogAsync(userId, 0, 0, cancellationToken);

    private async Task<DeviceUploadResult> GetBacklogAsync(
        UserId? userId,
        int delivered,
        int flagged,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<UserId> waiting = await context.Outbox
            .AsNoTracking()
            .Where(OutboxEvent.IsUnsent)
            .OrderBy(e => e.DeviceSequence)
            .Select(e => e.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<UserId> others = [.. waiting.Where(u => u != userId)];
        UserId[] distinct = [.. others.Distinct()];

        List<string> names = [];
        foreach (UserId other in distinct)
        {
            string? name = await context.Users
                .AsNoTracking()
                .Where(u => u.Id == other)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            name ??= await context.OfflineCredentials
                .AsNoTracking()
                .Where(c => c.UserId == other)
                .Select(c => c.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            names.Add(name ?? "another cashier");
        }

        return new DeviceUploadResult(delivered, flagged, waiting.Count, others.Count, names);
    }

    private async Task<(SyncPushRequest? Request, List<OutboxEvent> Batch)> PrepareBatchAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return (null, []);
        }

        List<OutboxEvent> head = await context.Outbox
            .Where(OutboxEvent.IsUnsent)
            .OrderBy(e => e.DeviceSequence)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The contiguous run that is this person's: head office takes events
        // strictly in sequence, so the first one that is not theirs ends it.
        List<OutboxEvent> batch = [.. head.TakeWhile(e => e.UserId == userId)];
        if (batch.Count == 0)
        {
            return (null, []);
        }

        DateTimeOffset now = clock.UtcNow;
        foreach (OutboxEvent item in batch)
        {
            item.MarkSending(now);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        SyncPushRequest request = new(
            profile.DeviceId.Value,
            now,
            Environment.TickCount64,
            [.. batch.Select(e => new SyncPushEvent(
                e.EventId.Value,
                e.DeviceSequence,
                e.Type.ToString(),
                e.PayloadJson,
                Convert.ToBase64String(e.PayloadHash),
                e.UserId.Value,
                e.LocationId.Value,
                e.OccurredAtUtc,
                e.DeviceUptimeTicks,
                e.CorrelationId.Value))]);

        return (request, batch);
    }

    private async Task<(int Accepted, int Flagged, bool Progressed)> RecordAsync(
        List<OutboxEvent> batch,
        SyncPushResponse response,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<Guid, SyncPushEventResult> answers = response.Results
            .GroupBy(r => r.EventId)
            .ToDictionary(g => g.Key, g => g.Last());

        EventId[] ids = [.. batch.Select(e => e.EventId)];
        List<OutboxEvent> tracked = await context.Outbox
            .Where(e => ids.Contains(e.EventId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int accepted = 0;
        int flagged = 0;
        bool progressed = false;

        foreach (OutboxEvent item in tracked.OrderBy(e => e.DeviceSequence))
        {
            if (!answers.TryGetValue(item.EventId.Value, out SyncPushEventResult? answer))
            {
                item.ReturnToQueue(response.Message ?? "Head office did not answer for this event.", null);
                continue;
            }

            string json = System.Text.Json.JsonSerializer.Serialize(answer);
            switch (answer.Outcome)
            {
                case "Accepted":
                    item.RecordOutcome(OutboxStatus.Synchronized, null, json);
                    accepted++;
                    progressed = true;
                    break;

                case "RequiresReview":
                    item.RecordOutcome(OutboxStatus.RequiresReview, answer.Message, json);
                    flagged++;
                    progressed = true;
                    break;

                case "Conflict":
                    item.RecordOutcome(OutboxStatus.Conflict, answer.Message, json);
                    flagged++;
                    progressed = true;
                    break;

                case "Rejected" when answer.ErrorCode == "sync.event_scope":
                    // Not recorded by head office: it belongs to another session.
                    item.ReturnToQueue(answer.Message ?? "Waiting for its own cashier to sign in.", null);
                    break;

                case "Rejected":
                    // Head office recorded it and raised a sync failure with the
                    // full payload; resolving it is a person's job there, not a
                    // retry's here.
                    item.RecordOutcome(OutboxStatus.RequiresReview, answer.Message, json);
                    flagged++;
                    progressed = true;
                    break;

                default:
                    // Deferred, or an outcome this client does not know: keep it.
                    item.ReturnToQueue(answer.Message ?? answer.Outcome, null);
                    break;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (accepted, flagged, progressed);
    }

    private async Task ReturnToQueueAsync(
        List<OutboxEvent> batch,
        string reason,
        CancellationToken cancellationToken)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        EventId[] ids = [.. batch.Select(e => e.EventId)];
        List<OutboxEvent> tracked = await context.Outbox
            .Where(e => ids.Contains(e.EventId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (OutboxEvent item in tracked)
        {
            item.ReturnToQueue(reason, null);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
