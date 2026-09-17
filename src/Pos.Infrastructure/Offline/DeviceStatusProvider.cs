using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Shared.Devices;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Reports whether head office is reachable. It is a port because reachability
/// is a platform question — a network interface on Windows, a radio on Android —
/// and infrastructure must not answer it.
/// </summary>
public interface IDeviceConnectivityProbe
{
    /// <summary>Gets the current connectivity.</summary>
    DeviceConnectivityState Current { get; }
}

/// <summary>
/// A probe for a device that has no network adapter wired up yet. It reports
/// offline, which is the safe answer: the register keeps working and the banner
/// tells the truth about uploads not leaving.
/// </summary>
public sealed class AssumeOfflineConnectivityProbe : IDeviceConnectivityProbe
{
    /// <inheritdoc />
    public DeviceConnectivityState Current => DeviceConnectivityState.Offline;
}

/// <summary>
/// Builds the status a cashier is shown (OFFLINE_SYNC.md §1, POS.md §6).
/// </summary>
/// <remarks>
/// <para>
/// Everything it reports is a state, never a mechanism. The result type lives in
/// <c>Pos.Shared</c>, which references nothing, so there is no field on it that
/// could carry the database path, the cipher settings, the feed position or a
/// server address even by accident.
/// </para>
/// <para>
/// It never throws for a device that is simply not ready. An unopened store, an
/// unenrolled register and a user with no cached authority are all ordinary
/// states with something useful to say, and a status screen that threw on them
/// would go blank exactly when someone needed to read it.
/// </para>
/// </remarks>
/// <param name="database">The encrypted device store.</param>
/// <param name="connectivity">The platform's reachability probe.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceStatusProvider(
    DeviceDatabaseInitializer database,
    IDeviceConnectivityProbe connectivity,
    ISystemClock clock)
{
    /// <summary>
    /// How long before expiry the device starts asking to be reconnected. A
    /// shift is eight hours, so a warning this wide means whoever is on the till
    /// when authority runs out has already been told.
    /// </summary>
    public static readonly TimeSpan ExpiringSoonWindow = TimeSpan.FromHours(8);

    /// <summary>Reads the current status.</summary>
    /// <param name="signedInUser">The signed-in user, or null when nobody is.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What to show.</returns>
    public async Task<DeviceStatusView> GetAsync(
        UserId? signedInUser,
        CancellationToken cancellationToken = default)
    {
        if (!database.IsOpen)
        {
            return new DeviceStatusView(
                DeviceStorageState.NotReady,
                DeviceEnrolmentState.NotEnrolled,
                DeviceCode: null,
                LocationName: null,
                connectivity.Current,
                DeviceSyncState.NeverSynchronised,
                LastSynchronisedUtc: null,
                DeviceAuthorityState.None,
                AuthorityExpiresUtc: null);
        }

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        string? locationName = profile is null
            ? null
            : await context.Locations
                .AsNoTracking()
                .Where(l => l.Id == profile.LocationId)
                .Select(l => l.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        DateTimeOffset? lastSynchronised = await context.SyncCursors
            .AsNoTracking()
            .Where(c => c.Feed == ChangeFeedApplier.ChangeFeed)
            .Select(c => (DateTimeOffset?)c.AdvancedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        (DeviceAuthorityState authority, DateTimeOffset? expiresAt) =
            await ReadAuthorityAsync(context, signedInUser, cancellationToken).ConfigureAwait(false);

        // Still on its way versus stuck. A refused, flagged, conflicted or
        // exhausted event has reached head office's attention one way or another
        // and will not move on its own; counting it as "unsent" would tell a
        // cashier to wait for a line that is already back.
        int unsent = await context.Outbox
            .AsNoTracking()
            .CountAsync(
                e => e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.Sending,
                cancellationToken)
            .ConfigureAwait(false);

        int escalated = await context.Outbox
            .AsNoTracking()
            .CountAsync(
                e => e.Status == OutboxStatus.Failed
                     || e.Status == OutboxStatus.Rejected
                     || e.Status == OutboxStatus.RequiresReview
                     || e.Status == OutboxStatus.Conflict,
                cancellationToken)
            .ConfigureAwait(false);

        return new DeviceStatusView(
            DeviceStorageState.Ready,
            profile is null ? DeviceEnrolmentState.NotEnrolled : DeviceEnrolmentState.Enrolled,
            profile?.ShortCode,
            locationName,
            connectivity.Current,
            lastSynchronised is null ? DeviceSyncState.NeverSynchronised : DeviceSyncState.Synchronised,
            lastSynchronised,
            authority,
            expiresAt,
            unsent,
            escalated);
    }

    private async Task<(DeviceAuthorityState State, DateTimeOffset? ExpiresAt)> ReadAuthorityAsync(
        PosDeviceDbContext context,
        UserId? signedInUser,
        CancellationToken cancellationToken)
    {
        if (signedInUser is not { } userId)
        {
            return (DeviceAuthorityState.None, null);
        }

        // The earliest expiry in the snapshot, because that is when the user
        // starts losing permissions, not when the last one goes.
        DateTimeOffset? expiresAt = await context.PermissionSnapshots
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.ExpiresAtUtc)
            .Select(s => (DateTimeOffset?)s.ExpiresAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expiresAt is not { } expiry)
        {
            return (DeviceAuthorityState.None, null);
        }

        DateTimeOffset now = clock.UtcNow;

        DeviceAuthorityState state = expiry <= now ? DeviceAuthorityState.Expired
            : expiry - now <= ExpiringSoonWindow ? DeviceAuthorityState.ExpiringSoon
            : DeviceAuthorityState.Active;

        return (state, expiry);
    }
}
