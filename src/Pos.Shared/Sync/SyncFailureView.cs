namespace Pos.Shared.Sync;

/// <summary>
/// One uploaded event that head office turned away or set aside.
/// </summary>
/// <remarks>
/// It names the register by its short code — the one printed on its receipts —
/// so somebody reading this can walk to it. There is nothing here about where
/// the register keeps its data or how it connects.
/// </remarks>
/// <param name="EventId">The event, which a retry request names.</param>
/// <param name="DeviceId">The register that produced it.</param>
/// <param name="DeviceCode">That register's short code.</param>
/// <param name="LocationId">The store it belongs to.</param>
/// <param name="DeviceSequence">Its place in that register's queue.</param>
/// <param name="Type">The kind of business event.</param>
/// <param name="Outcome">What the server decided.</param>
/// <param name="Detail">What the server said about it, in its own words.</param>
/// <param name="DecidedAtUtc">When the server decided.</param>
public sealed record SyncFailureView(
    Guid EventId,
    Guid DeviceId,
    string DeviceCode,
    Guid LocationId,
    long DeviceSequence,
    string Type,
    string Outcome,
    string? Detail,
    DateTimeOffset DecidedAtUtc);
