using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// Lets an account enrol an authenticator app before it can sign in.
/// </summary>
/// <remarks>
/// <para>
/// When two-factor is required for administrative accounts, such an account
/// cannot obtain a token until it has enrolled, so enrolment authenticates with
/// the password alone. That is no weaker than sign-in was before enforcement,
/// and it only ever works for an account that has not enrolled yet: once
/// two-factor is on, resetting it takes another administrator.
/// </para>
/// <para>
/// A wrong password counts towards the account lockout exactly as a failed
/// sign-in does, and every refusal is the same answer whether the account
/// exists or not.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="users">Identity's user manager.</param>
/// <param name="audit">The audit writer.</param>
public sealed class TwoFactorEnrolmentService(
    PosDbContext context,
    UserManager<AppUser> users,
    IAuditWriter audit) : ITwoFactorEnrolment
{
    private const string Issuer = "VaultFlow";

    /// <inheritdoc />
    public async Task<Result<TwoFactorSetup>> BeginAsync(
        string userName, string password, CancellationToken cancellationToken)
    {
        using TrackingScope tracking = TrackingScope.Begin(context);

        Result<AppUser> user = await AuthenticateAsync(userName, password).ConfigureAwait(false);

        if (user.IsFailure)
        {
            return Result<TwoFactorSetup>.Failure(user.Errors);
        }

        string? key = await users.GetAuthenticatorKeyAsync(user.Value).ConfigureAwait(false);

        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user.Value).ConfigureAwait(false);
            key = await users.GetAuthenticatorKeyAsync(user.Value).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        string account = Uri.EscapeDataString(user.Value.UserName ?? userName);
        string uri = string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{account}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6");

        return Result<TwoFactorSetup>.Success(new TwoFactorSetup(GroupKey(key!), uri));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> CompleteAsync(
        string userName, string password, string code, CancellationToken cancellationToken)
    {
        using TrackingScope tracking = TrackingScope.Begin(context);

        Result<AppUser> user = await AuthenticateAsync(userName, password).ConfigureAwait(false);

        if (user.IsFailure)
        {
            return Result<IReadOnlyList<string>>.Failure(user.Errors);
        }

        string normalized = (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        bool valid = normalized.Length > 0 && await users.VerifyTwoFactorTokenAsync(
            user.Value, users.Options.Tokens.AuthenticatorTokenProvider, normalized).ConfigureAwait(false);

        if (!valid)
        {
            return Result<IReadOnlyList<string>>.Failure(AdministrationErrors.TwoFactorCodeInvalid);
        }

        await users.SetTwoFactorEnabledAsync(user.Value, true).ConfigureAwait(false);
        IEnumerable<string>? recoveryCodes = await users
            .GenerateNewTwoFactorRecoveryCodesAsync(user.Value, 8).ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(AuditActions.Authentication.TwoFactorEnabled, nameof(AppUser), user.Value.Id),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<string>>.Success([.. recoveryCodes ?? []]);
    }

    private async Task<Result<AppUser>> AuthenticateAsync(string userName, string password)
    {
        AppUser? user = string.IsNullOrWhiteSpace(userName)
            ? null
            : await users.FindByNameAsync(userName.Trim()).ConfigureAwait(false);

        if (user is null || !user.CanAuthenticate || await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return Result<AppUser>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        if (!await users.CheckPasswordAsync(user, password ?? string.Empty).ConfigureAwait(false))
        {
            await users.AccessFailedAsync(user).ConfigureAwait(false);
            return Result<AppUser>.Failure(AuthenticationErrors.InvalidCredentials);
        }

        if (user.TwoFactorEnabled)
        {
            return Result<AppUser>.Failure(AdministrationErrors.TwoFactorAlreadyEnabled);
        }

        return Result<AppUser>.Success(user);
    }

    private static string GroupKey(string key)
    {
        StringBuilder grouped = new();

        for (int i = 0; i < key.Length; i += 4)
        {
            if (grouped.Length > 0)
            {
                grouped.Append(' ');
            }

            grouped.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }

        return grouped.ToString().ToLowerInvariant();
    }
}
