using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Brings the database's authorization tables into line with the code
/// catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The permission catalogue and the default role grants are defined in code, not
/// in the database, so a permission cannot be invented by editing a table and
/// then quietly satisfy a check. This seeder makes the rows match.
/// </para>
/// <para>
/// It is idempotent and safe to run on every deployment. Permissions that have
/// disappeared from the catalogue are reported rather than deleted: a grant
/// referencing one is a real inconsistency that someone should look at, and
/// silently dropping rows would erase the evidence.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="roles">Identity's role manager.</param>
/// <param name="policyVersion">The authorization policy version.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="logger">Logger.</param>
public sealed class IdentitySeeder(
    PosDbContext context,
    RoleManager<AppRole> roles,
    IPolicyVersionProvider policyVersion,
    ISystemClock clock,
    ILogger<IdentitySeeder> logger)
{
    /// <summary>Seeds permissions, roles and their default grants.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what changed.</returns>
    public async Task<SeedSummary> SeedAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;

        int permissionsAdded = await SeedPermissionsAsync(cancellationToken).ConfigureAwait(false);
        int rolesAdded = await SeedRolesAsync(now).ConfigureAwait(false);
        int grantsAdded = await SeedRoleGrantsAsync(now, cancellationToken).ConfigureAwait(false);
        await EnsurePolicyVersionRowAsync(now, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> orphaned = await FindOrphanedGrantsAsync(cancellationToken).ConfigureAwait(false);

        if (orphaned.Count > 0)
        {
            logger.LogWarning(
                "{Count} role grants reference permissions that are no longer in the catalogue: {Codes}. "
                + "They are left in place for review rather than deleted.",
                orphaned.Count,
                string.Join(", ", orphaned));
        }

        if (permissionsAdded + rolesAdded + grantsAdded > 0)
        {
            await policyVersion.BumpAsync("identity seed", cancellationToken).ConfigureAwait(false);
        }

        return new SeedSummary(permissionsAdded, rolesAdded, grantsAdded, orphaned);
    }

    private async Task<int> SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> existing = await context.Permissions
            .AsNoTracking()
            .Select(p => p.Code)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        int added = 0;

        foreach (PermissionDefinition definition in Permissions.All)
        {
            if (existing.Contains(definition.Code))
            {
                // Descriptions and flags live in code, so refresh them in case
                // the catalogue changed.
                PermissionRecord? row = await context.Permissions
                    .AsTracking()
                    .FirstOrDefaultAsync(p => p.Code == definition.Code, cancellationToken)
                    .ConfigureAwait(false);

                if (row is not null)
                {
                    row.Module = definition.Module;
                    row.Description = definition.Description;
                    row.IsOfflineCapable = definition.IsOfflineCapable;
                    row.IsReadOnly = definition.IsReadOnly;
                }

                continue;
            }

            context.Permissions.Add(new PermissionRecord
            {
                Code = definition.Code,
                Module = definition.Module,
                Description = definition.Description,
                IsOfflineCapable = definition.IsOfflineCapable,
                IsReadOnly = definition.IsReadOnly,
            });

            added++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return added;
    }

    private async Task<int> SeedRolesAsync(DateTimeOffset now)
    {
        int added = 0;

        foreach (string roleName in Roles.All)
        {
            if (await roles.RoleExistsAsync(roleName).ConfigureAwait(false))
            {
                continue;
            }

            IdentityResult result = await roles.CreateAsync(new AppRole
            {
                Id = Guid.CreateVersion7(),
                Name = roleName,
                NormalizedName = roleName.ToUpperInvariant(),
                Description = DescribeRole(roleName),
                IsSystemRole = true,
                CreatedAtUtc = now,
            }).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Could not create role {roleName}: {string.Join("; ", result.Errors.Select(e => e.Description))}"));
            }

            added++;
        }

        return added;
    }

    private async Task<int> SeedRoleGrantsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        int added = 0;

        foreach ((string roleName, IReadOnlyList<string> codes) in Roles.DefaultGrants)
        {
            AppRole? role = await roles.FindByNameAsync(roleName).ConfigureAwait(false);

            if (role is null)
            {
                continue;
            }

            HashSet<string> existing = await context.RolePermissions
                .AsNoTracking()
                .Where(rp => rp.RoleId == role.Id)
                .Select(rp => rp.PermissionCode)
                .ToHashSetAsync(StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);

            foreach (string code in codes.Where(c => !existing.Contains(c)))
            {
                context.RolePermissions.Add(new RolePermissionGrant
                {
                    RoleId = role.Id,
                    PermissionCode = code,
                    GrantedAtUtc = now,
                    GrantedByUserId = null,
                });

                added++;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return added;
    }

    private async Task EnsurePolicyVersionRowAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool exists = await context.PolicyVersion
            .AnyAsync(v => v.Id == AuthorizationPolicyVersion.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return;
        }

        context.PolicyVersion.Add(new AuthorizationPolicyVersion
        {
            Id = AuthorizationPolicyVersion.SingletonId,
            Version = 1,
            UpdatedAtUtc = now,
            LastChangeReason = "initial seed",
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> FindOrphanedGrantsAsync(CancellationToken cancellationToken)
    {
        List<string> granted = await context.RolePermissions
            .AsNoTracking()
            .Select(rp => rp.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. granted.Where(code => !Permissions.IsDefined(code)).Order(StringComparer.Ordinal)];
    }

    private static string DescribeRole(string roleName) => roleName switch
    {
        Roles.Owner => "The business owner. Unlimited authority across every location.",
        Roles.Administrator => "Manages users, devices, catalog and settings.",
        Roles.MainInventoryManager => "Runs the Main Warehouse and business-wide inventory control.",
        Roles.StoreManager => "Runs one store. Authority is confined to assigned locations.",
        Roles.InventoryStaff => "Handles stock at a location, without financial authority.",
        Roles.Cashier => "Operates the point of sale.",
        Roles.Auditor => "Reads everything, changes nothing.",
        _ => roleName,
    };
}

/// <summary>What the seeder changed.</summary>
/// <param name="PermissionsAdded">How many catalogue rows were inserted.</param>
/// <param name="RolesAdded">How many roles were created.</param>
/// <param name="GrantsAdded">How many role grants were inserted.</param>
/// <param name="OrphanedGrants">
/// Grants naming permissions that are no longer in the code catalogue. Reported,
/// never silently deleted.
/// </param>
public sealed record SeedSummary(
    int PermissionsAdded,
    int RolesAdded,
    int GrantsAdded,
    IReadOnlyList<string> OrphanedGrants);
