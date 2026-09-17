using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Applies the business effect of one uploaded event. One implementation per
/// event type; the processor owns idempotency and ordering, an applier owns
/// only what the event means.
/// </summary>
public interface ISyncEventApplier
{
    /// <summary>Gets the event type this applier handles, as the device names it.</summary>
    string EventType { get; }

    /// <summary>Applies the event against current server state.</summary>
    /// <param name="deviceId">The device that produced it.</param>
    /// <param name="eventId">
    /// The device's identifier for this business event, stable across every
    /// retry. It is the key the whole protocol already deduplicates on, so an
    /// applier that posts to the ledger passes it straight through rather than
    /// minting a second idempotency key that a retry would not reproduce.
    /// </param>
    /// <param name="payloadJson">The canonical payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What to report back, and what to store for a replay.</returns>
    Task<SyncApplyResult> ApplyAsync(
        DeviceId deviceId,
        EventId eventId,
        string payloadJson,
        CancellationToken cancellationToken);
}

/// <summary>What applying one event produced.</summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="ServerDocumentNumber">The document the server now holds, if any.</param>
/// <param name="ErrorCode">The stable refusal code, for anything but acceptance.</param>
/// <param name="Message">What went wrong, safe to show.</param>
public sealed record SyncApplyResult(
    SyncOutcome Outcome,
    string? ServerDocumentNumber = null,
    string? ErrorCode = null,
    string? Message = null)
{
    /// <summary>The event took effect.</summary>
    /// <param name="documentNumber">The document number the server holds.</param>
    /// <returns>An accepted result.</returns>
    public static SyncApplyResult Accepted(string? documentNumber = null)
        => new(SyncOutcome.Accepted, documentNumber);

    /// <summary>The event was refused and must not be retried unchanged.</summary>
    /// <param name="errorCode">The stable code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>A rejected result.</returns>
    public static SyncApplyResult Rejected(string errorCode, string message)
        => new(SyncOutcome.Rejected, null, errorCode, message);

    /// <summary>
    /// The event took effect and a person has to look at it.
    /// </summary>
    /// <remarks>
    /// This is not a softer refusal. It is for the events that must land
    /// whatever the server now thinks — the money already changed hands, the
    /// goods already left the shelf — but that a person would want to know
    /// about, such as a sale rung up by a cashier whose authority was withdrawn
    /// while the device was offline (OFFLINE_SYNC.md §7).
    /// </remarks>
    /// <param name="documentNumber">The document number the server holds.</param>
    /// <param name="errorCode">The stable code for what needs looking at.</param>
    /// <param name="message">What to tell whoever looks.</param>
    /// <returns>A result that applied but is flagged.</returns>
    public static SyncApplyResult RequiresReview(string? documentNumber, string errorCode, string message)
        => new(SyncOutcome.RequiresReview, documentNumber, errorCode, message);
}

