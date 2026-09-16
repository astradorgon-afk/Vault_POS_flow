namespace Pos.Web.Services;

/// <summary>Holds the authenticated API session for one Blazor circuit.</summary>
public sealed class UserSession
{
    /// <summary>Gets the current sign-in response.</summary>
    public SignInResponse? Current { get; private set; }

    /// <summary>Gets whether the access token is still usable.</summary>
    public bool IsAuthenticated => Current is { } current && current.AccessTokenExpiresAtUtc > DateTimeOffset.UtcNow;

    /// <summary>Gets the register the circuit sells through, or null.</summary>
    public Guid? DeviceId { get; private set; }

    /// <summary>Gets the register short code, or null.</summary>
    public string? DeviceShortCode { get; private set; }

    /// <summary>Gets the register name, or null.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Replaces the current session.</summary>
    public void Set(SignInResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Current = response;
    }

    /// <summary>Selects the register the circuit sells through.</summary>
    public void SetRegister(PosRegister register)
    {
        ArgumentNullException.ThrowIfNull(register);
        DeviceId = register.Id;
        DeviceShortCode = register.ShortCode;
        DeviceName = register.Name;
    }

    /// <summary>Clears the register context.</summary>
    public void ClearRegister()
    {
        DeviceId = null;
        DeviceShortCode = null;
        DeviceName = null;
    }

    /// <summary>Clears the current session and register context.</summary>
    public void Clear()
    {
        Current = null;
        ClearRegister();
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
    Guid Id,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> Locations,
    bool HasAllLocations,
    long PolicyVersion);
