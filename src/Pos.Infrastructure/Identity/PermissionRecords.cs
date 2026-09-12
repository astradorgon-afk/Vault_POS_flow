namespace Pos.Infrastructure.Identity;

/// <summary>
/// A permission from the code catalogue, materialised as a row.
/// </summary>
/// <remarks>
/// The catalogue is defined in <c>Pos.Application.Identity.Permissions</c> and
/// seeded here. Rows exist so role grants can carry a foreign key, and so the
/// admin interface can list and describe permissions; they are never created by
/// users. A permission that is not in the code catalogue therefore cannot be
/// invented by editing the database and then quietly satisfy a check.
/// </remarks>
public sealed class PermissionRecord
{
    /// <summary>Gets or sets the stable dotted code, which is the primary key.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the module the permission belongs to.</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets what holding it allows.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a device may cache it for offline use.</summary>
    public bool IsOfflineCapable { get; set; }

    /// <summary>Gets or sets a value indicating whether it grants only reading.</summary>
    public bool IsReadOnly { get; set; }
}

/// <summary>Grants one permission to one role.</summary>
public sealed class RolePermissionGrant
{
    /// <summary>Gets or sets the role.</summary>
    public Guid RoleId { get; set; }

    /// <summary>Gets or sets the permission code.</summary>
    public string PermissionCode { get; set; } = string.Empty;

    /// <summary>Gets or sets when the grant was made.</summary>
    public DateTimeOffset GrantedAtUtc { get; set; }

    /// <summary>Gets or sets who made the grant, or null when it was seeded.</summary>
    public Guid? GrantedByUserId { get; set; }
}

/// <summary>
/// A single row holding the authorization policy version.
/// </summary>
/// <remarks>
/// <para>
/// Access tokens carry this number rather than a list of permissions. The API
/// resolves permissions from a cache keyed by <c>(userId, policyVersion)</c>, so
/// bumping the version invalidates every cached permission set at once.
/// </para>
/// <para>
/// That is what makes revocation immediate. If permissions were baked into the
/// token, taking one away would not take effect until the token expired, and a
/// dismissed employee would keep their authority for the rest of its lifetime.
/// </para>
/// </remarks>
public sealed class AuthorizationPolicyVersion
{
    /// <summary>The primary key of the single row.</summary>
    public const int SingletonId = 1;

    /// <summary>Gets or sets the row identifier. Always <see cref="SingletonId"/>.</summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>Gets or sets the current version, incremented on any authorization change.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets when it last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Gets or sets what caused the last change, for diagnostics.</summary>
    public string? LastChangeReason { get; set; }
}
