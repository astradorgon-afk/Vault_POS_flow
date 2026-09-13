using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Lists roles and edits the permissions they bundle, under the safeguards of ADR-0028.
/// </summary>
/// <param name="context">The database context.</param>
/// <param name="safeguards">The administration safeguards.</param>
/// <param name="policyVersion">The authorization policy version.</param>
/// <param name="audit">The audit writer.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class RoleAdministrationService(
    PosDbContext context,
    AdministrationSafeguards safeguards,
    IPolicyVersionProvider policyVersion,
    IAuditWriter audit,
    ISystemClock clock) : IRoleAdministration
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleView>> ListRolesAsync(CancellationToken cancellationToken)
    {
        List<AppRole> roles = await context.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var grants = await context.RolePermissions.AsNoTracking()
            .Select(rp => new { rp.RoleId, rp.PermissionCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var members = await context.UserRoles.AsNoTracking()
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. roles.Select(r => new RoleView(
            r.Id,
            r.Name ?? string.Empty,
            r.Description,
            r.IsSystemRole,
            [.. grants.Where(g => g.RoleId == r.Id).Select(g => g.PermissionCode).Order(StringComparer.Ordinal)],
            members.FirstOrDefault(m => m.RoleId == r.Id)?.Count ?? 0))];
    }

    /// <inheritdoc />
    public IReadOnlyList<PermissionView> ListPermissions()
        => [.. Permissions.All.Select(p => new PermissionView(
            p.Code, p.Module, p.Description, p.IsOfflineCapable, p.IsReadOnly, Permissions.Privileged.Contains(p.Code)))];

    /// <inheritdoc />
    public async Task<Result> SetPermissionsAsync(
        Guid roleId, IReadOnlyList<string> permissionCodes, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        if (reason is null || reason.Trim().Length < 5)
        {
            return Result.Failure(AdministrationErrors.ReasonRequired);
        }

        string? unknown = permissionCodes.FirstOrDefault(code => !Permissions.IsDefined(code));

        if (unknown is not null)
        {
            return Result.Failure(AdministrationErrors.PermissionUnknown(unknown));
        }

        Result<AdministratorContext> caller = await safeguards.CallerAsync(cancellationToken).ConfigureAwait(false);

        if (caller.IsFailure)
        {
            return Result.Failure(caller.Errors);
        }

        AppRole? role = await context.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            return Result.Failure(AdministrationErrors.RoleUnknown(roleId.ToString()));
        }

        // A role the caller holds is their own authority, and a role carrying
        // governance authority the caller lacks outranks them.
        bool holdsRole = await context.UserRoles.AsNoTracking()
            .AnyAsync(ur => ur.RoleId == roleId && ur.UserId == caller.Value.UserId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (holdsRole)
        {
            return Result.Failure(AdministrationErrors.SelfAdministration);
        }

        HashSet<string> current = await safeguards.RolePermissionsAsync([roleId], cancellationToken).ConfigureAwait(false);

        if (current.Any(code => Permissions.Privileged.Contains(code) && !caller.Value.Permissions.Contains(code)))
        {
            return Result.Failure(AdministrationErrors.TargetOutranksCaller);
        }

        HashSet<string> wanted = new(permissionCodes, StringComparer.Ordinal);
        Result held = AdministrationSafeguards.RequireHeld(caller.Value, wanted.Where(code => !current.Contains(code)));

        if (held.IsFailure)
        {
            return held;
        }

        await using IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        List<RolePermissionGrant> removed = await context.RolePermissions.AsTracking()
            .Where(rp => rp.RoleId == roleId && !wanted.Contains(rp.PermissionCode))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        context.RolePermissions.RemoveRange(removed);

        DateTimeOffset now = clock.UtcNow;

        foreach (string code in wanted.Where(code => !current.Contains(code)))
        {
            context.RolePermissions.Add(new RolePermissionGrant
            {
                RoleId = roleId,
                PermissionCode = code,
                GrantedAtUtc = now,
                GrantedByUserId = caller.Value.UserId.Value,
            });
        }

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Administration.RolePermissionsChanged,
                nameof(AppRole),
                roleId,
                JsonSerializer.Serialize(new { Role = role.Name, Permissions = current.Order(StringComparer.Ordinal) }),
                JsonSerializer.Serialize(new { Role = role.Name, Permissions = wanted.Order(StringComparer.Ordinal) }),
                reason.Trim()),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!await safeguards.AdministratorRemainsAsync(cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return Result.Failure(AdministrationErrors.LastAdministrator);
        }

        await policyVersion.BumpAsync("role permissions changed", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
