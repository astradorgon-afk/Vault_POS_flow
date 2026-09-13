using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>The caller of an administrative action and the authority they hold.</summary>
/// <param name="UserId">The caller.</param>
/// <param name="Permissions">Their effective permissions, resolved fresh.</param>
/// <param name="ApprovalTier">Their approval tier.</param>
public sealed record AdministratorContext(UserId UserId, IReadOnlySet<string> Permissions, ApprovalTier ApprovalTier);

/// <summary>
/// The rules every change to someone's authority must pass (ADR-0028).
/// </summary>
/// <remarks>
/// <para>
/// Authority is resolved straight from the database, never from the permission
/// cache, and inside the change's own transaction: a decision about who may do
/// what must not rest on a cached answer from before the change began.
/// </para>
/// <list type="number">
/// <item>Nobody administers their own authority or account status.</item>
/// <item>Nobody hands out a governance permission, or an approval tier, above their own.</item>
/// <item>Nobody changes an account or role holding governance authority they lack.</item>
/// <item>At least one active account always remains able to manage both users and roles.</item>
/// </list>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class AdministrationSafeguards(PosDbContext context, ICurrentUser currentUser, ISystemClock clock)
{
    /// <summary>Resolves the caller.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The caller's context, or a refusal when there is no authenticated caller.</returns>
    public async Task<Result<AdministratorContext>> CallerAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } callerId)
        {
            return Result<AdministratorContext>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        Dictionary<Guid, HashSet<string>> effective = await EffectivePermissionsAsync([callerId.Value], cancellationToken)
            .ConfigureAwait(false);

        ApprovalTier tier = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == callerId.Value)
            .Select(u => u.ApprovalTier)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<AdministratorContext>.Success(new AdministratorContext(
            callerId,
            effective.GetValueOrDefault(callerId.Value) ?? new HashSet<string>(StringComparer.Ordinal),
            tier));
    }

    /// <summary>Refuses a change the caller would make to their own account.</summary>
    public static Result RefuseSelf(AdministratorContext caller, UserId target)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.UserId == target ? Result.Failure(AdministrationErrors.SelfAdministration) : Result.Success();
    }

    /// <summary>Refuses handing out governance permissions the caller does not hold.</summary>
    public static Result RequireHeld(AdministratorContext caller, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(caller);

        List<string> missing = [.. permissions
            .Where(Permissions.Privileged.Contains)
            .Where(code => !caller.Permissions.Contains(code))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        return missing.Count == 0 ? Result.Success() : Result.Failure(AdministrationErrors.PrivilegeEscalation(missing));
    }

    /// <summary>Refuses an approval tier above the caller's own.</summary>
    public static Result RequireTierWithin(AdministratorContext caller, ApprovalTier tier)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return tier <= caller.ApprovalTier ? Result.Success() : Result.Failure(AdministrationErrors.TierAboveCaller);
    }

    /// <summary>Refuses changing an account whose governance authority or tier exceeds the caller's.</summary>
    public async Task<Result> RequireOutranksAsync(
        AdministratorContext caller, UserId target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        Dictionary<Guid, HashSet<string>> effective = await EffectivePermissionsAsync([target.Value], cancellationToken)
            .ConfigureAwait(false);

        ApprovalTier targetTier = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == target.Value)
            .Select(u => u.ApprovalTier)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        bool outranked = (effective.GetValueOrDefault(target.Value) ?? [])
            .Any(code => Permissions.Privileged.Contains(code) && !caller.Permissions.Contains(code));

        return outranked || targetTier > caller.ApprovalTier
            ? Result.Failure(AdministrationErrors.TargetOutranksCaller)
            : Result.Success();
    }

    /// <summary>
    /// Determines whether at least one active account can still manage both users
    /// and roles. Reads the current transaction's own uncommitted changes.
    /// </summary>
    public async Task<bool> AdministratorRemainsAsync(CancellationToken cancellationToken)
    {
        List<Guid> active = await context.Users
            .AsNoTracking()
            .Where(u => u.IsActive && u.DisabledAtUtc == null)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, HashSet<string>> effective = await EffectivePermissionsAsync(active, cancellationToken)
            .ConfigureAwait(false);

        return effective.Values.Any(p =>
            p.Contains(Permissions.Administration.ManageUsers) && p.Contains(Permissions.Administration.ManageRoles));
    }

    /// <summary>
    /// Resolves effective permissions for accounts regardless of whether they are
    /// active: role grants, plus grant overrides in force, minus deny overrides in force.
    /// </summary>
    public async Task<Dictionary<Guid, HashSet<string>>> EffectivePermissionsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        List<Guid> ids = [.. userIds.Distinct()];
        List<UserId> typedIds = [.. ids.Select(id => new UserId(id))];

        var memberships = await context.UserRoles
            .AsNoTracking()
            .Where(ur => ids.Contains(ur.UserId))
            .Select(ur => new { ur.UserId, ur.RoleId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<Guid> roleIds = [.. memberships.Select(m => m.RoleId).Distinct()];

        var grants = await context.RolePermissions
            .AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => new { rp.RoleId, rp.PermissionCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<UserPermissionOverride> overrides = await context.UserPermissionOverrides
            .AsNoTracking()
            .Where(o => typedIds.Contains(o.UserId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        Dictionary<Guid, HashSet<string>> result = [];

        foreach (Guid id in ids)
        {
            HashSet<Guid> roles = [.. memberships.Where(m => m.UserId == id).Select(m => m.RoleId)];
            HashSet<string> permissions = new(
                grants.Where(g => roles.Contains(g.RoleId)).Select(g => g.PermissionCode),
                StringComparer.Ordinal);

            List<UserPermissionOverride> own = [.. overrides.Where(o => o.UserId.Value == id && o.IsActiveAt(now))];

            foreach (UserPermissionOverride grant in own.Where(o => o.Effect == PermissionEffect.Grant))
            {
                permissions.Add(grant.PermissionCode);
            }

            foreach (UserPermissionOverride deny in own.Where(o => o.Effect == PermissionEffect.Deny))
            {
                permissions.Remove(deny.PermissionCode);
            }

            result[id] = permissions;
        }

        return result;
    }

    /// <summary>Resolves the permissions a set of roles grants between them.</summary>
    public async Task<HashSet<string>> RolePermissionsAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. roleIds];

        List<string> codes = await context.RolePermissions
            .AsNoTracking()
            .Where(rp => ids.Contains(rp.RoleId))
            .Select(rp => rp.PermissionCode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(codes, StringComparer.Ordinal);
    }
}