/// <summary>
/// The upload endpoint's engine: exactly-once, in order, one verdict per event
/// (OFFLINE_SYNC.md §3.1).
/// </summary>
/// <remarks>
/// <para>
/// Each event is processed in its own transaction, so one refusal does not
/// discard the events that already landed — a batch is a transport convenience,
/// not a unit of work. The idempotency record is written in the same transaction
/// as the effect, which is what closes the window where a sale could be
/// committed without the record that stops it being applied twice.
/// </para>
/// <para>
/// A repeated event identifier carrying a different payload hash is treated as
/// tampering rather than as an update (ADR-0007): the original result is not
/// replayed and nothing is applied. That check is only meaningful because the
/// device serializes payloads canonically, so an honest retry always hashes the
/// same.
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="appliers">One applier per event type this server understands.</param>
public sealed class SyncPushProcessor(
    PosDbContext context,
    ISystemClock clock,
    IEnumerable<ISyncEventApplier> appliers)
{
    private readonly Dictionary<string, ISyncEventApplier> byType =
        appliers.ToDictionary(a => a.EventType, StringComparer.Ordinal);

    /// <summary>Processes one batch and answers for every event in it.</summary>
    /// <param name="request">The batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per event, in the order sent.</returns>
    public async Task<SyncPushResponse> ProcessAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset receivedAt = clock.UtcNow;
        DeviceId deviceId = new(request.DeviceId);
        List<SyncEventResult> results = [];

        // Reported, never corrected: the device decides what to do about its own
        // clock, and a silent adjustment here would hide a tampered one.
        double skewSeconds = (request.ClientSentAtUtc - receivedAt).TotalSeconds;

        bool deferRest = false;

        foreach (SyncPushEvent uploaded in request.Events.OrderBy(e => e.DeviceSequence))
        {
            if (deferRest)
            {
                // Everything behind a gap waits with it: applying a later event
                // first would let the server act on a state the device never had.
                results.Add(new SyncEventResult(uploaded.EventId, SyncOutcome.Deferred));
                continue;
            }

            SyncEventResult result = await ProcessOneAsync(deviceId, uploaded, cancellationToken)
                .ConfigureAwait(false);

            results.Add(result);
            deferRest = result.Outcome == SyncOutcome.Deferred;
        }

        return new SyncPushResponse(receivedAt, skewSeconds, results);
    }

    private async Task<SyncEventResult> ProcessOneAsync(
        DeviceId deviceId,
        SyncPushEvent uploaded,
        CancellationToken cancellationToken)
    {
        // Every event is its own unit of work, so it starts from a clean change
        // tracker. Two events touching the same row in one batch — a till locked
        // and unlocked again — would otherwise collide on the second, because an
        // applier reads untracked and attaches what it read.
        context.ChangeTracker.Clear();

        EventId eventId = new(uploaded.EventId);
        byte[] uploadedHash = SHA256.HashData(Encoding.UTF8.GetBytes(uploaded.PayloadJson));

        // 1. Idempotency, before anything is read or applied.
        ProcessedEvent? already = await context.ProcessedEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken)
            .ConfigureAwait(false);

        if (already is not null)
        {
            if (!already.PayloadHash.SequenceEqual(uploadedHash))
            {
                return new SyncEventResult(
                    uploaded.EventId,
                    SyncOutcome.Rejected,
                    ErrorCode: "sync.idempotency_key_reuse",
                    Message: "This event identifier has already been processed with different content.");
            }

            return new SyncEventResult(
                uploaded.EventId,
                Enum.Parse<SyncOutcome>(already.Outcome),
                already.ResultJson,
                already.AppliedAtUtc);
        }

        // 2. Ordering. A gap means the device has an earlier event still in
        //    flight, so this one waits rather than jumping it.
        // Tracked explicitly: the server context reads no-tracking by default,
        // and an advance applied to a detached checkpoint saves nothing. The
        // symptom is silent and total — the first batch lands, and every batch
        // after it defers forever against a checkpoint frozen at one.
        SyncCheckpoint? checkpoint = await context.SyncCheckpoints
            .AsTracking()
            .FirstOrDefaultAsync(c => c.DeviceId == deviceId, cancellationToken)
            .ConfigureAwait(false);

        long lastAccepted = checkpoint?.LastAcceptedDeviceSequence ?? 0;

        if (uploaded.DeviceSequence <= lastAccepted)
        {
            // Already past this point with no record of the event: the device is
            // resending something the server accepted under another identifier,
            // which it must stop retrying.
            return new SyncEventResult(uploaded.EventId, SyncOutcome.Duplicate);
        }

        if (uploaded.DeviceSequence != lastAccepted + 1)
        {
            return new SyncEventResult(uploaded.EventId, SyncOutcome.Deferred);
        }

        if (!this.byType.TryGetValue(uploaded.Type, out ISyncEventApplier? applier))
        {
            // Deliberately without a processed-event record or a checkpoint
            // advance, unlike every other refusal. The event is not wrong — this
            // server is behind the device that sent it — so recording an answer
            // would destroy a business record that an upgraded server could
            // still apply. The device keeps retrying, and its queue stays behind
            // this event until somebody notices.
            return new SyncEventResult(
                uploaded.EventId,
                SyncOutcome.Rejected,
                ErrorCode: "sync.event_type_unsupported",
                Message: FormattableString.Invariant($"This server does not understand {uploaded.Type} events."));
        }

        // 3–5. Apply, and 6–8 record it, in one transaction.
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        SyncApplyResult applied = await applier
            .ApplyAsync(deviceId, eventId, uploaded.PayloadJson, cancellationToken)
            .ConfigureAwait(false);

        if (applied.Outcome is SyncOutcome.Rejected or SyncOutcome.Conflict)
        {
            // A refused event has no business effect, and "no effect" has to
            // mean none at all. An applier that got several steps in before
            // refusing may have written rows already, or left an audit entry
            // staged describing something that did not happen; committing the
            // verdict alongside them would make the audit trail claim otherwise.
            // So the work is thrown away and the verdict recorded on its own.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            return await RecordRefusalAsync(deviceId, uploaded, eventId, uploadedHash, applied, cancellationToken)
                .ConfigureAwait(false);
        }

        DateTimeOffset appliedAt = clock.UtcNow;

        context.ProcessedEvents.Add(new ProcessedEvent(
            eventId,
            deviceId,
            uploaded.DeviceSequence,
            uploaded.Type,
            uploadedHash,
            applied.Outcome.ToString(),
            applied.ServerDocumentNumber,
            appliedAt));

        // A refused event still advances the checkpoint. It has been answered
        // for, and leaving it behind would stall every event after it forever.
        if (checkpoint is null)
        {
            context.SyncCheckpoints.Add(new SyncCheckpoint(deviceId, uploaded.DeviceSequence, appliedAt));
        }
        else
        {
            checkpoint.Advance(uploaded.DeviceSequence, appliedAt);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SyncEventResult(
            uploaded.EventId,
            applied.Outcome,
            applied.ServerDocumentNumber,
            appliedAt,
            applied.ErrorCode,
            applied.Message);
    }

    /// <summary>
    /// Records that a refused event was answered for, in a transaction of its
    /// own, after everything the applier touched has been discarded.
    /// </summary>
    private async Task<SyncEventResult> RecordRefusalAsync(
        DeviceId deviceId,
        SyncPushEvent uploaded,
        EventId eventId,
        byte[] uploadedHash,
        SyncApplyResult applied,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Re-read: the rollback discarded the instance the checkpoint was
        // tracked on, and advancing a stale copy would save nothing.
        SyncCheckpoint? checkpoint = await context.SyncCheckpoints
            .AsTracking()
            .FirstOrDefaultAsync(c => c.DeviceId == deviceId, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset appliedAt = clock.UtcNow;

        context.ProcessedEvents.Add(new ProcessedEvent(
            eventId,
            deviceId,
            uploaded.DeviceSequence,
            uploaded.Type,
            uploadedHash,
            applied.Outcome.ToString(),
            applied.ServerDocumentNumber,
            appliedAt));

        // A refused event still advances the checkpoint. It has been answered
        // for, and leaving it behind would stall every event after it forever.
        if (checkpoint is null)
        {
            context.SyncCheckpoints.Add(new SyncCheckpoint(deviceId, uploaded.DeviceSequence, appliedAt));
        }
        else
        {
            checkpoint.Advance(uploaded.DeviceSequence, appliedAt);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SyncEventResult(
            uploaded.EventId,
            applied.Outcome,
            applied.ServerDocumentNumber,
            appliedAt,
            applied.ErrorCode,
            applied.Message);
    }
}
