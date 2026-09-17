using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Answers permission questions on a device from the cached snapshot
/// (PERMISSIONS.md §5). It is the offline counterpart of the database
/// evaluator, and it only ever narrows: a snapshot is an upper bound the server
/// re-checks at sync time, never a grant in its own right.
/// </summary>
/// <remarks>
/// <para>
/// Three things make a cached answer safe. A snapshot expires, and expiry is
/// checked at every evaluation rather than trusted to a cleanup that might not
/// have run — a device left in a drawer for a week must not still be able to
/// void sales. A grant is scoped: a location grant answers only for that
/// location, while a global grant answers anywhere, which is what confines a
/// store's staff to their store. And the permission itself must be
/// offline-capable, checked here against the catalogue rather than taken from
/// the row, so a snapshot that somehow carried <c>inventory.adjust.approve</c>
/// still cannot authorize one.
/// </para>
/// <para>
/// Nothing here consults the cached user row. An inactive user is refused at
/// sign-in; a permission check that also re-read the user would be answering a
/// different question, and the snapshot is already invalidated through the feed
/// when a user changes.
/// </para>
/// </remarks>
/// <param name="database">The encrypted device store.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceSnapshotPermissionEvaluator(
    DeviceDatabaseInitializer database,
    ISystemClock clock) : IPermissionEvaluator
{
    /// <inheritdoc />
    public async Task<bool> HasPermissionAsync(
        UserId userId,
        string permissionCode,
        LocationId? locationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);

        // Checked before the database is touched: a permission a device may not
        // cache can never be held offline, whatever the stored rows say.
        if (Permissions.Find(permissionCode)?.IsOfflineCapable != true)
        {
            return false;
        }

        DateTimeOffset now = clock.UtcNow;

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.PermissionSnapshots
            .AsNoTracking()
            .Where(s => s.UserId == userId
                        && s.Permission == permissionCode
                        && s.ExpiresAtUtc > now
                        && (s.LocationId == null || locationId == null || s.LocationId == locationId))
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<string> cached = await context.PermissionSnapshots
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.ExpiresAtUtc > now)
            .Select(s => s.Permission)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return cached
            .Where(code => Permissions.Find(code)?.IsOfflineCapable == true)
            .ToHashSet(StringComparer.Ordinal);
    }
}
