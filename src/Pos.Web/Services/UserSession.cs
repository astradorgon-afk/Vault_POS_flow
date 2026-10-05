namespace Pos.Web.Services;

/// <summary>Holds the authenticated API session for one Blazor circuit.</summary>
public sealed class UserSession
{
    /// <summary>Gets the current sign-in response.</summary>
    public SignInResponse? Current { get; private set; }

    /// <summary>The optional work location chosen during sign-in.</summary>
    public Guid? WorkLocationId { get; private set; }

    /// <summary>The name shown for the chosen work location.</summary>
    public string? WorkLocationName { get; private set; }

    /// <summary>Gets whether the access token is still usable.</summary>
    public bool IsAuthenticated => Current is { } current && current.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow;

    /// <summary>Determines whether the current authorization snapshot contains a permission.</summary>
    /// <param name="permission">The stable permission code.</param>
    /// <returns><see langword="true"/> when the permission is effective for this session.</returns>
    public bool HasPermission(string permission)
        => Current?.User.Permissions.Contains(permission, StringComparer.Ordinal) == true;

    /// <summary>Determines whether any supplied permission is effective for this session.</summary>
    /// <param name="permissions">The stable permission codes.</param>
    /// <returns><see langword="true"/> when at least one permission is effective.</returns>
    public bool HasAnyPermission(params string[] permissions)
        => permissions.Any(HasPermission);

    /// <summary>Limits web inventory views to assigned locations, except location administrators.</summary>
    public bool CanUseInventoryLocation(Guid locationId)
        => Current is { } current &&
           (current.User.Locations.Contains(locationId) ||
            current.User.HasAllLocations && HasPermission("location.manage"));

    /// <summary>Replaces the current session.</summary>
    public void Set(SignInResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Current = response;
        WorkLocationId = null;
        WorkLocationName = null;
    }

    /// <summary>Sets an optional starting location within the signed-in user's scope.</summary>
    public bool SetWorkLocation(Guid? locationId, string? name = null)
    {
        if (locationId is { } id &&
            (Current is null || !Current.User.HasAllLocations && !Current.User.Locations.Contains(id)))
        {
            return false;
        }

        WorkLocationId = locationId;
        WorkLocationName = locationId is null ? null : name;
        return true;
    }

    /// <summary>Clears the current session.</summary>
    public void Clear()
    {
        Current = null;
        WorkLocationId = null;
        WorkLocationName = null;
    }
}

/// <summary>A successful authentication response from the API.</summary>
public sealed record SignInResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    SignedInUser User);

/// <summary>The signed-in user's client-safe authorization snapshot.</summary>
public sealed record SignedInUser(
    [property: System.Text.Json.Serialization.JsonPropertyName("userId")] Guid Id,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> Locations,
    bool HasAllLocations,
    long PolicyVersion);
