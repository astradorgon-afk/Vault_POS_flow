using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Signs users in, keeps sessions alive, and ends them.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal takes the same shape and the same amount of work whether the
/// account exists or not, so the endpoint cannot be used to enumerate
/// usernames.
/// </para>
/// <para>
/// Every attempt, successful or not, is recorded. Throttling counts failures per
/// account and per address separately: locking an account after five bad guesses
/// protects the password, and limiting an address stops one machine from working
/// through a staff list — but the account lock alone would let an attacker deny
/// a cashier their own till by guessing at it all morning.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="users">Identity's user manager.</param>
/// <param name="tokens">The token service.</param>
/// <param name="permissions">The permission evaluator.</param>
/// <param name="policyVersion">The authorization policy version.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="jwtOptions">Token settings.</param>
/// <param name="securityOptions">Authentication hardening settings.</param>
/// <param name="audit">The audit writer.</param>
/// <param name="logger">Logger.</param>
public sealed class AuthenticationService(
    PosDbContext context,
    UserManager<AppUser> users,
    ITokenService tokens,
    DatabasePermissionEvaluator permissions,
    IPolicyVersionProvider policyVersion,
    ISystemClock clock,
    IOptions<JwtOptions> jwtOptions,
    IOptions<SecurityOptions> securityOptions,
    IAuditWriter audit,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    /// <summary>
    /// A well-formed hash of a value nobody knows, verified against when no user
    /// matches so that a missing account costs the same time as a wrong password.
    /// </summary>
    private const string DummyPasswordHash =
        "AQAAAAIAAYagAAAAEL+Jx5Z1mZ0m2qk9oH7Xh1Q2mB0nQm1s1w8t5nQeF0m8p4kJwP2vQ9lT7r3sH1aZzA==";

    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly SecurityOptions _security = securityOptions.Value;

    /// <inheritdoc />
    public async Task<Result<AuthenticationResult>> SignInAsync(
        PasswordSignInRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] identifierHash = HashIdentifier(request.UserName);

        if (await IsThrottledAsync(identifierHash, request.IpAddress, cancellationToken).ConfigureAwait(false))
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.RateLimited, LoginMethod.Password,
                null, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.TooManyAttempts);
        }

        AppUser? user = await users.FindByNameAsync(request.UserName).ConfigureAwait(false)
                        ?? await users.FindByEmailAsync(request.UserName).ConfigureAwait(false);

        if (user is null)
        {
            // Verify against a dummy hash so a missing account and a wrong
            // password take the same time and produce the same answer.
            _ = users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash, request.Password);

            await RecordFailureAsync(
                identifierHash, LoginFailureReason.InvalidCredentials, LoginMethod.Password,
                null, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        UserId userId = new(user.Id);

        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.LockedOut, LoginMethod.Password,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.LockedOut);
        }

        if (!user.CanAuthenticate)
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.Disabled, LoginMethod.Password,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.AccountDisabled);
        }

        if (!await users.CheckPasswordAsync(user, request.Password).ConfigureAwait(false))
        {
            await users.AccessFailedAsync(user).ConfigureAwait(false);

            await RecordFailureAsync(
                identifierHash, LoginFailureReason.InvalidCredentials, LoginMethod.Password,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        // Accounts that can change other people's authority must have a second
        // factor. Decided by what the account can do, never by a role name.
        if (_security.RequireTwoFactorForAdmins
            && !user.TwoFactorEnabled
            && await HoldsAdministrativeAuthorityAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.TwoFactorRequired, LoginMethod.Password,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.TwoFactorEnrolmentRequired);
        }

        Result twoFactor = await VerifyTwoFactorAsync(user, request.TwoFactorCode).ConfigureAwait(false);

        if (twoFactor.IsFailure)
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.TwoFactorRequired, LoginMethod.Password,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(twoFactor.Error);
        }

        Result<Device?> device = await ResolveDeviceAsync(
            request.DeviceId, userId, identifierHash, LoginMethod.Password,
            request.IpAddress, request.UserAgent, cancellationToken).ConfigureAwait(false);

        if (device.IsFailure)
        {
            return Result<AuthenticationResult>.Failure(device.Error);
        }

        await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        return await CompleteSignInAsync(
            user,
            device.Value,
            identifierHash,
            LoginMethod.Password,
            authenticationMethod: "pwd",
            _jwt.RefreshTokenLifetime,
            offlineOnly: false,
            request.IpAddress,
            request.UserAgent,
            request.AppVersion,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResult>> SignInWithPinAsync(
        PinSignInRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] identifierHash = HashIdentifier(request.EmployeeCode);

        if (await IsPinThrottledAsync(request.DeviceId, cancellationToken).ConfigureAwait(false))
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.RateLimited, LoginMethod.Pin,
                null, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.TooManyAttempts);
        }

        string code = request.EmployeeCode.Trim();

        AppUser? user = await context.Users
            .AsTracking()
            .FirstOrDefaultAsync(u => u.EmployeeCode == code, cancellationToken)
            .ConfigureAwait(false);

        if (user?.PinHash is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash, request.Pin);

            await RecordFailureAsync(
                identifierHash, LoginFailureReason.InvalidCredentials, LoginMethod.Pin,
                user is null ? null : new UserId(user.Id), request.DeviceId,
                request.IpAddress, request.UserAgent, cancellationToken).ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        UserId userId = new(user.Id);

        if (!user.CanAuthenticate)
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.Disabled, LoginMethod.Pin,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.AccountDisabled);
        }

        PasswordVerificationResult verification =
            users.PasswordHasher.VerifyHashedPassword(user, user.PinHash, request.Pin);

        if (verification == PasswordVerificationResult.Failed)
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.InvalidCredentials, LoginMethod.Pin,
                userId, request.DeviceId, request.IpAddress, request.UserAgent, cancellationToken)
                .ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        Result<Device?> device = await ResolveDeviceAsync(
            request.DeviceId, userId, identifierHash, LoginMethod.Pin,
            request.IpAddress, request.UserAgent, cancellationToken).ConfigureAwait(false);

        if (device.IsFailure)
        {
            return Result<AuthenticationResult>.Failure(device.Error);
        }

        return await CompleteSignInAsync(
            user,
            device.Value,
            identifierHash,
            LoginMethod.Pin,
            authenticationMethod: "pin",
            _jwt.PinRefreshTokenLifetime,
            // A PIN is typed on a shared screen in front of customers. The
            // session it buys is narrowed to what a device may do offline.
            offlineOnly: true,
            request.IpAddress,
            request.UserAgent,
            request.AppVersion,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResult>> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] hash = tokens.HashRefreshToken(request.RefreshToken);
        DateTimeOffset now = clock.UtcNow;

        RefreshToken? token = await context.RefreshTokens
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidRefreshToken);
        }

        // A rotated token presented a second time means either it was stolen, or
        // the legitimate successor was. We cannot tell which, so the whole
        // family goes and the user re-authenticates.
        if (token.WasRotated)
        {
            token.FlagReuse(now);
            int burned = await RevokeFamilyAsync(
                token.FamilyId, RefreshTokenRevocationReason.ReuseDetected, now, cancellationToken)
                .ConfigureAwait(false);

            await audit.WriteAsync(
                new AuditEntry(
                    AuditActions.Authentication.RefreshTokenReuseDetected,
                    nameof(RefreshToken),
                    token.Id,
                    Reason: FormattableString.Invariant(
                        $"A rotated refresh token was presented again; {burned} tokens in the family were revoked.")),
                cancellationToken).ConfigureAwait(false);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; family {FamilyId} revoked.",
                token.UserId.Value,
                token.FamilyId);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.RefreshTokenReuse);
        }

        if (!token.IsActiveAt(now))
        {
            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidRefreshToken);
        }

        // A token bound to a device stays bound to it: presenting a terminal's
        // token from somewhere else is not a refresh, it is theft.
        if (token.DeviceId is not null && token.DeviceId != request.DeviceId)
        {
            return Result<AuthenticationResult>.Failure(AuthenticationErrors.InvalidRefreshToken);
        }

        Guid userGuid = token.UserId.Value;

        AppUser? user = await context.Users
            .FirstOrDefaultAsync(u => u.Id == userGuid, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !user.CanAuthenticate)
        {
            await RevokeFamilyAsync(
                token.FamilyId, RefreshTokenRevocationReason.SecurityStampChanged, now, cancellationToken)
                .ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result<AuthenticationResult>.Failure(AuthenticationErrors.AccountDisabled);
        }

        Device? device = null;

        if (token.DeviceId is { } boundDevice)
        {
            device = await context.Devices
                .AsTracking()
                .FirstOrDefaultAsync(d => d.Id == boundDevice, cancellationToken)
                .ConfigureAwait(false);

            if (device is null || !device.IsOperational)
            {
                await RevokeFamilyAsync(
                    token.FamilyId, RefreshTokenRevocationReason.DeviceRevoked, now, cancellationToken)
                    .ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return Result<AuthenticationResult>.Failure(AuthenticationErrors.DeviceNotOperational);
            }
        }

        RefreshTokenMaterial material = tokens.CreateRefreshToken();
        TimeSpan lifetime = token.DeviceId is null ? _jwt.RefreshTokenLifetime : _jwt.PinRefreshTokenLifetime;

        Result<RefreshToken> rotated = token.Rotate(material.Hash, now, lifetime, request.IpAddress);

        if (rotated.IsFailure)
        {
            return Result<AuthenticationResult>.Failure(rotated.Error);
        }

        context.RefreshTokens.Add(rotated.Value);

        device?.RecordContact(now);

        UserAuthorization authorization = await permissions
            .GetAuthorizationAsync(new UserId(user.Id), cancellationToken)
            .ConfigureAwait(false);

        long version = await policyVersion.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        AccessToken access = tokens.IssueAccessToken(
            new UserId(user.Id),
            user.DisplayName,
            user.SecurityStamp ?? string.Empty,
            version,
            user.ApprovalTier,
            token.DeviceId is null ? "pwd" : "pin",
            token.DeviceId,
            authorization.Locations.FirstOrDefault());

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Authentication.TokenRefreshed,
                nameof(AppUser),
                user.Id),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<AuthenticationResult>.Success(Build(
            access, material.Value, rotated.Value.ExpiresAtUtc, user, authorization, version,
            restrictToOfflineCapable: token.DeviceId is not null));
    }

    /// <inheritdoc />
    public async Task<Result> SignOutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        byte[] hash = tokens.HashRefreshToken(refreshToken);
        DateTimeOffset now = clock.UtcNow;

        RefreshToken? token = await context.RefreshTokens
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);

        // Signing out an unknown or already-dead token is not an error: the
        // caller's intent is satisfied either way, and reporting the difference
        // would tell an attacker whether a token they hold is live.
        if (token is null)
        {
            return Result.Success();
        }

        await RevokeFamilyAsync(token.FamilyId, RefreshTokenRevocationReason.SignedOut, now, cancellationToken)
            .ConfigureAwait(false);

        await EndSessionsAsync(token.FamilyId, now, "signed out", cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(AuditActions.Authentication.Logout, nameof(AppUser), token.UserId.Value),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<int>> RevokeAllSessionsAsync(
        UserId userId,
        string reason,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;

        List<RefreshToken> live = await context.RefreshTokens
            .AsTracking()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (RefreshToken token in live)
        {
            token.Revoke(RefreshTokenRevocationReason.AdministrativelyRevoked, now);
        }

        List<DeviceSession> sessions = await context.DeviceSessions
            .AsTracking()
            .Where(s => s.UserId == userId && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (DeviceSession session in sessions)
        {
            session.End(now, reason);
        }

        // The security stamp is the belt to the revocation's braces: it
        // invalidates any access token already in flight at its next validation,
        // without waiting for it to expire.
        Guid id = userId.Value;
        AppUser? user = await context.Users
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (user is not null)
        {
            await users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        }

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Authentication.TokensRevoked,
                nameof(AppUser),
                userId.Value,
                Reason: reason),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await permissions.InvalidateAsync(userId, cancellationToken).ConfigureAwait(false);

        return Result<int>.Success(live.Count);
    }

    private async Task<Result<AuthenticationResult>> CompleteSignInAsync(
        AppUser user,
        Device? device,
        byte[] identifierHash,
        LoginMethod method,
        string authenticationMethod,
        TimeSpan refreshLifetime,
        bool offlineOnly,
        string? ipAddress,
        string? userAgent,
        string? appVersion,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        UserId userId = new(user.Id);

        UserAuthorization authorization = await permissions
            .GetAuthorizationAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        long version = await policyVersion.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        RefreshTokenMaterial material = tokens.CreateRefreshToken();

        RefreshToken refresh = RefreshToken.IssueNewFamily(
            userId, device?.Id, material.Hash, now, refreshLifetime, ipAddress);

        context.RefreshTokens.Add(refresh);

        if (device is not null)
        {
            context.DeviceSessions.Add(DeviceSession.Start(
                device.Id, userId, refresh.FamilyId, now, ipAddress, appVersion));

            device.RecordContact(now, appVersion);
        }

        AccessToken access = tokens.IssueAccessToken(
            userId,
            user.DisplayName,
            user.SecurityStamp ?? string.Empty,
            version,
            user.ApprovalTier,
            authenticationMethod,
            device?.Id,
            device?.LocationId ?? authorization.Locations.FirstOrDefault());

        // The password path gets its user from UserManager, which does not track
        // it in this context; the PIN path does. Attach only when needed, and
        // mark just the one column so this never becomes a blind full update.
        user.LastLoginAtUtc = now;

        if (context.Entry(user).State == EntityState.Detached)
        {
            context.Users.Attach(user);
        }

        context.Entry(user).Property(u => u.LastLoginAtUtc).IsModified = true;

        context.LoginAttempts.Add(LoginAttempt.Success(
            identifierHash, userId, method, now, device?.Id, ipAddress, userAgent));

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Authentication.LoginSucceeded,
                nameof(AppUser),
                user.Id,
                LocationId: device?.LocationId),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<AuthenticationResult>.Success(Build(
            access, material.Value, refresh.ExpiresAtUtc, user, authorization, version, offlineOnly));
    }

    private static AuthenticationResult Build(
        AccessToken access,
        string refreshToken,
        DateTimeOffset refreshExpiry,
        AppUser user,
        UserAuthorization authorization,
        long policyVersion,
        bool restrictToOfflineCapable)
    {
        // A PIN session is handed only the permissions a device may exercise
        // offline. Everything else needs a full sign-in, so a shoulder-surfed
        // PIN cannot approve anything.
        IReadOnlyList<string> granted = restrictToOfflineCapable
            ? [.. authorization.Permissions.Where(p => Permissions.Find(p)?.IsOfflineCapable == true)]
            : [.. authorization.Permissions];

        return new AuthenticationResult(
            access.Token,
            access.ExpiresAtUtc,
            refreshToken,
            refreshExpiry,
            new UserId(user.Id),
            user.DisplayName,
            authorization.Roles,
            granted,
            [.. authorization.Locations],
            authorization.HasAllLocations,
            policyVersion);
    }

    private async Task<Result> VerifyTwoFactorAsync(AppUser user, string? code)
    {
        if (!user.TwoFactorEnabled)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure(AuthenticationErrors.TwoFactorRequired);
        }

        string normalized = code.Replace(" ", string.Empty, StringComparison.Ordinal);

        // Redeeming a recovery code rewrites an existing token row through
        // Identity's store, which only persists under tracking queries. The user
        // is attached first, so a tracking query for the same account resolves to
        // this instance instead of conflicting with it.
        if (context.Entry(user).State == EntityState.Detached)
        {
            context.Attach(user);
        }

        using TrackingScope tracking = TrackingScope.Begin(context);

        bool valid = await users.VerifyTwoFactorTokenAsync(
            user, users.Options.Tokens.AuthenticatorTokenProvider, normalized).ConfigureAwait(false);

        // A lost phone must not lock an owner out for good: each one-time recovery
        // code issued at enrolment works once in place of the authenticator code.
        if (!valid)
        {
            valid = (await users.RedeemTwoFactorRecoveryCodeAsync(user, normalized).ConfigureAwait(false)).Succeeded;
        }

        return valid ? Result.Success() : Result.Failure(AuthenticationErrors.TwoFactorRequired);
    }

    private async Task<bool> HoldsAdministrativeAuthorityAsync(UserId userId, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> held = await permissions
            .GetEffectivePermissionsAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return held.Contains(Permissions.Administration.ManageUsers)
               || held.Contains(Permissions.Administration.ManageRoles);
    }

    private async Task<Result<Device?>> ResolveDeviceAsync(
        DeviceId? deviceId,
        UserId userId,
        byte[] identifierHash,
        LoginMethod method,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (deviceId is not { } id)
        {
            return Result<Device?>.Success(null);
        }

        Device? device = await context.Devices
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (device is null || !device.IsOperational)
        {
            await RecordFailureAsync(
                identifierHash, LoginFailureReason.DeviceNotOperational, method,
                userId, deviceId, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);

            return Result<Device?>.Failure(AuthenticationErrors.DeviceNotOperational);
        }

        // Signing in at a terminal you are not posted to is refused even with
        // correct credentials: it is the difference between a cashier covering
        // their own till and one quietly working another store's. Development
        // relaxes this so one seed account can stand at any enrolled register.
        if (_security.RequireDeviceLocationAssignment)
        {
            UserAuthorization authorization = await permissions
                .GetAuthorizationAsync(userId, cancellationToken)
                .ConfigureAwait(false);

            if (!authorization.HasAllLocations && !authorization.Locations.Contains(device.LocationId))
            {
                await RecordFailureAsync(
                    identifierHash, LoginFailureReason.LocationNotPermitted, method,
                    userId, deviceId, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);

                return Result<Device?>.Failure(AuthenticationErrors.LocationNotPermitted);
            }
        }

        return Result<Device?>.Success(device);
    }

    private async Task<bool> IsThrottledAsync(
        byte[] identifierHash,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset since = clock.UtcNow - _security.LockoutDuration;

        int perAccount = await context.LoginAttempts
            .CountAsync(
                a => a.IdentifierHash == identifierHash && !a.Succeeded && a.AttemptedAtUtc >= since,
                cancellationToken)
            .ConfigureAwait(false);

        if (perAccount >= _security.MaxFailedAccessAttempts)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        // An address gets a wider budget than an account: several staff share a
        // store's connection, so the per-address limit is there to stop a
        // sweep, not to catch one person mistyping.
        int perAddress = await context.LoginAttempts
            .CountAsync(
                a => a.IpAddress == ipAddress && !a.Succeeded && a.AttemptedAtUtc >= since,
                cancellationToken)
            .ConfigureAwait(false);

        return perAddress >= _security.MaxFailedAccessAttempts * 4;
    }

    private async Task<bool> IsPinThrottledAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        DateTimeOffset since = clock.UtcNow - _security.PinAttemptWindow;

        int failures = await context.LoginAttempts
            .CountAsync(
                a => a.DeviceId == deviceId
                     && a.Method == LoginMethod.Pin
                     && !a.Succeeded
                     && a.AttemptedAtUtc >= since,
                cancellationToken)
            .ConfigureAwait(false);

        return failures >= _security.MaxPinAttemptsPerDevice;
    }

    private async Task RecordFailureAsync(
        byte[] identifierHash,
        LoginFailureReason reason,
        LoginMethod method,
        UserId? userId,
        DeviceId? deviceId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        context.LoginAttempts.Add(LoginAttempt.Failure(
            identifierHash, reason, method, clock.UtcNow, userId, deviceId, ipAddress, userAgent));

        await audit.WriteAsync(
            new AuditEntry(
                reason == LoginFailureReason.LockedOut
                    ? AuditActions.Authentication.LockedOut
                    : AuditActions.Authentication.LoginFailed,
                nameof(AppUser),
                userId?.Value,
                Reason: reason.ToString()),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RevokeFamilyAsync(
        Guid familyId,
        RefreshTokenRevocationReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<RefreshToken> family = await context.RefreshTokens
            .AsTracking()
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (RefreshToken token in family)
        {
            token.Revoke(reason, now);
        }

        return family.Count;
    }

    private async Task EndSessionsAsync(
        Guid familyId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        List<DeviceSession> sessions = await context.DeviceSessions
            .AsTracking()
            .Where(s => s.RefreshTokenFamilyId == familyId && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (DeviceSession session in sessions)
        {
            session.End(now, reason);
        }
    }

    private static byte[] HashIdentifier(string identifier)
        => SHA256.HashData(Encoding.UTF8.GetBytes((identifier ?? string.Empty).Trim().ToUpperInvariant()));
}
