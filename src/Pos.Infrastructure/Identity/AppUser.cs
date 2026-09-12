using Microsoft.AspNetCore.Identity;
using Pos.Domain.Identity;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// A person who can sign in.
/// </summary>
/// <remarks>
/// <para>
/// This type lives in infrastructure rather than the domain because it inherits
/// from ASP.NET Core Identity, and the domain deliberately has no framework
/// dependencies. The domain refers to people by <c>UserId</c> only; everything
/// it needs to reason about — location assignments, permission overrides,
/// approval authority — is modelled there as framework-free types.
/// </para>
/// <para>
/// Identity supplies the parts that are genuinely hard to get right on your own:
/// password hashing with a versioned format, the security stamp that invalidates
/// outstanding tokens, lockout, and two-factor.
/// </para>
/// </remarks>
public sealed class AppUser : IdentityUser<Guid>
{
    /// <summary>Gets or sets the name shown in the interface and on receipts.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short code a cashier types at a shared terminal, for
    /// example <c>1042</c>. Unique, and separate from the username so a cashier
    /// never types an e-mail address on a touch screen.
    /// </summary>
    public string? EmployeeCode { get; set; }

    /// <summary>
    /// Gets or sets the hashed cashier PIN.
    /// </summary>
    /// <remarks>
    /// Hashed with the same Identity hasher as the password, and never stored or
    /// logged in the clear. A PIN is a real credential, not a convenience code:
    /// it is rate-limited per device and only valid at a location the user is
    /// assigned to.
    /// </remarks>
    public string? PinHash { get; set; }

    /// <summary>Gets or sets when the PIN was last changed.</summary>
    public DateTimeOffset? PinChangedAtUtc { get; set; }

    /// <summary>Gets or sets the highest value this user may approve.</summary>
    public ApprovalTier ApprovalTier { get; set; } = ApprovalTier.None;

    /// <summary>Gets or sets a value indicating whether the account may be used.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets or sets when the account was disabled.</summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }

    /// <summary>Gets or sets why the account was disabled.</summary>
    public string? DisabledReason { get; set; }

    /// <summary>Gets or sets when the account was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Gets or sets who created the account.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Gets or sets when the user last signed in successfully.</summary>
    public DateTimeOffset? LastLoginAtUtc { get; set; }

    /// <summary>Gets a value indicating whether this account can authenticate at all.</summary>
    public bool CanAuthenticate => IsActive && DisabledAtUtc is null;
}

/// <summary>
/// A named bundle of permissions.
/// </summary>
/// <remarks>
/// No code branches on a role name; roles exist so that administering dozens of
/// permissions across dozens of people stays tractable. Authorization always
/// asks for a permission.
/// </remarks>
public sealed class AppRole : IdentityRole<Guid>
{
    /// <summary>Gets or sets what the role is for, shown in the admin interface.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this is one of the seeded roles.
    /// System roles cannot be deleted, because removing one would silently strip
    /// authority from everyone holding it.
    /// </summary>
    public bool IsSystemRole { get; set; }

    /// <summary>Gets or sets when the role was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
