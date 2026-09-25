using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// What this register remembers about one person so it can check their password
/// while head office is unreachable: a salted PBKDF2 verifier of the password
/// they last signed in with here, never the password and never the server's hash.
/// </summary>
/// <remarks>
/// It is written only after head office itself accepted the password, so a
/// register can never learn a credential head office would refuse. It is a
/// device-owned record, not a feed-owned cache: the change feed never writes it,
/// and a baseline does not wipe it.
/// </remarks>
public sealed class DeviceOfflineCredential
{
    private DeviceOfflineCredential()
    {
        UserName = string.Empty;
        LoginName = string.Empty;
        DisplayName = string.Empty;
        Salt = [];
        Verifier = [];
    }

    internal DeviceOfflineCredential(
        UserId userId,
        string userName,
        string loginName,
        string displayName,
        byte[] salt,
        byte[] verifier,
        int iterations,
        DateTimeOffset recordedAtUtc)
    {
        UserId = userId;
        UserName = userName;
        LoginName = loginName;
        DisplayName = displayName;
        Salt = salt;
        Verifier = verifier;
        Iterations = iterations;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Gets the person this verifier belongs to.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets their username, normalized for lookup.</summary>
    public string UserName { get; private set; }

    /// <summary>Gets what they typed at their last connected sign-in (a username or an e-mail), normalized.</summary>
    public string LoginName { get; private set; }

    /// <summary>Gets their display name as head office last described them.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Gets the random per-verifier salt.</summary>
    public byte[] Salt { get; private set; }

    /// <summary>Gets the PBKDF2-SHA256 output.</summary>
    public byte[] Verifier { get; private set; }

    /// <summary>Gets the PBKDF2 iteration count the verifier was derived with.</summary>
    public int Iterations { get; private set; }

    /// <summary>Gets when head office last accepted this password here.</summary>
    public DateTimeOffset RecordedAtUtc { get; private set; }

    /// <summary>Gets the offline attempts that failed since the last success.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Gets when offline sign-in may be tried again after too many failures.</summary>
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    internal void Replace(
        string userName,
        string loginName,
        string displayName,
        byte[] salt,
        byte[] verifier,
        int iterations,
        DateTimeOffset recordedAtUtc)
    {
        UserName = userName;
        LoginName = loginName;
        DisplayName = displayName;
        Salt = salt;
        Verifier = verifier;
        Iterations = iterations;
        RecordedAtUtc = recordedAtUtc;
        FailedAttempts = 0;
        LockedUntilUtc = null;
    }

    internal bool IsLockedAt(DateTimeOffset now) => LockedUntilUtc is { } until && until > now;

    /// <summary>Counts a failed attempt, locking the verifier once the limit is reached.</summary>
    /// <returns>True when this attempt locked it.</returns>
    internal bool RecordFailure(DateTimeOffset now, int limit, TimeSpan lockout)
    {
        FailedAttempts++;
        if (FailedAttempts < limit)
        {
            return false;
        }

        FailedAttempts = 0;
        LockedUntilUtc = now + lockout;
        return true;
    }

    internal void RecordSuccess()
    {
        FailedAttempts = 0;
        LockedUntilUtc = null;
    }
}

/// <summary>Who an offline sign-in let in, and what their cached authority allows here.</summary>
/// <param name="UserId">The person.</param>
/// <param name="UserName">Their username.</param>
/// <param name="DisplayName">Their display name.</param>
/// <param name="Permissions">Their unexpired, offline-capable permissions at this register's store.</param>
/// <param name="AuthorityExpiresAtUtc">When the first of those permissions runs out.</param>
public sealed record OfflineSignInGrant(
    UserId UserId,
    string UserName,
    string DisplayName,
    IReadOnlyList<string> Permissions,
    DateTimeOffset AuthorityExpiresAtUtc);

/// <summary>Why a register refused an offline sign-in, in words a cashier can act on.</summary>
public static class OfflineSignInErrors
{
    /// <summary>The register has never seen this person sign in while connected.</summary>
    public static readonly Error NotRemembered = Error.Forbidden(
        "offline.sign_in_not_remembered",
        "Head office can’t be reached, and this account hasn’t signed in on this register while it was connected. " +
        "Sign in once with a connection to be able to sign in here offline.");

