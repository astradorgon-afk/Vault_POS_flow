using Pos.Domain.Common;

namespace Pos.Domain.Identity;

/// <summary>How a sign-in was attempted.</summary>
public enum LoginMethod
{
    /// <summary>Username and password, from the web dashboard or an admin client.</summary>
    Password = 0,

    /// <summary>Employee code and PIN, from a shared point-of-sale device.</summary>
    Pin = 1,

    /// <summary>A refresh token exchange.</summary>
    RefreshToken = 2,
}

/// <summary>Why a sign-in was refused.</summary>
public enum LoginFailureReason
{
    /// <summary>The attempt succeeded.</summary>
    None = 0,

    /// <summary>No such account, or the wrong secret. Deliberately not distinguished.</summary>
    InvalidCredentials = 1,

    /// <summary>The account is locked out after repeated failures.</summary>
    LockedOut = 2,

    /// <summary>The account is disabled.</summary>
    Disabled = 3,

    /// <summary>The device is suspended, revoked or not enrolled.</summary>
    DeviceNotOperational = 4,

    /// <summary>The user is not assigned to the device's location.</summary>
    LocationNotPermitted = 5,

    /// <summary>A second factor is required and was missing or wrong.</summary>
    TwoFactorRequired = 6,

    /// <summary>Too many attempts from this address or for this account.</summary>
    RateLimited = 7,
}

/// <summary>
/// One sign-in attempt, successful or not.
/// </summary>
/// <remarks>
/// <para>
/// The identifier that was tried is stored only as a hash. Recording the raw
/// value would turn this table into a list of usernames for anyone who reads the
/// logs, and would capture a password typed into the username box by mistake.
/// The hash is enough to count failures per account, which is what throttling
/// needs.
/// </para>
/// <para>
/// Failures are counted per account *and* per address, so an attacker cannot
/// lock a cashier out of their own till by guessing at their account all
/// morning.
/// </para>
/// </remarks>
public sealed class LoginAttempt : Entity<Guid>
{
    private LoginAttempt(
        Guid id,
        byte[] identifierHash,
        UserId? userId,
        DeviceId? deviceId,
        LoginMethod method,
        bool succeeded,
        LoginFailureReason failureReason,
        DateTimeOffset attemptedAtUtc,
        string? ipAddress,
        string? userAgent)
    {
        Id = id;
        IdentifierHash = identifierHash;
        UserId = userId;
        DeviceId = deviceId;
        Method = method;
        Succeeded = succeeded;
        FailureReason = failureReason;
        AttemptedAtUtc = attemptedAtUtc;
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private LoginAttempt()
    {
        IdentifierHash = [];
    }

    /// <summary>Gets the SHA-256 hash of the identifier that was tried.</summary>
    public byte[] IdentifierHash { get; private init; }

    /// <summary>Gets the matched user, when the identifier resolved to one.</summary>
    public UserId? UserId { get; private init; }

    /// <summary>Gets the device the attempt came from, if any.</summary>
    public DeviceId? DeviceId { get; private init; }

    /// <summary>Gets how the sign-in was attempted.</summary>
    public LoginMethod Method { get; private init; }

    /// <summary>Gets a value indicating whether the attempt succeeded.</summary>
    public bool Succeeded { get; private init; }

    /// <summary>Gets why the attempt was refused.</summary>
    public LoginFailureReason FailureReason { get; private init; }

    /// <summary>Gets when the attempt was made, on the server clock.</summary>
    public DateTimeOffset AttemptedAtUtc { get; private init; }

    /// <summary>Gets the caller's address.</summary>
    public string? IpAddress { get; private init; }

    /// <summary>Gets the caller's user agent.</summary>
    public string? UserAgent { get; private init; }

    /// <summary>Records a successful sign-in.</summary>
    /// <param name="identifierHash">Hash of the identifier used.</param>
    /// <param name="userId">The user.</param>
    /// <param name="method">How they signed in.</param>
    /// <param name="attemptedAtUtc">Server time.</param>
    /// <param name="deviceId">The device, if any.</param>
    /// <param name="ipAddress">Caller address.</param>
    /// <param name="userAgent">Caller user agent.</param>
    /// <returns>The record.</returns>
    public static LoginAttempt Success(
        byte[] identifierHash,
        UserId userId,
        LoginMethod method,
        DateTimeOffset attemptedAtUtc,
        DeviceId? deviceId = null,
        string? ipAddress = null,
        string? userAgent = null)
        => new(
            Guid.CreateVersion7(),
            identifierHash,
            userId,
            deviceId,
            method,
            succeeded: true,
            LoginFailureReason.None,
            attemptedAtUtc,
            ipAddress,
            userAgent);

    /// <summary>Records a refused sign-in.</summary>
    /// <param name="identifierHash">Hash of the identifier used.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <param name="method">How they tried to sign in.</param>
    /// <param name="attemptedAtUtc">Server time.</param>
    /// <param name="userId">The user, when the identifier resolved to one.</param>
    /// <param name="deviceId">The device, if any.</param>
    /// <param name="ipAddress">Caller address.</param>
    /// <param name="userAgent">Caller user agent.</param>
    /// <returns>The record.</returns>
    public static LoginAttempt Failure(
        byte[] identifierHash,
        LoginFailureReason reason,
        LoginMethod method,
        DateTimeOffset attemptedAtUtc,
        UserId? userId = null,
        DeviceId? deviceId = null,
        string? ipAddress = null,
        string? userAgent = null)
        => new(
            Guid.CreateVersion7(),
            identifierHash,
            userId,
            deviceId,
            method,
            succeeded: false,
            reason,
            attemptedAtUtc,
            ipAddress,
            userAgent);
}
