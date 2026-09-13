using Pos.Domain.Common;
using Pos.Domain.Identity;

namespace Pos.Infrastructure.Identity;

/// <summary>A user as listed for administration.</summary>
public sealed record UserSummary(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? EmployeeCode,
    bool IsActive,
    bool TwoFactorEnabled,
    ApprovalTier ApprovalTier,
    IReadOnlyList<string> Roles,
    IReadOnlyList<Guid> LocationIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

/// <summary>One location a user is assigned to.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="IsPrimary">Whether it is the user's home location.</param>
public sealed record UserLocationSpec(Guid LocationId, bool IsPrimary);

/// <summary>A per-user permission override as shown to an administrator.</summary>
public sealed record UserOverrideView(
    Guid Id,
    string PermissionCode,
    string Effect,
    Guid? LocationId,
    string Reason,
    DateTimeOffset GrantedAtUtc,
    Guid GrantedByUserId,
    DateTimeOffset? ExpiresAtUtc,
    bool IsActive);

/// <summary>A user with everything that shapes their authority.</summary>
public sealed record UserDetail(
    UserSummary User,
    IReadOnlyList<UserLocationSpec> Locations,
    IReadOnlyList<UserOverrideView> Overrides,
    IReadOnlyList<string> EffectivePermissions,
    bool HasPin,
    bool IsLockedOut,
    DateTimeOffset? DisabledAtUtc,
    string? DisabledReason);

/// <summary>What is needed to create an account.</summary>
public sealed record CreateUserRequest(
    string UserName,
    string DisplayName,
    string Password,
    string? Email,
    string? EmployeeCode,
    ApprovalTier ApprovalTier,
    IReadOnlyList<string> Roles,
    IReadOnlyList<UserLocationSpec> Locations);

/// <summary>The editable details of an account.</summary>
public sealed record UpdateUserRequest(
    string DisplayName,
    string? Email,
    string? EmployeeCode,
    ApprovalTier ApprovalTier);

/// <summary>A permission granted to or withheld from one user.</summary>
public sealed record GrantOverrideRequest(
    string PermissionCode,
    PermissionEffect Effect,
    string Reason,
    DateTimeOffset? ExpiresAtUtc,
    Guid? LocationId);

/// <summary>A role as shown to an administrator.</summary>
public sealed record RoleView(
    Guid Id,
    string Name,
    string Description,
    bool IsSystemRole,
    IReadOnlyList<string> Permissions,
    int MemberCount);

/// <summary>A catalogue permission as shown to an administrator.</summary>
public sealed record PermissionView(
    string Code,
    string Module,
    string Description,
    bool IsOfflineCapable,
    bool IsReadOnly,
    bool IsPrivileged);

/// <summary>The enrolment details an authenticator app needs.</summary>
/// <param name="SharedKey">The secret, grouped for manual entry.</param>
/// <param name="AuthenticatorUri">An <c>otpauth://</c> URI to render as a QR code.</param>
public sealed record TwoFactorSetup(string SharedKey, string AuthenticatorUri);

/// <summary>Manages user accounts and their authority.</summary>
public interface IUserAdministration
{
    /// <summary>Lists accounts.</summary>
    Task<IReadOnlyList<UserSummary>> ListAsync(
        string? search, bool includeInactive, int offset, int limit, CancellationToken cancellationToken);

    /// <summary>Gets one account with its assignments, overrides and effective permissions.</summary>
    Task<Result<UserDetail>> GetAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>Creates an account.</summary>
    Task<Result<UserId>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);

    /// <summary>Changes an account's details.</summary>
    Task<Result> UpdateAsync(UserId userId, UpdateUserRequest request, CancellationToken cancellationToken);

    /// <summary>Disables an account and ends its sessions.</summary>
    Task<Result> DisableAsync(UserId userId, string reason, CancellationToken cancellationToken);

    /// <summary>Re-enables a disabled account.</summary>
    Task<Result> EnableAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>Replaces an account's roles.</summary>
    Task<Result> SetRolesAsync(UserId userId, IReadOnlyList<string> roleNames, CancellationToken cancellationToken);

    /// <summary>Replaces an account's location assignments.</summary>
    Task<Result> SetLocationsAsync(
        UserId userId, IReadOnlyList<UserLocationSpec> locations, CancellationToken cancellationToken);

    /// <summary>Grants or withholds one permission for one account.</summary>
    Task<Result<Guid>> GrantOverrideAsync(
        UserId userId, GrantOverrideRequest request, CancellationToken cancellationToken);

    /// <summary>Removes an override.</summary>
    Task<Result> RemoveOverrideAsync(
        UserId userId, Guid overrideId, string reason, CancellationToken cancellationToken);

    /// <summary>Sets a new password and ends the account's sessions.</summary>
    Task<Result> ResetPasswordAsync(UserId userId, string newPassword, CancellationToken cancellationToken);

    /// <summary>Sets the cashier PIN used at a registered terminal.</summary>
    Task<Result> SetPinAsync(UserId userId, string pin, CancellationToken cancellationToken);

    /// <summary>Clears the account's authenticator so it must enrol again.</summary>
    Task<Result> ResetTwoFactorAsync(UserId userId, string reason, CancellationToken cancellationToken);
}

/// <summary>Manages roles, the named bundles of permissions.</summary>
public interface IRoleAdministration
{
    /// <summary>Lists roles with their permissions and member counts.</summary>
    Task<IReadOnlyList<RoleView>> ListRolesAsync(CancellationToken cancellationToken);

    /// <summary>Lists the permission catalogue.</summary>
    IReadOnlyList<PermissionView> ListPermissions();

