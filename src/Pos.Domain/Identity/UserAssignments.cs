using Pos.Domain.Common;

namespace Pos.Domain.Identity;

/// <summary>Whether an override adds a permission or takes one away.</summary>
public enum PermissionEffect
{
    /// <summary>Adds the permission on top of the user's roles.</summary>
    Grant = 0,

    /// <summary>
    /// Removes the permission regardless of what any role grants. Deny always
    /// wins, so a single override can suspend one capability without unpicking
    /// someone's roles.
    /// </summary>
    Deny = 1,
}

/// <summary>
/// The value bands that decide who may approve an action.
/// </summary>
/// <remarks>
/// Stored as <see cref="short"/> on the user's grant; values are part of the
/// database contract and must never be renumbered.
/// </remarks>
public enum ApprovalTier
{
    /// <summary>No approval authority at all.</summary>
    None = 0,

    /// <summary>Store-manager level. Small, local corrections.</summary>
    Tier1 = 1,

    /// <summary>Main-inventory-manager level.</summary>
    Tier2 = 2,

    /// <summary>Administrator level.</summary>
    Tier3 = 3,

    /// <summary>Owner. No ceiling.</summary>
    Unlimited = 4,
}

/// <summary>
/// Binds a user to a location they may act in.
/// </summary>
/// <remarks>
/// This is what confines a store manager to their own store. A permission check
/// is always a pair of (permission, location): holding
/// <c>inventory.adjust.approve</c> means nothing at a location the user is not
/// assigned to, unless they also hold <c>location.all</c>.
/// </remarks>
public sealed class UserLocationAssignment
{
    private UserLocationAssignment(
        UserId userId,
        LocationId locationId,
        bool isPrimary,
        DateTimeOffset assignedAtUtc,
        UserId assignedByUserId)
    {
        UserId = userId;
        LocationId = locationId;
        IsPrimary = isPrimary;
        AssignedAtUtc = assignedAtUtc;
        AssignedByUserId = assignedByUserId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private UserLocationAssignment()
    {
    }

    /// <summary>Gets the user.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets the location.</summary>
    public LocationId LocationId { get; private init; }

    /// <summary>Gets a value indicating whether this is the user's home location.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>Gets when the assignment was made.</summary>
    public DateTimeOffset AssignedAtUtc { get; private init; }

    /// <summary>Gets who made the assignment.</summary>
    public UserId AssignedByUserId { get; private init; }

    /// <summary>Creates an assignment.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="locationId">The location.</param>
    /// <param name="isPrimary">Whether this is the user's home location.</param>
    /// <param name="assignedAtUtc">When the assignment was made.</param>
    /// <param name="assignedByUserId">Who made it.</param>
    /// <returns>The assignment.</returns>
    public static UserLocationAssignment Create(
        UserId userId,
        LocationId locationId,
        bool isPrimary,
        DateTimeOffset assignedAtUtc,
        UserId assignedByUserId)
        => new(userId, locationId, isPrimary, assignedAtUtc, assignedByUserId);

    /// <summary>Marks or unmarks this as the user's home location.</summary>
    /// <param name="isPrimary">Whether this is the home location.</param>
    public void SetPrimary(bool isPrimary) => IsPrimary = isPrimary;
}

/// <summary>
/// A permission granted to or withheld from one user, on top of their roles.
/// </summary>
/// <remarks>
/// Overrides exist so a one-off need does not turn into a permanent role
/// change: a cashier can be granted <c>sale.void</c> for a single shift, with an
/// expiry, rather than being promoted and never demoted. Every override carries
/// a reason and is audited.
/// </remarks>
public sealed class UserPermissionOverride : Entity<Guid>
{
    private UserPermissionOverride(
        Guid id,
        UserId userId,
        string permissionCode,
        PermissionEffect effect,
        DateTimeOffset grantedAtUtc,
        UserId grantedByUserId,
        string reason,
        DateTimeOffset? expiresAtUtc,
        LocationId? locationId)
    {
        Id = id;
        UserId = userId;
        PermissionCode = permissionCode;
        Effect = effect;
        GrantedAtUtc = grantedAtUtc;
        GrantedByUserId = grantedByUserId;
        Reason = reason;
        ExpiresAtUtc = expiresAtUtc;
        LocationId = locationId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private UserPermissionOverride()
    {
        PermissionCode = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Gets the user the override applies to.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets the permission code.</summary>
    public string PermissionCode { get; private init; }

    /// <summary>Gets whether the permission is added or withheld.</summary>
    public PermissionEffect Effect { get; private init; }

    /// <summary>Gets when the override was created.</summary>
    public DateTimeOffset GrantedAtUtc { get; private init; }

    /// <summary>Gets who created it.</summary>
    public UserId GrantedByUserId { get; private init; }

    /// <summary>Gets why it was created. Always required.</summary>
    public string Reason { get; private init; }

    /// <summary>Gets when it stops applying, or null for indefinite.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; private init; }

    /// <summary>
    /// Gets the location the override is confined to, or null to apply
    /// everywhere the user is assigned.
    /// </summary>
    public LocationId? LocationId { get; private init; }

    /// <summary>Creates an override.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="permissionCode">The permission code.</param>
    /// <param name="effect">Grant or deny.</param>
    /// <param name="grantedAtUtc">When it was created.</param>
    /// <param name="grantedByUserId">Who created it.</param>
    /// <param name="reason">Why. Required.</param>
    /// <param name="expiresAtUtc">When it lapses, if ever.</param>
    /// <param name="locationId">Location scope, if narrower than the user's.</param>
    /// <returns>The override, or a validation failure.</returns>
    public static Result<UserPermissionOverride> Create(
        UserId userId,
        string permissionCode,
        PermissionEffect effect,
        DateTimeOffset grantedAtUtc,
        UserId grantedByUserId,
        string reason,
        DateTimeOffset? expiresAtUtc = null,
        LocationId? locationId = null)
    {
        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            return Result<UserPermissionOverride>.Failure(Error.Validation(
                "identity.override_permission_required",
                "A permission code is required."));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result<UserPermissionOverride>.Failure(Error.Validation(
                "identity.override_reason_required",
                "An override must record why it was granted."));
        }

        if (expiresAtUtc is { } expiry && expiry <= grantedAtUtc)
        {
            return Result<UserPermissionOverride>.Failure(Error.Validation(
                "identity.override_expiry_in_past",
                "An override cannot expire before it is granted."));
        }

        return Result<UserPermissionOverride>.Success(new UserPermissionOverride(
            Guid.CreateVersion7(),
            userId,
            permissionCode.Trim(),
            effect,
            grantedAtUtc,
            grantedByUserId,
            reason.Trim(),
            expiresAtUtc,
            locationId));
    }

    /// <summary>Determines whether the override is in force at a moment.</summary>
    /// <param name="atUtc">The moment to test.</param>
    /// <returns><see langword="true"/> when it still applies.</returns>
    public bool IsActiveAt(DateTimeOffset atUtc) => ExpiresAtUtc is null || ExpiresAtUtc > atUtc;

    /// <summary>Determines whether the override applies at a location.</summary>
    /// <param name="locationId">The location being acted on, if any.</param>
    /// <returns><see langword="true"/> when it applies there.</returns>
    public bool AppliesTo(LocationId? locationId)
        => LocationId is null || locationId is null || LocationId == locationId;
}
