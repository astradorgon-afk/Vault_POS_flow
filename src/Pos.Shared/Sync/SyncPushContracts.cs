using System.Text.Json.Serialization;

namespace Pos.Shared.Sync;

/// <summary>What the server decided about one uploaded event.</summary>
/// <remarks>
/// <para>
/// There is one of these per event and never a single batch-level verdict: a
/// batch is a transport convenience, and a device has to know which of its
/// events landed so it can stop retrying exactly those.
/// </para>
/// <para>
/// It goes on the wire by name, as OFFLINE_SYNC.md §3 documents it. The
/// converter is on the type rather than left to the host's serializer options,
/// because the same contract is read by a device client that does not share
/// them, and a verdict that silently became <c>3</c> would be a protocol break
/// nobody notices until a register stops retrying the wrong events.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SyncOutcome>))]
public enum SyncOutcome
{
    /// <summary>Applied for the first time.</summary>
    Accepted = 0,

    /// <summary>Already applied; the original result is replayed unchanged.</summary>
    Duplicate = 1,

    /// <summary>Not applied yet: an earlier event in this device's sequence is missing.</summary>
    Deferred = 2,

    /// <summary>Refused. The device must not retry this event unchanged.</summary>
    Rejected = 3,

    /// <summary>Recorded, but a person has to look at it before it takes effect.</summary>
    RequiresReview = 4,

    /// <summary>The server's state disagrees with the device's.</summary>
    Conflict = 5,
}

/// <summary>One business event a device is uploading.</summary>
/// <param name="EventId">Generated once on the device and never regenerated.</param>
/// <param name="DeviceSequence">This device's gapless, strictly increasing order.</param>
/// <param name="Type">The kind of event, as the device named it.</param>
/// <param name="OccurredAtUtc">The device clock when the business event happened.</param>
/// <param name="PayloadJson">The canonical payload; property order is stable.</param>
/// <param name="PayloadHash">Base64 SHA-256 of <paramref name="PayloadJson"/>.</param>
public sealed record SyncPushEvent(
    Guid EventId,
    long DeviceSequence,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string PayloadJson,
    string PayloadHash);

/// <summary>One batch of events from one device.</summary>
/// <param name="DeviceId">The device uploading.</param>
/// <param name="BatchId">The batch, for the idempotency key.</param>
/// <param name="ClientSentAtUtc">The device clock at send.</param>
/// <param name="DeviceUptimeTicks">Monotonic uptime, as clock-tamper evidence.</param>
/// <param name="Events">The events, in device-sequence order.</param>
public sealed record SyncPushRequest(
    Guid DeviceId,
    Guid BatchId,
    DateTimeOffset ClientSentAtUtc,
    long DeviceUptimeTicks,
    IReadOnlyList<SyncPushEvent> Events);

/// <summary>What happened to one event.</summary>
/// <param name="EventId">The event.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="ServerDocumentNumber">The number the server holds, where the event created a document.</param>
/// <param name="AppliedAtUtc">When it took effect; for a duplicate, when it first did.</param>
/// <param name="ErrorCode">The stable code for a refusal.</param>
/// <param name="Message">What went wrong, safe to show.</param>
public sealed record SyncEventResult(
    Guid EventId,
    SyncOutcome Outcome,
    string? ServerDocumentNumber = null,
    DateTimeOffset? AppliedAtUtc = null,
    string? ErrorCode = null,
    string? Message = null);

/// <summary>The server's answer to one batch.</summary>
/// <param name="ServerReceivedAtUtc">When the server took the batch.</param>
/// <param name="ClockSkewSeconds">
/// How far the device's clock is from the server's, positive when the device is
/// ahead. Reported rather than corrected: the device decides what to do about
/// it, and a silent adjustment would hide a tampered clock.
/// </param>
/// <param name="Results">One result per event, in the order they were sent.</param>
public sealed record SyncPushResponse(
    DateTimeOffset ServerReceivedAtUtc,
    double ClockSkewSeconds,
    IReadOnlyList<SyncEventResult> Results);
