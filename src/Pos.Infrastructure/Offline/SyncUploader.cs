using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Carries one batch of outbox events to the server and brings the verdicts
/// back.
/// </summary>
/// <remarks>
/// A separate port from the uploader so the retry rules can be tested without a
/// network, and so the transport can own what only it knows: the base address,
/// the device's token, and how to recognise an answer that never came.
/// </remarks>
public interface ISyncTransport
{
    /// <summary>Sends one batch.</summary>
    /// <param name="request">The batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The server's answer, or why there wasn't one.</returns>
    Task<Result<SyncPushResponse>> PushAsync(SyncPushRequest request, CancellationToken cancellationToken);

    /// <summary>Asks for one page of the change feed.</summary>
    /// <param name="cursor">The sequence this device last stored.</param>
    /// <param name="limit">How many changes it will take.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The page, or a failure. <c>sync.rebaseline_required</c> is the one failure
    /// that is not a transport problem: the server can no longer serve this
    /// cursor and the device has to start from a fresh baseline.
    /// </returns>
    Task<Result<SyncPullResponse>> PullAsync(long cursor, int limit, CancellationToken cancellationToken);

    /// <summary>Asks for this device's whole starting state.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The baseline, shaped as changes so the same applier writes it.</returns>
    Task<Result<SyncBaselineResponse>> BaselineAsync(CancellationToken cancellationToken);
}

/// <summary>What one upload run did.</summary>
/// <param name="Sent">How many events were put on the wire.</param>
/// <param name="Accepted">How many the server took.</param>
/// <param name="Deferred">How many the server is still waiting on.</param>
/// <param name="Escalated">How many now need a person.</param>
/// <param name="Unreachable">Whether the run ended because the server could not be reached.</param>
public sealed record SyncUploadOutcome(
    int Sent,
    int Accepted,
    int Deferred,
    int Escalated,
    bool Unreachable)
{
    /// <summary>A run that found nothing to send.</summary>
    public static SyncUploadOutcome Idle => new(0, 0, 0, 0, false);
}

