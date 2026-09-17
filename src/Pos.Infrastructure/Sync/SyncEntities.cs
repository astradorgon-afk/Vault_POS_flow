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
