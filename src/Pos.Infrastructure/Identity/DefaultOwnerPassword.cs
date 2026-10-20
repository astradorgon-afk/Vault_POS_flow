using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>Settings for a well-known starting password that must be replaced on first sign-in.</summary>
/// <remarks>
/// Meant for the public demo, where the owner account starts on a published
/// password. Off unless explicitly enabled.
/// </remarks>
public sealed class DefaultOwnerPasswordOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "DefaultOwnerPassword";

    /// <summary>Gets or sets whether the account is put on the default password.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the account's username.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the default password.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>The user claims that track a default password.</summary>
public static class DefaultPasswordClaims
{
    /// <summary>The account must choose a new password before it can sign in.</summary>
    public const string ChangeRequired = "vaultflow:password_change_required";

    /// <summary>The account has replaced its default, so start-up must never put it back.</summary>
    public const string DefaultRetired = "vaultflow:default_password_retired";
}

/// <summary>Puts the configured account on its default password until someone replaces it.</summary>
/// <remarks>
/// <para>
/// Runs at every start-up but acts at most once per account: the first time it
/// finds the account it sets the default and flags it, and once the password has
/// been replaced the account carries <see cref="DefaultPasswordClaims.DefaultRetired"/>
/// and is never touched again.
/// </para>
/// <para>
/// The default is written straight to the hash, skipping the length policy. It
/// only has to work until the first sign-in, and that sign-in is refused until
/// the account picks a password that does meet the policy.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="users">Identity's user manager.</param>
/// <param name="authentication">Used to end sessions opened under the old password.</param>
/// <param name="options">Default password settings.</param>
/// <param name="logger">Logger.</param>
public sealed class DefaultOwnerPasswordSeeder(
    PosDbContext context,
    UserManager<AppUser> users,
    IAuthenticationService authentication,
    IOptions<DefaultOwnerPasswordOptions> options,
    ILogger<DefaultOwnerPasswordSeeder> logger)
{
    private readonly DefaultOwnerPasswordOptions _options = options.Value;

    /// <summary>Applies the default password if the account has never replaced it.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the account is in the right state.</returns>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrEmpty(_options.Password))
        {
            logger.LogWarning("DefaultOwnerPassword is enabled but no username or password was supplied.");
            return;
        }

        using TrackingScope tracking = TrackingScope.Begin(context);

        AppUser? user = await users.FindByNameAsync(_options.UserName.Trim()).ConfigureAwait(false);

        if (user is null)
        {
            logger.LogWarning("DefaultOwnerPassword names {UserName}, which does not exist.", _options.UserName);
            return;
        }

        IList<Claim> claims = await users.GetClaimsAsync(user).ConfigureAwait(false);

        if (claims.Any(c => c.Type == DefaultPasswordClaims.DefaultRetired))
        {
            return;
        }

        if (!await users.CheckPasswordAsync(user, _options.Password).ConfigureAwait(false))
        {
            user.PasswordHash = users.PasswordHasher.HashPassword(user, _options.Password);
            await users.UpdateSecurityStampAsync(user).ConfigureAwait(false);
            await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            await authentication.RevokeAllSessionsAsync(
                new UserId(user.Id), "default password applied", cancellationToken).ConfigureAwait(false);
        }

        if (!claims.Any(c => c.Type == DefaultPasswordClaims.ChangeRequired))
        {
            await users.AddClaimAsync(user, new Claim(DefaultPasswordClaims.ChangeRequired, "true")).ConfigureAwait(false);
        }

        logger.LogWarning(
            "{UserName} is on the default password and must replace it at the next sign-in.", user.UserName);
    }
}

/// <summary>Replaces a default password for an account that cannot sign in until it does.</summary>
public interface IRequiredPasswordChange
{
    /// <summary>Checks the current password, sets the new one, and lifts the requirement.</summary>
    Task<Result> ChangeAsync(
        string userName, string currentPassword, string newPassword, CancellationToken cancellationToken);
}

/// <summary>
/// Lets an account flagged with <see cref="DefaultPasswordClaims.ChangeRequired"/>
/// choose its own password.
/// </summary>
/// <remarks>
/// Such an account cannot obtain a token, so this authenticates with the current
/// password alone, the same way authenticator enrolment does. A wrong password
/// counts towards the lockout exactly as a failed sign-in does, and it only ever
/// works for an account that still has the change pending.
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="users">Identity's user manager.</param>
/// <param name="audit">The audit writer.</param>
public sealed class RequiredPasswordChangeService(
    PosDbContext context,
    UserManager<AppUser> users,
    IAuditWriter audit) : IRequiredPasswordChange
{
    /// <inheritdoc />
    public async Task<Result> ChangeAsync(
        string userName, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        using TrackingScope tracking = TrackingScope.Begin(context);

        string name = userName?.Trim() ?? string.Empty;
        AppUser? user = name.Length == 0
            ? null
            : await users.FindByNameAsync(name).ConfigureAwait(false)
              ?? await users.FindByEmailAsync(name).ConfigureAwait(false);

        if (user is null || !user.CanAuthenticate || await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return Result.Failure(AuthenticationErrors.InvalidCredentials);
        }

        if (!await users.CheckPasswordAsync(user, currentPassword ?? string.Empty).ConfigureAwait(false))
        {
            await users.AccessFailedAsync(user).ConfigureAwait(false);
            return Result.Failure(AuthenticationErrors.InvalidCredentials);
        }

        IList<Claim> claims = await users.GetClaimsAsync(user).ConfigureAwait(false);
        Claim? required = claims.FirstOrDefault(c => c.Type == DefaultPasswordClaims.ChangeRequired);

        if (required is null)
        {
            return Result.Failure(AuthenticationErrors.PasswordChangeNotRequired);
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return Result.Failure(AdministrationErrors.PasswordRejected(
                ["The new password must be different from the default password."]));
        }

        IdentityResult changed = await users
            .ChangePasswordAsync(user, currentPassword!, newPassword ?? string.Empty)
            .ConfigureAwait(false);

        if (!changed.Succeeded)
        {
            return Result.Failure(AdministrationErrors.PasswordRejected(changed.Errors.Select(e => e.Description)));
        }

        await users.RemoveClaimAsync(user, required).ConfigureAwait(false);
        await users.AddClaimAsync(user, new Claim(DefaultPasswordClaims.DefaultRetired, "true")).ConfigureAwait(false);
        await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.Authentication.PasswordChanged,
                nameof(AppUser),
                user.Id,
                Reason: "default password replaced at first sign-in",
                ActorUserId: new UserId(user.Id)),
            cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
