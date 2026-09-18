using Pos.Domain.Common;

namespace Pos.Application.Notifications;

/// <summary>Reads the uploaded events head office turned away or set aside.</summary>
public interface ISyncFailureAlertRepository
{
    /// <summary>Finds events decided since the given instant that still need a person.</summary>
    /// <param name="sinceUtc">The oldest decision instant to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The failures, oldest first.</returns>
    Task<IReadOnlyList<SyncFailureAlert>> GetRecentAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// One uploaded event that needs somebody, ready to be announced.
/// </summary>
/// <remarks>
/// It names the register by the short code printed on its receipts, because the
/// person who acts on this walks to a till rather than to a row in a table.
/// </remarks>
/// <param name="EventId">The event, which a retry request names.</param>
/// <param name="DeviceCode">The register's short code.</param>
/// <param name="LocationId">The store the register belongs to.</param>
/// <param name="DeviceSequence">Its place in that register's queue.</param>
/// <param name="Type">The kind of business event.</param>
/// <param name="Outcome">What the server decided.</param>
/// <param name="Detail">What the server said about it, in its own words.</param>
/// <param name="DecidedAtUtc">When the server decided.</param>
public sealed record SyncFailureAlert(
    EventId EventId,
    string DeviceCode,
    LocationId LocationId,
    long DeviceSequence,
    string Type,
    string Outcome,
    string? Detail,
    DateTimeOffset DecidedAtUtc);
