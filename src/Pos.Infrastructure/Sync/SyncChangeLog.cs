using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>An append-only server change-feed row.</summary>
public sealed class SyncChangeLogEntry
{
    private SyncChangeLogEntry()
    {
        ChangeType = string.Empty;
        PayloadJson = string.Empty;
    }

    /// <summary>Creates a feed row whose sequence is assigned by the database.</summary>
    public SyncChangeLogEntry(
        string changeType,
        string payloadJson,
        LocationId? locationScopeId,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        ChangeType = changeType;
        PayloadJson = payloadJson;
        LocationScopeId = locationScopeId;
        RecordedAtUtc = recordedAtUtc;
    }

    public long Sequence { get; private set; }
    public string ChangeType { get; private init; }
    public string PayloadJson { get; private init; }
    public LocationId? LocationScopeId { get; private init; }
    public DateTimeOffset RecordedAtUtc { get; private init; }
}

/// <summary>Appends typed master-data changes to the server feed.</summary>
public interface IChangeFeedPublisher
{
    /// <summary>Appends one change inside the caller's transaction.</summary>
    Task<long> PublishAsync<TPayload>(
        string changeType,
        TPayload payload,
        LocationId? locationScopeId,
        CancellationToken cancellationToken);
}

/// <summary>EF-backed change-feed publisher.</summary>
public sealed class ChangeFeedPublisher(
    PosDbContext context,
    ISystemClock clock) : IChangeFeedPublisher
{
    public async Task<long> PublishAsync<TPayload>(
        string changeType,
        TPayload payload,
        LocationId? locationScopeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeType);
        ArgumentNullException.ThrowIfNull(payload);

        SyncChangeLogEntry entry = new(
            changeType,
            System.Text.Json.JsonSerializer.Serialize(payload),
            locationScopeId,
            clock.UtcNow);
        context.SyncChangeLog.Add(entry);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry.Sequence;
    }
}

/// <summary>Reads the device-scoped server change feed.</summary>
public sealed class SyncPullService(
    PosDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock)
{
    /// <summary>Returns one scoped page after the supplied cursor.</summary>
    public async Task<SyncPullResponse> PullAsync(
        long cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (cursor < 0)
        {
            return SyncPullResponse.Refused("sync.cursor_invalid", "The feed cursor cannot be negative.");
        }

        if (limit is < 1 or > 500)
        {
            return SyncPullResponse.Refused("sync.limit_invalid", "The feed limit must be between 1 and 500.");
        }

        DeviceId? deviceId = currentUser.DeviceId;
        if (deviceId is null || currentUser.UserId is null)
        {
            return SyncPullResponse.Refused("sync.device_binding", "A device-bound session is required.");
        }

        Device? device = await context.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (device is null || !device.IsOperational)
        {
            return SyncPullResponse.Refused("sync.device_not_operational", "The device is not permitted to synchronize.");
        }

        List<SyncChangeLogEntry> entries = await context.SyncChangeLog
            .AsNoTracking()
            .Where(e => e.Sequence > cursor
                        && (e.LocationScopeId == null || e.LocationScopeId == device.LocationId))
            .OrderBy(e => e.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        long? earliestRetainedSequence = await context.SyncChangeLog
            .AsNoTracking()
            .Where(e => e.LocationScopeId == null || e.LocationScopeId == device.LocationId)
            .Select(e => (long?)e.Sequence)
            .MinAsync(cancellationToken)
            .ConfigureAwait(false);

        if (earliestRetainedSequence is { } earliest && cursor < earliest - 1)
        {
            return SyncPullResponse.Rebaseline(cursor, earliest - 1);
        }

        long nextCursor = entries.Count > 0
            ? entries[^1].Sequence
            : await context.SyncChangeLog
                .AsNoTracking()
                .Select(e => (long?)e.Sequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false) ?? cursor;

        List<SyncPullChange> changes = [];
        foreach (SyncChangeLogEntry entry in entries)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(entry.PayloadJson);
                changes.Add(new SyncPullChange(entry.Sequence, entry.ChangeType, document.RootElement.Clone()));
            }
            catch (JsonException)
            {
                // A malformed server row must never be emitted as a successful
                // page: the client would be unable to apply it atomically.
                return SyncPullResponse.Refused("sync.feed_entry_invalid", "The server change feed contains an invalid payload.");
            }
        }

        return new SyncPullResponse(clock.UtcNow, cursor, nextCursor, changes, null, null);
    }
}

public sealed record SyncPullChange(long Sequence, string Type, JsonElement Payload);

public sealed record SyncPullResponse(
    DateTimeOffset ServerReceivedAtUtc,
    long FromCursor,
    long NextCursor,
    IReadOnlyList<SyncPullChange> Changes,
    string? ErrorCode,
    string? Message,
    bool RebaselineRequired = false,
    long? EarliestRetainedCursor = null)
{
    public static SyncPullResponse Refused(string code, string message)
        => new(DateTimeOffset.UtcNow, 0, 0, [], code, message);

    public static SyncPullResponse Rebaseline(long cursor, long earliestRetainedCursor)
        => new(
            DateTimeOffset.UtcNow,
            cursor,
            cursor,
            [],
            "sync.cursor_expired",
            "The requested feed cursor is outside the retained change-feed window; download a baseline.",
            RebaselineRequired: true,
            EarliestRetainedCursor: earliestRetainedCursor);
}
