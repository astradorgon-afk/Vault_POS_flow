using Pos.Domain.Common;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// The record that makes uploading exactly-once. It is written in the same
/// transaction as the effect it describes, so there is no window in which a sale
/// is committed but its idempotency record is not (OFFLINE_SYNC.md §3.1).
/// </summary>
public sealed class ProcessedEvent
{
    private ProcessedEvent() { Type = string.Empty; PayloadHash = []; Outcome = string.Empty; }

    /// <summary>Records one processed event.</summary>
    internal ProcessedEvent(
        EventId eventId,
        DeviceId deviceId,
        long deviceSequence,
        string type,
        byte[] payloadHash,
        string outcome,
        string? resultJson,
        DateTimeOffset appliedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        EventId = eventId;
        DeviceId = deviceId;
        DeviceSequence = deviceSequence;
        Type = type;
        PayloadHash = payloadHash;
        Outcome = outcome;
        ResultJson = resultJson;
        AppliedAtUtc = appliedAtUtc;
    }

    /// <summary>Gets the device-generated event identifier.</summary>
    public EventId EventId { get; private init; }

    /// <summary>Gets the device that produced it.</summary>
    public DeviceId DeviceId { get; private init; }

    /// <summary>Gets its place in that device's sequence.</summary>
    public long DeviceSequence { get; private init; }

    /// <summary>Gets the kind of event.</summary>
    public string Type { get; private init; }

    /// <summary>
    /// Gets the SHA-256 of the payload as first accepted. A repeat carrying a
    /// different hash is tampering, not an update (ADR-0007).
    /// </summary>
    public byte[] PayloadHash { get; private init; }

    /// <summary>Gets the verdict, replayed verbatim on a repeat.</summary>
    public string Outcome { get; private init; }

    /// <summary>Gets the original result, replayed verbatim on a repeat.</summary>
    public string? ResultJson { get; private init; }

    /// <summary>Gets when it first took effect.</summary>
    public DateTimeOffset AppliedAtUtc { get; private init; }
}

/// <summary>
/// How far through one device's sequence the server has accepted. It is what
/// turns a gap into a deferral instead of an out-of-order apply.
/// </summary>
public sealed class SyncCheckpoint
{
    private SyncCheckpoint() { }

    /// <summary>Creates a checkpoint for a device.</summary>
    internal SyncCheckpoint(DeviceId deviceId, long lastAcceptedDeviceSequence, DateTimeOffset updatedAtUtc)
    {
        DeviceId = deviceId;
        LastAcceptedDeviceSequence = lastAcceptedDeviceSequence;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Gets the device.</summary>
    public DeviceId DeviceId { get; private init; }

    /// <summary>Gets the highest sequence accepted without a gap behind it.</summary>
    public long LastAcceptedDeviceSequence { get; private set; }

    /// <summary>Gets when it last moved.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Advances the checkpoint. It never moves backwards.</summary>
    /// <param name="deviceSequence">The newly accepted sequence.</param>
    /// <param name="updatedAtUtc">Now.</param>
    internal void Advance(long deviceSequence, DateTimeOffset updatedAtUtc)
    {
        if (deviceSequence <= LastAcceptedDeviceSequence)
        {
            return;
        }

        LastAcceptedDeviceSequence = deviceSequence;
        UpdatedAtUtc = updatedAtUtc;
    }
}

/// <summary>
/// One change a device needs to hear about, in the order the server decided it
/// happened (OFFLINE_SYNC.md §5).
/// </summary>
/// <remarks>
/// <para>
/// The feed is append-only and read by cursor: a device asks for everything
/// after the sequence it last stored. That only works if sequences become
/// visible in order — a row numbered 40 committing before one numbered 39 would
/// let a device store 40 and never see 39 again. A database identity column does
/// not promise that, so the number is allocated from a single counter row inside
/// the writing transaction, which serialises feed appends against each other.
/// Master data changes rarely; sales do not touch this table at all.
/// </para>
/// <para>
/// The payload is stored as the device will receive it, so serving a page is a
/// read rather than a re-derivation from rows that have since changed again. A
/// device that asks for a change from last week gets what was true last week.
/// </para>
/// </remarks>
public sealed class ChangeFeedEntry
{
    private ChangeFeedEntry()
    {
        Kind = string.Empty;
        PayloadJson = string.Empty;
    }

    /// <summary>Records one change.</summary>
    /// <param name="sequence">Its place in the feed, allocated from the counter.</param>
    /// <param name="kind">The change type, as the device names it.</param>
    /// <param name="locationScopeId">The location it concerns, or null for every location.</param>
    /// <param name="payloadJson">The change, as the device will receive it.</param>
    /// <param name="recordedAtUtc">When the server wrote it.</param>
    internal ChangeFeedEntry(
        long sequence,
        string kind,
        Guid? locationScopeId,
        string payloadJson,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);

        Sequence = sequence;
        Kind = kind;
        LocationScopeId = locationScopeId;
        PayloadJson = payloadJson;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Gets the feed position; strictly ascending and gapless.</summary>
    public long Sequence { get; private init; }

    /// <summary>Gets the change type, matching the device's change records.</summary>
    public string Kind { get; private init; }

    /// <summary>
    /// Gets the location this change concerns, or null when it concerns every
    /// location. A device is served the global changes and its own.
    /// </summary>
    public Guid? LocationScopeId { get; private init; }

    /// <summary>Gets the change exactly as a device will receive it.</summary>
    public string PayloadJson { get; private init; }

    /// <summary>Gets when the server recorded it.</summary>
    public DateTimeOffset RecordedAtUtc { get; private init; }
}

/// <summary>
/// The counter the change feed's order comes from.
/// </summary>
/// <remarks>
/// One row, incremented inside the transaction that appends to the feed. It is
/// deliberately not a database identity column: identities can be handed out in
/// one order and committed in another, and a device reading "everything after my
/// cursor" would store the higher number and never see the lower one again.
/// Serialising on a single row costs concurrency on master-data writes, which
/// are rare; a sale never touches it.
/// </remarks>
public sealed class ChangeFeedSequence
{
    private ChangeFeedSequence() => Name = string.Empty;

    /// <summary>Gets the counter's name; there is one.</summary>
    public string Name { get; private init; }

    /// <summary>Gets the next value to hand out.</summary>
    public long NextValue { get; private init; }
}