/// <summary>
/// Drains the device's outbox (OFFLINE_SYNC.md §3.2).
/// </summary>
/// <remarks>
/// <para>
/// Events go up in <c>deviceSequence</c> order and a batch stops at the first
/// <see cref="SyncOutcome.Deferred"/>, because everything behind a gap is
/// waiting on the same missing event and sending it would only earn the same
/// answer.
/// </para>
/// <para>
/// Nothing is ever deleted. An event the server refused, one that needs review
/// and one whose retries ran out all keep their row, their payload and the
/// server's own words — they are the sync-failure queue, and a queue that
/// tidies away its worst entries is a queue nobody can act on.
/// </para>
/// <para>
/// The verdicts are written in one local transaction with the batch's own
/// bookkeeping, so a device that loses power mid-write comes back either having
/// recorded the whole answer or none of it, and re-sends safely either way: the
/// event identifiers did not change, so the server replays its original
/// verdicts.
/// </para>
/// </remarks>
/// <param name="context">The device store.</param>
/// <param name="transport">How a batch reaches the server.</param>
/// <param name="profile">This device's enrolled identity.</param>
/// <param name="clock">The device clock.</param>
/// <param name="policy">The backoff rules.</param>
public sealed class SyncUploader(
    PosDeviceDbContext context,
    ISyncTransport transport,
    IDeviceProfileAccessor profile,
    ISystemClock clock,
    SyncRetryPolicy policy)
{
    /// <summary>The most events one batch carries (OFFLINE_SYNC.md §3.2).</summary>
    public const int MaxBatchSize = 100;

    /// <summary>The most payload bytes one batch carries.</summary>
    public const int MaxBatchBytes = 512 * 1024;

    /// <summary>Sends whatever is due and records what came back.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the run did.</returns>
    public async Task<SyncUploadOutcome> RunOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;

        List<OutboxEvent> due = await context.Outbox
            .Where(e => (e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.Sending)
                        && (e.NextRetryAtUtc == null || e.NextRetryAtUtc <= now))
            .OrderBy(e => e.DeviceSequence)
            .Take(MaxBatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return SyncUploadOutcome.Idle;
        }

        List<OutboxEvent> batch = TakeWhileUnderByteLimit(due);
        DeviceStoreProfile enrolled = await profile.GetAsync(cancellationToken).ConfigureAwait(false);

        foreach (OutboxEvent queued in batch)
        {
            queued.MarkSending(now);
        }

        SyncPushRequest request = new(
            enrolled.DeviceId.Value,
            Guid.CreateVersion7(),
            now,
            Environment.TickCount64,
            [.. batch.Select(e => new SyncPushEvent(
                e.EventId.Value,
                e.DeviceSequence,
                e.Type.ToString(),
                e.OccurredAtUtc,
                e.PayloadJson,
                Convert.ToBase64String(e.PayloadHash)))]);

        Result<SyncPushResponse> answered = await transport
            .PushAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (answered.IsFailure)
        {
            int escalated = ScheduleRetries(batch, now, answered.Error.Message);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new SyncUploadOutcome(batch.Count, 0, 0, escalated, Unreachable: true);
        }

        SyncUploadOutcome outcome = Record(batch, answered.Value, now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return outcome;
    }

    /// <summary>
    /// Fills a batch up to the byte limit, always keeping at least one event.
    /// </summary>
    /// <remarks>
    /// A single event larger than the limit still goes on its own, because
    /// dropping it from every batch forever would strand the whole queue behind
    /// an event that can never be sent.
    /// </remarks>
    private static List<OutboxEvent> TakeWhileUnderByteLimit(List<OutboxEvent> due)
    {
        List<OutboxEvent> batch = [];
        int bytes = 0;

        foreach (OutboxEvent queued in due)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(queued.PayloadJson);

            if (batch.Count > 0 && bytes + size > MaxBatchBytes)
            {
                break;
            }

            batch.Add(queued);
            bytes += size;
        }

        return batch;
    }

    private SyncUploadOutcome Record(
        List<OutboxEvent> batch,
        SyncPushResponse response,
        DateTimeOffset now)
    {
        Dictionary<Guid, SyncEventResult> byEvent = response.Results.ToDictionary(r => r.EventId);
        int accepted = 0;
        int deferred = 0;
        int escalated = 0;

        foreach (OutboxEvent queued in batch)
        {
            if (!byEvent.TryGetValue(queued.EventId.Value, out SyncEventResult? result))
            {
                // The server answered the batch but not this event. Treat it as
                // unsent rather than assume either way: the next attempt gets
                // the original verdict back, because the identifier is the same.
                escalated += ScheduleRetry(queued, now, "sync.no_result_for_event");
                continue;
            }

            switch (result.Outcome)
            {
                case SyncOutcome.Accepted:
                case SyncOutcome.Duplicate:
                    queued.MarkAnswered(OutboxStatus.Synchronized, null, result.ServerDocumentNumber);
                    accepted++;
                    break;

                case SyncOutcome.RequiresReview:
                    queued.MarkAnswered(OutboxStatus.RequiresReview, result.ErrorCode, result.Message);
                    escalated++;
                    break;

                case SyncOutcome.Conflict:
                    queued.MarkAnswered(OutboxStatus.Conflict, result.ErrorCode, result.Message);
                    escalated++;
                    break;

                case SyncOutcome.Rejected:
                    // Never retried: the answer would not change. Never dropped
                    // either — a refused event is exactly the one somebody has
                    // to look at.
                    queued.MarkAnswered(OutboxStatus.Rejected, result.ErrorCode, result.Message);
                    escalated++;
                    break;

                case SyncOutcome.Deferred:
                default:
                    escalated += ScheduleRetry(queued, now, result.ErrorCode ?? "sync.deferred");
                    deferred++;
                    break;
            }
        }

        return new SyncUploadOutcome(batch.Count, accepted, deferred, escalated, Unreachable: false);
    }

    private int ScheduleRetries(List<OutboxEvent> batch, DateTimeOffset now, string error)
    {
        int escalated = 0;

        foreach (OutboxEvent queued in batch)
        {
            escalated += ScheduleRetry(queued, now, error);
        }

        return escalated;
    }

    /// <summary>Schedules the next attempt, or gives up and escalates.</summary>
    /// <returns>1 when the event was escalated, 0 otherwise.</returns>
    private int ScheduleRetry(OutboxEvent queued, DateTimeOffset now, string error)
    {
        if (queued.AttemptCount >= policy.MaxAttempts)
        {
            queued.MarkRetriesExhausted(error);
            return 1;
        }

        queued.MarkAttemptFailed(now, now + policy.DelayFor(queued.AttemptCount), error);
        return 0;
    }
}
