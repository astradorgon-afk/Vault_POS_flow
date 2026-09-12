using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// A user's authority, resolved once and cached.
/// </summary>
/// <param name="Permissions">Every permission code the user effectively holds.</param>
/// <param name="Locations">The locations the user is assigned to.</param>
/// <param name="HasAllLocations">Whether the user may act business-wide.</param>
/// <param name="IsActive">Whether the account can authenticate at all.</param>
/// <param name="Roles">The role names held, snapshotted for the audit log.</param>
/// <param name="ApprovalTier">The highest value band the user may approve.</param>
public sealed record UserAuthorization(
    IReadOnlySet<string> Permissions,
    IReadOnlySet<LocationId> Locations,
    bool HasAllLocations,
    bool IsActive,
    IReadOnlyList<string> Roles,
    ApprovalTier ApprovalTier)
{
    /// <summary>An authorization that grants nothing.</summary>
    public static UserAuthorization None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<LocationId>(),
        HasAllLocations: false,
        IsActive: false,
        [],
        ApprovalTier.None);
}

/// <summary>
/// Answers permission questions from the database, with a short-lived cache.
/// </summary>
/// <remarks>
/// <para>
/// The effective set is the union of every role's grants and every
/// <see cref="PermissionEffect.Grant"/> override, minus every
/// <see cref="PermissionEffect.Deny"/> override. Deny always wins, so one
/// override can suspend a single capability without unpicking someone's roles.
/// </para>
/// <para>
/// Expired overrides are ignored at evaluation time rather than relied upon to
/// have been cleaned up: a nightly purge that fails must not silently extend
/// someone's authority.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="cache">The in-process cache.</param>
/// <param name="policyVersion">The policy version, which forms part of the cache key.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class DatabasePermissionEvaluator(
    PosDbContext context,
    IMemoryCache cache,
    IPolicyVersionProvider policyVersion,
    ISystemClock clock) : IPermissionEvaluator
{
    /// <summary>How long a resolved permission set is held in process.</summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<bool> HasPermissionAsync(
        UserId userId,
        string permissionCode,
        LocationId? locationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);

        UserAuthorization authorization = await GetAuthorizationAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        if (!authorization.IsActive || !authorization.Permissions.Contains(permissionCode))
        {
            return false;
        }

        // Holding a permission is only half the question. The other half is
        // where: this is what confines a store manager to their own store.
        return locationId is null
               || authorization.HasAllLocations
               || authorization.Locations.Contains(locationId.Value);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await GetAuthorizationAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return authorization.Permissions;
    }

    /// <summary>Resolves everything known about a user's authority.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's authorization, or <see cref="UserAuthorization.None"/>.</returns>
    public async Task<UserAuthorization> GetAuthorizationAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        long version = await policyVersion.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        string cacheKey = $"authz:user:{userId.Value}:{version}";

        if (cache.TryGetValue(cacheKey, out UserAuthorization? cached) && cached is not null)
        {
            return cached;
        }

        UserAuthorization resolved = await ResolveAsync(userId, cancellationToken).ConfigureAwait(false);

        cache.Set(cacheKey, resolved, CacheWindow);

        return resolved;
    }

    /// <summary>Clears any cached authority for one user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the entry is evicted.</returns>
    public async Task InvalidateAsync(UserId userId, CancellationToken cancellationToken)
    {
        long version = await policyVersion.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        cache.Remove($"authz:user:{userId.Value}:{version}");
    }

    private async Task<UserAuthorization> ResolveAsync(UserId userId, CancellationToken cancellationToken)
    {
        Guid id = userId.Value;

        AppUser? user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !user.CanAuthenticate)
        {
            return UserAuthorization.None;
        }

        List<Guid> roleIds = await context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == id)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<string> roleNames = await context.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<string> granted = await context.RolePermissions
            .AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<string> permissions = new(granted, StringComparer.Ordinal);

        List<UserPermissionOverride> overrides = await context.UserPermissionOverrides
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;

        foreach (UserPermissionOverride grant in overrides
                     .Where(o => o.Effect == PermissionEffect.Grant && o.IsActiveAt(now)))
        {
            permissions.Add(grant.PermissionCode);
        }

        // Deny is applied last and unconditionally: it must beat every grant,
        // including one that arrives later from a role change.
        foreach (UserPermissionOverride deny in overrides
                     .Where(o => o.Effect == PermissionEffect.Deny && o.IsActiveAt(now)))
        {
            permissions.Remove(deny.PermissionCode);
        }

        List<LocationId> locations = await context.UserLocations
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.LocationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new UserAuthorization(
            permissions,
            new HashSet<LocationId>(locations),
            permissions.Contains(Permissions.Administration.AllLocations),
            IsActive: true,
            roleNames,
            user.ApprovalTier);
    }
}