    /// <summary>The password does not match the one last accepted here.</summary>
    public static readonly Error CredentialsInvalid = Error.Forbidden(
        "offline.credentials_invalid",
        "Head office can’t be reached, and that password doesn’t match the one last used on this register.");

    /// <summary>Too many failed attempts.</summary>
    public static readonly Error LockedOut = Error.Forbidden(
        "offline.locked_out",
        "Too many failed attempts on this register. Wait a few minutes and try again.");

    /// <summary>The account is no longer active, or head office no longer lists it for this store.</summary>
    public static readonly Error AccountUnavailable = Error.Forbidden(
        "offline.account_unavailable",
        "This account can’t be used on this register offline. Connect to head office to sign in.");

    /// <summary>The person has no cached authority at this store.</summary>
    public static readonly Error NoOfflineAuthority = Error.Forbidden(
        "offline.no_authority",
        "This account has nothing it may do on this register offline. Connect to head office to sign in.");

    /// <summary>The cached authority ran out.</summary>
    public static readonly Error AuthorityExpired = Error.Forbidden(
        "offline.authority_expired",
        "Your offline access on this register has expired. Connect to head office and sign in once to renew it.");

    /// <summary>The register has no identity yet.</summary>
    public static readonly Error NotEnrolled = Error.Conflict(
        "offline.not_enrolled",
        "This register is not set up yet, so it can’t sign anyone in offline.");
}

/// <summary>
/// Signs people in at a register while head office cannot be reached, and
/// remembers them whenever head office signs them in (SECURITY.md §2.5, ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// Three things must all hold, and each fails closed. The password must match
/// the verifier written the last time head office accepted it on this register.
/// The person must still be listed, and active, in the store data head office
/// last sent — a baseline drops anyone who has been disabled or moved away. And
/// they must hold unexpired offline authority at this store, because the
/// snapshot's expiry is what bounds how long a register may act on stale
/// knowledge. An offline sign-in therefore never grants more than the cached
/// snapshot, which is itself only offline-capable permissions (ADR-0011).
/// </para>
/// <para>
/// Repeated failures lock the verifier for a few minutes. The lock is stored,
/// so restarting the app does not reset it.
/// </para>
/// </remarks>
/// <param name="database">The encrypted device store.</param>
/// <param name="clock">The device clock.</param>
/// <param name="iterations">The PBKDF2 work factor for new verifiers.</param>
public sealed class DeviceOfflineSignIn(
    DeviceDatabaseInitializer database,
    ISystemClock clock,
    int iterations = DeviceOfflineSignIn.DefaultIterations)
{
    /// <summary>PBKDF2-HMAC-SHA256 iterations: the OWASP recommendation for that function.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Failed attempts allowed before the verifier locks.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>How long a locked verifier stays locked.</summary>
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private const int SaltBytes = 16;
    private const int VerifierBytes = 32;

    /// <summary>
    /// Remembers a password head office has just accepted, replacing whatever
    /// was remembered for this person before.
    /// </summary>
    /// <param name="userId">The person head office signed in.</param>
    /// <param name="loginName">What they typed as their username.</param>
    /// <param name="displayName">Their display name.</param>
    /// <param name="password">The password head office accepted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the verifier is stored.</returns>
    public async Task RememberAsync(
        UserId userId,
        string loginName,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        int work = iterations;
        byte[] verifier = await Task.Run(() => Derive(password, salt, work), cancellationToken).ConfigureAwait(false);

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The username head office knows them by, when the store data has it:
        // they may have typed an e-mail address, and offline they may type either.
        string? canonical = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.UserName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        string login = Normalize(loginName);
        string userName = canonical is { Length: > 0 } ? Normalize(canonical) : login;
        string name = string.IsNullOrWhiteSpace(displayName) ? loginName.Trim() : displayName.Trim();

        // A username can move to another person; the newest acceptance wins.
        await context.OfflineCredentials
            .Where(c => c.UserId != userId
                        && (c.UserName == userName || c.LoginName == login
                            || c.UserName == login || c.LoginName == userName))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        DeviceOfflineCredential? existing = await context.OfflineCredentials
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.OfflineCredentials.Add(new DeviceOfflineCredential(
                userId, userName, login, name, salt, verifier, work, clock.UtcNow));
        }
        else
        {
            existing.Replace(userName, login, name, salt, verifier, work, clock.UtcNow);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks a sign-in against what this register remembers.</summary>
    /// <param name="loginName">The username or e-mail typed.</param>
    /// <param name="password">The password typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The person and their offline authority here, or why they cannot sign in.</returns>
    public async Task<Result<OfflineSignInGrant>> SignInAsync(
        string loginName,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrEmpty(password))
        {
            return Result<OfflineSignInGrant>.Failure(OfflineSignInErrors.CredentialsInvalid);
        }

        string login = Normalize(loginName);

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeviceStoreProfile? profile = await context.DeviceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return Result<OfflineSignInGrant>.Failure(OfflineSignInErrors.NotEnrolled);
        }

        DeviceOfflineCredential? credential = await context.OfflineCredentials
            .FirstOrDefaultAsync(c => c.UserName == login || c.LoginName == login, cancellationToken)
            .ConfigureAwait(false);
        if (credential is null)
        {
            return Result<OfflineSignInGrant>.Failure(OfflineSignInErrors.NotRemembered);
        }

        DateTimeOffset now = clock.UtcNow;
        if (credential.IsLockedAt(now))
        {
            return Result<OfflineSignInGrant>.Failure(OfflineSignInErrors.LockedOut);
        }

        byte[] salt = credential.Salt;
        int work = credential.Iterations;
        byte[] attempt = await Task.Run(() => Derive(password, salt, work), cancellationToken).ConfigureAwait(false);

        if (!CryptographicOperations.FixedTimeEquals(attempt, credential.Verifier))
        {
            bool locked = credential.RecordFailure(now, MaxFailedAttempts, Lockout);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<OfflineSignInGrant>.Failure(
                locked ? OfflineSignInErrors.LockedOut : OfflineSignInErrors.CredentialsInvalid);
        }

        if (credential.FailedAttempts > 0 || credential.LockedUntilUtc is not null)
        {
            credential.RecordSuccess();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // The last store data from head office must still list them as active.
        // A disabled or reassigned account drops out of the next baseline, and
        // with it any way to sign in here without asking head office.
        DeviceCachedUser? user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == credential.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is not { IsActive: true })
        {
            return Result<OfflineSignInGrant>.Failure(OfflineSignInErrors.AccountUnavailable);
        }

        LocationId store = profile.LocationId;
        List<DevicePermissionSnapshot> held = await context.PermissionSnapshots
            .AsNoTracking()
            .Where(s => s.UserId == credential.UserId && (s.LocationId == null || s.LocationId == store))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DevicePermissionSnapshot> usable =
        [
            .. held.Where(s => s.ExpiresAtUtc > now && Permissions.Find(s.Permission)?.IsOfflineCapable == true),
        ];

        if (usable.Count == 0)
        {
            return Result<OfflineSignInGrant>.Failure(
                held.Count > 0 ? OfflineSignInErrors.AuthorityExpired : OfflineSignInErrors.NoOfflineAuthority);
        }

        return Result<OfflineSignInGrant>.Success(new OfflineSignInGrant(
            credential.UserId,
            user.UserName,
            string.IsNullOrWhiteSpace(user.DisplayName) ? credential.DisplayName : user.DisplayName,
            [.. usable.Select(s => s.Permission).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            usable.Min(s => s.ExpiresAtUtc)));
    }

    private static string Normalize(string loginName) => loginName.Trim().ToUpperInvariant();

    private static byte[] Derive(string password, byte[] salt, int work)
    {
        byte[] secret = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(secret, salt, work, HashAlgorithmName.SHA256, VerifierBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