    /// <summary>Replaces a role's permissions.</summary>
    Task<Result> SetPermissionsAsync(
        Guid roleId, IReadOnlyList<string> permissionCodes, string reason, CancellationToken cancellationToken);
}

/// <summary>Lets an account enrol an authenticator before its first sign-in.</summary>
public interface ITwoFactorEnrolment
{
    /// <summary>Issues (or re-issues) the authenticator key for an account that has not enrolled.</summary>
    Task<Result<TwoFactorSetup>> BeginAsync(string userName, string password, CancellationToken cancellationToken);

    /// <summary>Confirms the first code, turns two-factor on, and returns one-time recovery codes.</summary>
    Task<Result<IReadOnlyList<string>>> CompleteAsync(
        string userName, string password, string code, CancellationToken cancellationToken);
}

/// <summary>The failures administration can produce. Codes are part of the API contract.</summary>
public static class AdministrationErrors
{
    /// <summary>The user does not exist.</summary>
    public static Error UserUnknown { get; } = Error.NotFound("identity.user_unknown", "The user could not be found.");

    /// <summary>The role does not exist.</summary>
    public static Error RoleUnknown(string role) => Error.Validation(
        "identity.role_unknown", $"There is no role called '{role}'.");

    /// <summary>The override does not exist on this user.</summary>
    public static Error OverrideUnknown { get; } = Error.NotFound(
        "identity.override_unknown", "The override could not be found on this user.");

    /// <summary>The permission code is not in the catalogue.</summary>
    public static Error PermissionUnknown(string code) => Error.Validation(
        "identity.permission_unknown", $"'{code}' is not a permission in the catalogue.");

    /// <summary>Administrators may not change their own authority or account status.</summary>
    public static Error SelfAdministration { get; } = Error.Forbidden(
        "identity.self_administration_forbidden",
        "You cannot change your own roles, locations, overrides, approval tier, PIN, two-factor or account status. Ask another administrator.");

    /// <summary>The change would hand out governance authority the caller does not hold.</summary>
    public static Error PrivilegeEscalation(IEnumerable<string> missing) => Error.Forbidden(
        "identity.privilege_escalation_forbidden",
        "You can only give out governance permissions you hold yourself. Missing: " + string.Join(", ", missing) + ".");

    /// <summary>The approval tier is above the caller's own.</summary>
    public static Error TierAboveCaller { get; } = Error.Forbidden(
        "identity.privilege_escalation_forbidden",
        "You can only give an approval tier up to your own.");

    /// <summary>The target holds governance authority the caller does not.</summary>
    public static Error TargetOutranksCaller { get; } = Error.Forbidden(
        "identity.target_outranks_caller",
        "This account or role holds authority you do not have, so you cannot change it.");

    /// <summary>The change would leave nobody able to manage users and roles.</summary>
    public static Error LastAdministrator { get; } = Error.Conflict(
        "identity.last_administrator",
        "This change would leave no active account able to manage both users and roles.");

    /// <summary>A location is unknown.</summary>
    public static Error LocationUnknown(Guid locationId) => Error.Validation(
        "identity.location_unknown", $"Location {locationId} could not be found.");

    /// <summary>Users are assigned to real places, never to external counterparties.</summary>
    public static Error LocationExternal { get; } = Error.Validation(
        "identity.location_external", "A user cannot be assigned to an external counterparty.");

    /// <summary>At most one location can be the user's home.</summary>
    public static Error PrimaryLocationInvalid { get; } = Error.Validation(
        "identity.primary_location_invalid", "Mark at most one location as the user's home location.");

    /// <summary>The username is taken.</summary>
    public static Error UserNameTaken { get; } = Error.Conflict("identity.username_taken", "That username is already in use.");

    /// <summary>The employee code is taken.</summary>
    public static Error EmployeeCodeTaken { get; } = Error.Conflict(
        "identity.employee_code_taken", "That employee code is already in use.");

    /// <summary>A required field is missing.</summary>
    public static Error Required(string field) => Error.Validation(
        "identity.field_required", $"{field} is required.");

    /// <summary>The password does not meet the policy.</summary>
    public static Error PasswordRejected(IEnumerable<string> reasons) => Error.Validation(
        "identity.password_rejected", string.Join(" ", reasons));

    /// <summary>A PIN must be digits of the configured length.</summary>
    public static Error PinInvalid(int minimumLength) => Error.Validation(
        "identity.pin_invalid", $"A PIN must be at least {minimumLength} digits and contain nothing else.");

    /// <summary>A PIN is only usable with an employee code.</summary>
    public static Error EmployeeCodeRequired { get; } = Error.Validation(
        "identity.employee_code_required", "Give the user an employee code before setting a PIN.");

    /// <summary>The account is already in the requested state.</summary>
    public static Error AlreadyInState(string state) => Error.Conflict(
        "identity.account_state_unchanged", $"The account is already {state}.");

    /// <summary>A reason must be recorded.</summary>
    public static Error ReasonRequired { get; } = Error.Validation(
        "identity.reason_required", "Record why this change is being made (at least 5 characters).");

    /// <summary>Two-factor is already on for this account.</summary>
    public static Error TwoFactorAlreadyEnabled { get; } = Error.Conflict(
        "identity.two_factor_already_enabled", "Two-factor sign-in is already set up for this account.");

    /// <summary>The authenticator code did not match.</summary>
    public static Error TwoFactorCodeInvalid { get; } = Error.Validation(
        "identity.two_factor_code_invalid", "That code did not match. Check the time on your phone and try the next code.");
}
