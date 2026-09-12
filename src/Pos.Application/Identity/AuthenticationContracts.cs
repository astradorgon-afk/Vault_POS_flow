using Pos.Domain.Common;

namespace Pos.Application.Identity;

/// <summary>A password sign-in, from the dashboard or an administrative client.</summary>
/// <param name="UserName">The username or e-mail address.</param>
/// <param name="Password">The password.</param>
/// <param name="TwoFactorCode">The time-based code, where the account requires one.</param>
/// <param name="DeviceId">The device, when signing in on a registered terminal.</param>
/// <param name="IpAddress">The caller's address.</param>
/// <param name="UserAgent">The caller's user agent.</param>
/// <param name="AppVersion">The client application version.</param>
public sealed record PasswordSignInRequest(
    string UserName,
    string Password,
    string? TwoFactorCode = null,
    DeviceId? DeviceId = null,
    string? IpAddress = null,
    string? UserAgent = null,
    string? AppVersion = null);

/// <summary>A cashier sign-in at a shared terminal.</summary>
/// <param name="EmployeeCode">The cashier's short code.</param>
/// <param name="Pin">The PIN.</param>
/// <param name="DeviceId">The terminal. Always required.</param>
/// <param name="IpAddress">The caller's address.</param>
/// <param name="UserAgent">The caller's user agent.</param>
/// <param name="AppVersion">The client application version.</param>
public sealed record PinSignInRequest(
    string EmployeeCode,
    string Pin,
    DeviceId DeviceId,
    string? IpAddress = null,
    string? UserAgent = null,
    string? AppVersion = null);

/// <summary>An exchange of a refresh token for a new pair.</summary>
/// <param name="RefreshToken">The token the client holds.</param>
/// <param name="DeviceId">The device presenting it, when device-bound.</param>
/// <param name="IpAddress">The caller's address.</param>
public sealed record RefreshRequest(
    string RefreshToken,
    DeviceId? DeviceId = null,
    string? IpAddress = null);

/// <summary>A successful sign-in.</summary>
/// <param name="AccessToken">The short-lived bearer token.</param>
/// <param name="AccessTokenExpiresAtUtc">When the access token stops working.</param>
/// <param name="RefreshToken">The single-use token that buys the next pair.</param>
/// <param name="RefreshTokenExpiresAtUtc">When the session finally ends.</param>
/// <param name="UserId">The signed-in user.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Roles">The roles they hold.</param>
/// <param name="Permissions">
/// Their effective permissions. Returned so the client can hide controls it
/// cannot use; the server re-checks every one of them on every request.
/// </param>
/// <param name="Locations">The locations they may act in.</param>
/// <param name="HasAllLocations">Whether they may act business-wide.</param>
/// <param name="PolicyVersion">
/// The authorization version in force. A device compares this with the version
/// on its cached snapshot to know when the snapshot is stale.
/// </param>
public sealed record AuthenticationResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    UserId UserId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<LocationId> Locations,
    bool HasAllLocations,
    long PolicyVersion);

/// <summary>Signs users in and keeps their sessions alive.</summary>
public interface IAuthenticationService
{
    /// <summary>Signs in with a username and password.</summary>
    /// <param name="request">The sign-in request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A token pair, or the reason sign-in was refused.</returns>
    Task<Result<AuthenticationResult>> SignInAsync(
        PasswordSignInRequest request,
        CancellationToken cancellationToken);

    /// <summary>Signs in with an employee code and PIN at a registered terminal.</summary>
    /// <param name="request">The sign-in request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A token pair, or the reason sign-in was refused.</returns>
    Task<Result<AuthenticationResult>> SignInWithPinAsync(
        PinSignInRequest request,
        CancellationToken cancellationToken);

    /// <summary>Exchanges a refresh token for a new pair.</summary>
    /// <param name="request">The refresh request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new token pair, or the reason the exchange was refused.</returns>
    Task<Result<AuthenticationResult>> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken);

    /// <summary>Ends a session by revoking its token family.</summary>
    /// <param name="refreshToken">The token identifying the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, whether or not the token was still live.</returns>
    Task<Result> SignOutAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>Revokes every session belonging to a user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many sessions were ended.</returns>
    Task<Result<int>> RevokeAllSessionsAsync(
        UserId userId,
        string reason,
        CancellationToken cancellationToken);
}

/// <summary>The failures sign-in and token exchange can produce.</summary>
/// <remarks>
/// Invalid credentials deliberately produce one code whether the account exists
/// or not. Telling an attacker which usernames are real turns a password guess
/// into a two-step problem they can solve half of for free.
/// </remarks>
public static class AuthenticationErrors
{
    /// <summary>The username, PIN or password did not match.</summary>
    public static Error InvalidCredentials { get; } = new(
        "auth.invalid_credentials",
        "The credentials supplied are not valid.",
        ErrorType.Unauthenticated);

    /// <summary>The account is temporarily locked after repeated failures.</summary>
    public static Error LockedOut { get; } = new(
        "auth.locked_out",
        "This account is temporarily locked. Try again later or ask an administrator.",
        ErrorType.Unauthenticated);

    /// <summary>The account has been disabled.</summary>
    public static Error AccountDisabled { get; } = new(
        "auth.account_disabled",
        "This account has been disabled.",
        ErrorType.Unauthenticated);

    /// <summary>A second factor is required and was missing or wrong.</summary>
    public static Error TwoFactorRequired { get; } = new(
        "auth.two_factor_required",
        "A verification code is required for this account.",
        ErrorType.Unauthenticated);

    /// <summary>Too many attempts from this address or for this account.</summary>
    public static Error TooManyAttempts { get; } = new(
        "auth.too_many_attempts",
        "Too many attempts. Wait a few minutes and try again.",
        ErrorType.Unauthenticated);

    /// <summary>The device is not enrolled, is suspended, or has been revoked.</summary>
    public static Error DeviceNotOperational { get; } = new(
        "auth.device_not_operational",
        "This device is not permitted to sign in. Contact an administrator.",
        ErrorType.Forbidden);

    /// <summary>The user is not assigned to the location the device belongs to.</summary>
    public static Error LocationNotPermitted { get; } = new(
        "auth.location_not_permitted",
        "You are not assigned to this location.",
        ErrorType.Forbidden);

    /// <summary>The refresh token is unknown, expired or already used.</summary>
    public static Error InvalidRefreshToken { get; } = new(
        "auth.refresh_token_invalid",
        "This session has expired. Sign in again.",
        ErrorType.Unauthenticated);

    /// <summary>A rotated refresh token was presented again, so the family was burned.</summary>
    public static Error RefreshTokenReuse { get; } = new(
        "auth.refresh_token_reuse",
        "This session has been ended for security reasons. Sign in again.",
        ErrorType.Unauthenticated);

    /// <summary>The account has no PIN configured.</summary>
    public static Error PinNotConfigured { get; } = new(
        "auth.pin_not_configured",
        "This account cannot sign in with a PIN.",
        ErrorType.Unauthenticated);
}
