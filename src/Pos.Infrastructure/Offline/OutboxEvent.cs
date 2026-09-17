using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>The kind of business event a device uploads.</summary>
/// <remarks>
/// A device uploads business events, not row changes: the server replays the
/// event through the same use case it would have run online. The values are
/// persisted, so they are assigned explicitly and never renumbered.
/// </remarks>
public enum SyncEventType
{
    /// <summary>A cashier opened a shift.</summary>
    ShiftOpened = 1,

    /// <summary>A cashier suspended a shift.</summary>
    ShiftSuspended = 2,

    /// <summary>A cashier resumed a suspended shift.</summary>
    ShiftResumed = 3,

    /// <summary>A sale was completed and paid for.</summary>
    SaleCompleted = 4,
}

/// <summary>Where one outbox event has got to.</summary>
public enum OutboxStatus
{
    /// <summary>Waiting to be sent.</summary>
    Pending = 0,

    /// <summary>Handed to the server; the outcome is not known yet.</summary>
    Sending = 1,

    /// <summary>The server accepted it. Kept for the audit trail.</summary>
    Synchronized = 2,

    /// <summary>Retries were exhausted. Kept forever and escalated, never dropped.</summary>
    Failed = 3,

    /// <summary>The server accepted it but a person must look at it.</summary>
    RequiresReview = 4,

    /// <summary>The server's state disagrees with the device's.</summary>
    Conflict = 5,
}

/// <summary>
/// One business event waiting to reach head office (OFFLINE_SYNC.md §2.1).
/// </summary>
/// <remarks>
/// <para>
/// The event identifier is created once, in the same local transaction as the
/// business rows it describes, and is never regenerated. That is the whole basis
/// of safe retries: a request that timed out after the server committed is
/// recognised on the next attempt rather than applied twice.
/// </para>
/// <para>
/// <see cref="DeviceUptimeTicks"/> is monotonic and survives a clock change, so
/// a device whose wall clock was moved backwards still produces events in an
/// order the server can see through.
/// </para>
/// </remarks>
public sealed class OutboxEvent
{
    private OutboxEvent()
    {
        PayloadJson = string.Empty;
        PayloadHash = [];
    }

    /// <summary>Creates a pending event.</summary>
    internal OutboxEvent(
        EventId eventId,
        long deviceSequence,
        SyncEventType type,
        string payloadJson,
        DeviceId deviceId,
        UserId userId,
        LocationId locationId,
        DateTimeOffset occurredAtUtc,
        long deviceUptimeTicks,
        CorrelationId correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceSequence);

        EventId = eventId;
        DeviceSequence = deviceSequence;
        Type = type;
        PayloadJson = payloadJson;
        PayloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        DeviceId = deviceId;
        UserId = userId;
        LocationId = locationId;
        OccurredAtUtc = occurredAtUtc;
        DeviceUptimeTicks = deviceUptimeTicks;
        CorrelationId = correlationId;
        Status = OutboxStatus.Pending;
    }

    /// <summary>Gets the business event identifier, generated once and never again.</summary>
    public EventId EventId { get; private init; }

    /// <summary>Gets this device's gapless, strictly increasing sequence number.</summary>
    public long DeviceSequence { get; private init; }

    /// <summary>Gets the kind of event.</summary>
    public SyncEventType Type { get; private init; }

    /// <summary>Gets the canonical JSON payload, with properties in a stable order.</summary>
    public string PayloadJson { get; private init; }

    /// <summary>Gets the SHA-256 of <see cref="PayloadJson"/>.</summary>
    public byte[] PayloadHash { get; private init; }

    /// <summary>Gets the device that produced the event.</summary>
    public DeviceId DeviceId { get; private init; }

    /// <summary>Gets the user who caused it.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets the location it applies to.</summary>
    public LocationId LocationId { get; private init; }

    /// <summary>Gets the device clock at creation.</summary>
    public DateTimeOffset OccurredAtUtc { get; private init; }

    /// <summary>Gets the monotonic uptime at creation, as clock-tamper evidence.</summary>
    public long DeviceUptimeTicks { get; private init; }

    /// <summary>Gets the correlation identifier of the session that produced it.</summary>
    public CorrelationId CorrelationId { get; private init; }

    /// <summary>Gets where the event has got to.</summary>
    public OutboxStatus Status { get; private set; }

    /// <summary>Gets how many times it has been attempted.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Gets when it was last attempted.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    /// <summary>Gets when it may next be attempted.</summary>
    public DateTimeOffset? NextRetryAtUtc { get; private set; }

    /// <summary>Gets the last failure, for the sync-failure queue.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets the server's last response, kept verbatim for escalation.</summary>
    public string? ServerResponseJson { get; private set; }
}

/// <summary>
/// Serializes a payload so the same event always produces the same bytes, and
/// therefore the same hash, on any device and any runtime.
/// </summary>
/// <remarks>
/// <para>
/// The server compares a repeated event identifier against the hash of what it
/// first stored, and treats a different hash as tampering rather than as an
/// update (ADR-0007). That comparison is only meaningful if the bytes are
/// canonical: <see cref="JsonSerializer"/> writes properties in declaration
/// order, so adding or moving a property on a payload type would silently change
/// every hash and turn honest retries into tamper reports.
/// </para>
/// <para>
/// So the payload is written, re-read as a tree, and re-emitted with object
/// properties sorted by ordinal name at every depth, with no whitespace. Array
/// order is left alone: it is data, not layout.
/// </para>
/// </remarks>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Serializes a payload canonically.</summary>
    /// <typeparam name="TPayload">The payload type.</typeparam>
    /// <param name="payload">The payload.</param>
    /// <returns>Canonical JSON.</returns>
    public static string Serialize<TPayload>(TPayload payload)
    {
        JsonNode? tree = JsonSerializer.SerializeToNode(payload, WriteOptions);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            Write(tree, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(JsonNode? node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonObject obj:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in
                         obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? item in array)
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                node.WriteTo(writer);
                break;
        }
    }
}
