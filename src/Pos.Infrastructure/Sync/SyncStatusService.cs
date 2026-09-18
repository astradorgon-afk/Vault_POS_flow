using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>Builds the authenticated device's sync-health summary.</summary>
public sealed class SyncStatusService(
    PosDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock)
{
    public async Task<SyncStatusResponse> GetAsync(CancellationToken cancellationToken)
    {
        DeviceId? deviceId = currentUser.DeviceId;
        if (deviceId is null || currentUser.UserId is null)
        {
            return SyncStatusResponse.Refused("sync.device_binding", "A device-bound session is required.");
        }

        Device? device = await context.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (device is null)
        {
            return SyncStatusResponse.Refused("sync.device_unknown", "The synchronization device was not found.");
        }

        SyncDeviceCheckpoint? checkpoint = await context.SyncDeviceCheckpoints
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.DeviceId == deviceId.Value, cancellationToken)
            .ConfigureAwait(false);
        int openFailures = await context.SyncFailures
            .AsNoTracking()
            .CountAsync(f => f.DeviceId == deviceId.Value
                             && f.Status != SyncFailureStatus.Resolved
                             && f.Status != SyncFailureStatus.Dismissed,
                cancellationToken)
            .ConfigureAwait(false);
        long currentFeedCursor = await context.SyncChangeLog
            .AsNoTracking()
            .Select(e => (long?)e.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new SyncStatusResponse(
            clock.UtcNow,
            device.Id.Value,
            device.IsOperational,
            checkpoint?.LastAcceptedSequence ?? 0,
            openFailures,
            currentFeedCursor,
            null,
            null);
    }
}

public sealed record SyncStatusResponse(
    DateTimeOffset ServerTimeUtc,
    Guid DeviceId,
    bool DeviceOperational,
    long LastAcceptedSequence,
    int OpenFailureCount,
    long CurrentFeedCursor,
    string? ErrorCode,
    string? Message)
{
    public static SyncStatusResponse Refused(string code, string message)
        => new(DateTimeOffset.UtcNow, Guid.Empty, false, 0, 0, 0, code, message);
}
