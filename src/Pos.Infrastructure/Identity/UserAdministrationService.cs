using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Creates accounts and changes who may do what, under the safeguards of ADR-0028.
/// </summary>
/// <remarks>
/// Every change runs in one transaction with its audit entry and a bump of the
/// authorization policy version, so the new authority takes effect on the next
/// request and the record of who changed it can never be missing or orphaned.
/// </remarks>
public sealed class UserAdministrationService(
    PosDbContext context,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    AdministrationSafeguards safeguards,
    IAuthenticationService authentication,
    IPolicyVersionProvider policyVersion,
    IAuditWriter audit,
    ISystemClock clock,
    IOptions<SecurityOptions> securityOptions) : IUserAdministration
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSummary>> ListAsync(
        string? search, bool includeInactive, int offset, int limit, CancellationToken cancellationToken)
    {
        IQueryable<AppUser> query = context.Users.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive && u.DisabledAtUtc == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToUpperInvariant();
            query = query.Where(u =>
                u.NormalizedUserName!.Contains(term)
                || EF.Functions.Like(u.DisplayName, $"%{search.Trim()}%")
                || u.EmployeeCode == search.Trim());
        }

        List<AppUser> page = await query
            .OrderBy(u => u.UserName)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await SummariseAsync(page, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<UserDetail>> GetAsync(UserId userId, CancellationToken cancellationToken)
    {
        AppUser? user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Result<UserDetail>.Failure(AdministrationErrors.UserUnknown);
        }

        UserSummary summary = (await SummariseAsync([user], cancellationToken).ConfigureAwait(false))[0];

        List<UserLocationSpec> locations = await context.UserLocations.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new UserLocationSpec(a.LocationId.Value, a.IsPrimary))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.UtcNow;
        List<UserPermissionOverride> overrides = await context.UserPermissionOverrides.AsNoTracking()
            .Where(o => o.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, HashSet<string>> effective = await safeguards
            .EffectivePermissionsAsync([userId.Value], cancellationToken)
            .ConfigureAwait(false);

        return Result<UserDetail>.Success(new UserDetail(
            summary,
            locations,
            [.. overrides
                .OrderByDescending(o => o.GrantedAtUtc)
                .Select(o => new UserOverrideView(
                    o.Id, o.PermissionCode, o.Effect.ToString(), o.LocationId?.Value, o.Reason,
                    o.GrantedAtUtc, o.GrantedByUserId.Value, o.ExpiresAtUtc, o.IsActiveAt(now)))],
            [.. effective[userId.Value].Order(StringComparer.Ordinal)],
            HasPin: user.PinHash is not null,
            IsLockedOut: user.LockoutEnd is { } end && end > now,
            user.DisabledAtUtc,
            user.DisabledReason));
    }

    /// <inheritdoc />
    public async Task<Result<UserId>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        UserId createdId = new(Guid.CreateVersion7());

        Result outcome = await ChangeAsync("user created", checkAdministrators: false, async caller =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName)) { return Result.Failure(AdministrationErrors.Required("A username")); }
            if (string.IsNullOrWhiteSpace(request.DisplayName)) { return Result.Failure(AdministrationErrors.Required("A display name")); }

            Result<List<AppRole>> resolvedRoles = await ResolveRolesAsync(request.Roles ?? [], cancellationToken).ConfigureAwait(false);
            if (resolvedRoles.IsFailure) { return Result.Failure(resolvedRoles.Error); }

            HashSet<string> granted = await safeguards
                .RolePermissionsAsync([.. resolvedRoles.Value.Select(r => r.Id)], cancellationToken).ConfigureAwait(false);

            Result guard = Result.Combine(
                AdministrationSafeguards.RequireHeld(caller, granted),
                AdministrationSafeguards.RequireTierWithin(caller, request.ApprovalTier),
                await ValidateLocationsAsync(request.Locations ?? [], cancellationToken).ConfigureAwait(false),
                await EmployeeCodeFreeAsync(request.EmployeeCode, null, cancellationToken).ConfigureAwait(false));
            if (guard.IsFailure) { return guard; }

            AppUser user = new()
            {
                Id = createdId.Value,
                UserName = request.UserName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
                DisplayName = request.DisplayName.Trim(),
                EmployeeCode = string.IsNullOrWhiteSpace(request.EmployeeCode) ? null : request.EmployeeCode.Trim(),
                ApprovalTier = request.ApprovalTier,
                CreatedAtUtc = clock.UtcNow,
                CreatedByUserId = caller.UserId.Value,
            };

            IdentityResult created = await users.CreateAsync(user, request.Password ?? string.Empty).ConfigureAwait(false);
            if (!created.Succeeded) { return Result.Failure(MapIdentityErrors(created)); }

            if (resolvedRoles.Value.Count > 0)
            {
                IdentityResult assigned = await users
                    .AddToRolesAsync(user, resolvedRoles.Value.Select(r => r.Name!)).ConfigureAwait(false);
                if (!assigned.Succeeded) { return Result.Failure(MapIdentityErrors(assigned)); }
            }

            ReplaceLocations(createdId, request.Locations ?? [], caller.UserId);

            await WriteAuditAsync(AuditActions.Administration.UserCreated, createdId, null, new
            {
                user.UserName, user.DisplayName, user.EmployeeCode, user.ApprovalTier,
                Roles = resolvedRoles.Value.Select(r => r.Name), request.Locations,
            }, null, cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);

        return outcome.IsSuccess ? Result<UserId>.Success(createdId) : Result<UserId>.Failure(outcome.Errors);
    }

    /// <inheritdoc />
    public Task<Result> UpdateAsync(UserId userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ChangeTargetAsync(userId, "user updated", allowSelf: true, async (caller, user) =>
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName)) { return Result.Failure(AdministrationErrors.Required("A display name")); }

            if (request.ApprovalTier != user.ApprovalTier)
            {
                Result tier = Result.Combine(
                    AdministrationSafeguards.RefuseSelf(caller, userId),
                    AdministrationSafeguards.RequireTierWithin(caller, request.ApprovalTier));
                if (tier.IsFailure) { return tier; }
            }

            Result code = await EmployeeCodeFreeAsync(request.EmployeeCode, user.Id, cancellationToken).ConfigureAwait(false);
            if (code.IsFailure) { return code; }

            var previous = new { user.DisplayName, user.Email, user.EmployeeCode, user.ApprovalTier };
            user.DisplayName = request.DisplayName.Trim();
            user.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
            user.EmployeeCode = string.IsNullOrWhiteSpace(request.EmployeeCode) ? null : request.EmployeeCode.Trim();
            user.ApprovalTier = request.ApprovalTier;

            IdentityResult updated = await users.UpdateAsync(user).ConfigureAwait(false);
            if (!updated.Succeeded) { return Result.Failure(MapIdentityErrors(updated)); }

            await WriteAuditAsync(AuditActions.Administration.UserUpdated, userId, previous,
                new { user.DisplayName, user.Email, user.EmployeeCode, user.ApprovalTier }, null, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> DisableAsync(UserId userId, string reason, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user disabled", allowSelf: false, async (caller, user) =>
        {
            if (!HasReason(reason)) { return Result.Failure(AdministrationErrors.ReasonRequired); }
            if (!user.CanAuthenticate) { return Result.Failure(AdministrationErrors.AlreadyInState("disabled")); }

            user.IsActive = false;
            user.DisabledAtUtc = clock.UtcNow;
            user.DisabledReason = reason.Trim();

            IdentityResult updated = await users.UpdateAsync(user).ConfigureAwait(false);
            if (!updated.Succeeded) { return Result.Failure(MapIdentityErrors(updated)); }

            await authentication.RevokeAllSessionsAsync(userId, "account disabled", cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(AuditActions.Administration.UserDisabled, userId, null, null, reason, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken, checkAdministrators: true);

    /// <inheritdoc />
    public Task<Result> EnableAsync(UserId userId, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user enabled", allowSelf: false, async (caller, user) =>
        {
            if (user.CanAuthenticate) { return Result.Failure(AdministrationErrors.AlreadyInState("active")); }

            user.IsActive = true;
            user.DisabledAtUtc = null;
            user.DisabledReason = null;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;

            IdentityResult updated = await users.UpdateAsync(user).ConfigureAwait(false);
            if (!updated.Succeeded) { return Result.Failure(MapIdentityErrors(updated)); }

            await WriteAuditAsync(AuditActions.Administration.UserEnabled, userId, null, null, null, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetRolesAsync(UserId userId, IReadOnlyList<string> roleNames, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user roles changed", allowSelf: false, async (caller, user) =>
        {
            Result<List<AppRole>> resolved = await ResolveRolesAsync(roleNames ?? [], cancellationToken).ConfigureAwait(false);
            if (resolved.IsFailure) { return Result.Failure(resolved.Error); }

            HashSet<string> granted = await safeguards
                .RolePermissionsAsync([.. resolved.Value.Select(r => r.Id)], cancellationToken).ConfigureAwait(false);
            Result held = AdministrationSafeguards.RequireHeld(caller, granted);
            if (held.IsFailure) { return held; }

            IList<string> current = await users.GetRolesAsync(user).ConfigureAwait(false);
            HashSet<string> wanted = [.. resolved.Value.Select(r => r.Name!)];

            IdentityResult removed = await users.RemoveFromRolesAsync(user, current.Where(r => !wanted.Contains(r))).ConfigureAwait(false);
            IdentityResult added = await users.AddToRolesAsync(user, wanted.Where(r => !current.Contains(r))).ConfigureAwait(false);
            if (!removed.Succeeded || !added.Succeeded) { return Result.Failure(MapIdentityErrors(removed.Succeeded ? added : removed)); }

            await WriteAuditAsync(AuditActions.Administration.UserRolesChanged, userId,
                new { Roles = current.Order(StringComparer.Ordinal) }, new { Roles = wanted.Order(StringComparer.Ordinal) },
                null, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken, checkAdministrators: true);

    /// <inheritdoc />
    public Task<Result> SetLocationsAsync(
        UserId userId, IReadOnlyList<UserLocationSpec> locations, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user locations changed", allowSelf: false, async (caller, _) =>
        {
            Result valid = await ValidateLocationsAsync(locations ?? [], cancellationToken).ConfigureAwait(false);
            if (valid.IsFailure) { return valid; }

            List<Guid> previous = await context.UserLocations.AsNoTracking()
                .Where(a => a.UserId == userId).Select(a => a.LocationId.Value)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            List<UserLocationAssignment> existing = await context.UserLocations.AsTracking()
                .Where(a => a.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);
            context.UserLocations.RemoveRange(existing);
            ReplaceLocations(userId, locations ?? [], caller.UserId);

            await WriteAuditAsync(AuditActions.Administration.UserLocationsChanged, userId,
                new { Locations = previous }, new { Locations = locations }, null, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<Guid>> GrantOverrideAsync(
        UserId userId, GrantOverrideRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid overrideId = Guid.Empty;

        Result outcome = await ChangeTargetAsync(userId, "user override granted", allowSelf: false, async (caller, _) =>
        {
            if (!Permissions.IsDefined(request.PermissionCode ?? string.Empty))
            {
                return Result.Failure(AdministrationErrors.PermissionUnknown(request.PermissionCode ?? string.Empty));
            }

            if (request.Effect == PermissionEffect.Grant)
            {
                Result held = AdministrationSafeguards.RequireHeld(caller, [request.PermissionCode!]);
                if (held.IsFailure) { return held; }
            }

            if (request.LocationId is { } location)
            {
                Result valid = await ValidateLocationsAsync([new UserLocationSpec(location, false)], cancellationToken).ConfigureAwait(false);
                if (valid.IsFailure) { return valid; }
            }

            Result<UserPermissionOverride> created = UserPermissionOverride.Create(
                userId, request.PermissionCode!, request.Effect, clock.UtcNow, caller.UserId, request.Reason ?? string.Empty,
                request.ExpiresAtUtc, request.LocationId is { } l ? new LocationId(l) : null);
            if (created.IsFailure) { return Result.Failure(created.Errors); }

            context.UserPermissionOverrides.Add(created.Value);
            overrideId = created.Value.Id;

            await WriteAuditAsync(AuditActions.Administration.UserOverrideChanged, userId, null, new
            {
                created.Value.Id, created.Value.PermissionCode, Effect = created.Value.Effect.ToString(),
                created.Value.ExpiresAtUtc, LocationId = request.LocationId,
            }, created.Value.Reason, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken, checkAdministrators: true).ConfigureAwait(false);

        return outcome.IsSuccess ? Result<Guid>.Success(overrideId) : Result<Guid>.Failure(outcome.Errors);
    }

    /// <inheritdoc />
    public Task<Result> RemoveOverrideAsync(
        UserId userId, Guid overrideId, string reason, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user override removed", allowSelf: false, async (caller, _) =>
        {
            if (!HasReason(reason)) { return Result.Failure(AdministrationErrors.ReasonRequired); }

            UserPermissionOverride? existing = await context.UserPermissionOverrides.AsTracking()
                .FirstOrDefaultAsync(o => o.Id == overrideId && o.UserId == userId, cancellationToken).ConfigureAwait(false);
            if (existing is null) { return Result.Failure(AdministrationErrors.OverrideUnknown); }

            // Lifting a deny gives the permission back, which is handing it out.
            if (existing.Effect == PermissionEffect.Deny)
            {
                Result held = AdministrationSafeguards.RequireHeld(caller, [existing.PermissionCode]);
                if (held.IsFailure) { return held; }
            }

            context.UserPermissionOverrides.Remove(existing);

            await WriteAuditAsync(AuditActions.Administration.UserOverrideChanged, userId,
                new { existing.Id, existing.PermissionCode, Effect = existing.Effect.ToString() }, null, reason, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken, checkAdministrators: true);

    /// <inheritdoc />
    public Task<Result> ResetPasswordAsync(UserId userId, string newPassword, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user password reset", allowSelf: false, async (_, user) =>
        {
            foreach (IPasswordValidator<AppUser> validator in users.PasswordValidators)
            {
                IdentityResult check = await validator.ValidateAsync(users, user, newPassword ?? string.Empty).ConfigureAwait(false);
                if (!check.Succeeded) { return Result.Failure(MapIdentityErrors(check)); }
            }

            IdentityResult removed = await users.RemovePasswordAsync(user).ConfigureAwait(false);
            IdentityResult added = removed.Succeeded
                ? await users.AddPasswordAsync(user, newPassword!).ConfigureAwait(false)
                : removed;
            if (!added.Succeeded) { return Result.Failure(MapIdentityErrors(added)); }

            await authentication.RevokeAllSessionsAsync(userId, "password reset by an administrator", cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(AuditActions.Authentication.PasswordChanged, userId, null, null, "reset by an administrator", cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetPinAsync(UserId userId, string pin, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user pin set", allowSelf: false, async (_, user) =>
        {
            int minimum = securityOptions.Value.MinimumPinLength;
            if (string.IsNullOrEmpty(pin) || pin.Length < minimum || !pin.All(char.IsAsciiDigit))
            {
                return Result.Failure(AdministrationErrors.PinInvalid(minimum));
            }

            if (string.IsNullOrWhiteSpace(user.EmployeeCode)) { return Result.Failure(AdministrationErrors.EmployeeCodeRequired); }

            user.PinHash = users.PasswordHasher.HashPassword(user, pin);
            user.PinChangedAtUtc = clock.UtcNow;

            IdentityResult updated = await users.UpdateAsync(user).ConfigureAwait(false);
            if (!updated.Succeeded) { return Result.Failure(MapIdentityErrors(updated)); }

            await WriteAuditAsync(AuditActions.Authentication.PinChanged, userId, null, null, "set by an administrator", cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> ResetTwoFactorAsync(UserId userId, string reason, CancellationToken cancellationToken)
        => ChangeTargetAsync(userId, "user two-factor reset", allowSelf: false, async (_, user) =>
        {
            if (!HasReason(reason)) { return Result.Failure(AdministrationErrors.ReasonRequired); }

            await users.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
            await users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            await authentication.RevokeAllSessionsAsync(userId, "two-factor reset by an administrator", cancellationToken).ConfigureAwait(false);

            await WriteAuditAsync(AuditActions.Authentication.TwoFactorReset, userId, null, null, reason, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    private async Task<Result> ChangeTargetAsync(
        UserId userId,
        string policyReason,
        bool allowSelf,
        Func<AdministratorContext, AppUser, Task<Result>> apply,
        CancellationToken cancellationToken,
        bool checkAdministrators = false)
        => await ChangeAsync(policyReason, checkAdministrators, async caller =>
        {
            AppUser? user = await users.FindByIdAsync(userId.Value.ToString()).ConfigureAwait(false);
            if (user is null) { return Result.Failure(AdministrationErrors.UserUnknown); }

            Result guard = allowSelf
                ? Result.Success()
                : AdministrationSafeguards.RefuseSelf(caller, userId);
            if (guard.IsFailure) { return guard; }

            if (caller.UserId != userId)
            {
                Result rank = await safeguards.RequireOutranksAsync(caller, userId, cancellationToken).ConfigureAwait(false);
                if (rank.IsFailure) { return rank; }
            }

            return await apply(caller, user).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<Result> ChangeAsync(
        string policyReason,
        bool checkAdministrators,
        Func<AdministratorContext, Task<Result>> apply,
        CancellationToken cancellationToken)
    {
        Result<AdministratorContext> caller = await safeguards.CallerAsync(cancellationToken).ConfigureAwait(false);
        if (caller.IsFailure) { return Result.Failure(caller.Errors); }

        // Identity's stores change existing rows (tokens, role links) that only
        // persist under tracking queries.
        using TrackingScope tracking = TrackingScope.Begin(context);

        await using IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Result applied = await apply(caller.Value).ConfigureAwait(false);

        if (applied.IsSuccess)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (checkAdministrators && !await safeguards.AdministratorRemainsAsync(cancellationToken).ConfigureAwait(false))
            {
                applied = Result.Failure(AdministrationErrors.LastAdministrator);
            }
        }

        if (applied.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            return applied;
        }

        await policyVersion.BumpAsync(policyReason, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<IReadOnlyList<UserSummary>> SummariseAsync(List<AppUser> page, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. page.Select(u => u.Id)];
        List<UserId> typedIds = [.. ids.Select(id => new UserId(id))];

        var memberships = await (
                from ur in context.UserRoles.AsNoTracking()
                join r in context.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where ids.Contains(ur.UserId)
                select new { ur.UserId, r.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var assignments = await context.UserLocations.AsNoTracking()
            .Where(a => typedIds.Contains(a.UserId))
            .Select(a => new { a.UserId, a.LocationId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. page.Select(u => new UserSummary(
            u.Id, u.UserName ?? string.Empty, u.DisplayName, u.Email, u.EmployeeCode,
            u.CanAuthenticate, u.TwoFactorEnabled, u.ApprovalTier,
            [.. memberships.Where(m => m.UserId == u.Id).Select(m => m.Name!).Order(StringComparer.Ordinal)],
            [.. assignments.Where(a => a.UserId.Value == u.Id).Select(a => a.LocationId.Value)],
            u.CreatedAtUtc, u.LastLoginAtUtc))];
    }

    private async Task<Result<List<AppRole>>> ResolveRolesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        List<AppRole> resolved = [];

        foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AppRole? role = await roles.FindByNameAsync(name.Trim()).ConfigureAwait(false);
            if (role is null) { return Result<List<AppRole>>.Failure(AdministrationErrors.RoleUnknown(name)); }
            resolved.Add(role);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Result<List<AppRole>>.Success(resolved);
    }

    private async Task<Result> ValidateLocationsAsync(IReadOnlyList<UserLocationSpec> locations, CancellationToken cancellationToken)
    {
        if (locations.Count(l => l.IsPrimary) > 1) { return Result.Failure(AdministrationErrors.PrimaryLocationInvalid); }

        List<LocationId> ids = [.. locations.Select(l => new LocationId(l.LocationId)).Distinct()];
        var found = await context.Locations.AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, l.Kind })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (LocationId id in ids)
        {
            var location = found.FirstOrDefault(l => l.Id == id);
            if (location is null) { return Result.Failure(AdministrationErrors.LocationUnknown(id.Value)); }
            if (location.Kind == LocationKind.External) { return Result.Failure(AdministrationErrors.LocationExternal); }
        }

        return Result.Success();
    }

    private async Task<Result> EmployeeCodeFreeAsync(string? code, Guid? ownerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)) { return Result.Success(); }

        string trimmed = code.Trim();
        bool taken = await context.Users.AsNoTracking()
            .AnyAsync(u => u.EmployeeCode == trimmed && (ownerId == null || u.Id != ownerId), cancellationToken)
            .ConfigureAwait(false);

        return taken ? Result.Failure(AdministrationErrors.EmployeeCodeTaken) : Result.Success();
    }

    private void ReplaceLocations(UserId userId, IReadOnlyList<UserLocationSpec> locations, UserId assignedBy)
    {
        foreach (UserLocationSpec spec in locations.DistinctBy(l => l.LocationId))
        {
            context.UserLocations.Add(UserLocationAssignment.Create(
                userId, new LocationId(spec.LocationId), spec.IsPrimary, clock.UtcNow, assignedBy));
        }
    }

    private Task WriteAuditAsync(
        string action, UserId userId, object? previous, object? next, string? reason, CancellationToken cancellationToken)
        => audit.WriteAsync(
            new AuditEntry(
                action,
                nameof(AppUser),
                userId.Value,
                previous is null ? null : JsonSerializer.Serialize(previous),
                next is null ? null : JsonSerializer.Serialize(next),
                reason),
            cancellationToken);

    private static bool HasReason(string? reason) => reason is not null && reason.Trim().Length >= 5;

    private static Error MapIdentityErrors(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is "DuplicateUserName"))
        {
            return AdministrationErrors.UserNameTaken;
        }

        return result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal))
            ? AdministrationErrors.PasswordRejected(result.Errors.Select(e => e.Description))
            : Error.Validation("identity.account_invalid", string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
