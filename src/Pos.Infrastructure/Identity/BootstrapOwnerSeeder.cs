using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>Settings for the first administrative account.</summary>
/// <remarks>
/// Supplied through the environment or user secrets and never committed. The
/// section is optional: an installation that does not set it simply has no
/// bootstrap account, which is the right outcome for a system that already has
/// users.
/// </remarks>
public sealed class BootstrapOwnerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "BootstrapOwner";

    /// <summary>Gets or sets whether to create the account when the system has no users.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the username to create.</summary>
    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the account's e-mail address.</summary>
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    [MaxLength(128)]
    public string DisplayName { get; set; } = "Owner";

    /// <summary>Gets or sets the initial password.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Creates the first Owner account, once, on an empty system.
/// </summary>
/// <remarks>
/// <para>
/// A system with no users cannot be administered: there is nobody to sign in and
/// create the first account. This closes that gap without the usual cure, which
/// is a well-known default password that survives into production.
/// </para>
/// <para>
/// Three properties make it safe. It runs only when explicitly enabled. It
/// refuses outright if any user already exists, so it can never reset or
/// resurrect an account on a live system. And the password comes from
/// configuration — the environment or user secrets — so there is no default to
/// forget to change.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="users">Identity's user manager.</param>
/// <param name="options">Bootstrap settings.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="logger">Logger.</param>
public sealed class BootstrapOwnerSeeder(
    PosDbContext context,
    UserManager<AppUser> users,
    IOptions<BootstrapOwnerOptions> options,
    ISystemClock clock,
    ILogger<BootstrapOwnerSeeder> logger)
{
    private readonly BootstrapOwnerOptions _options = options.Value;

    /// <summary>Creates the bootstrap account if the conditions allow it.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created user's identifier, or null when nothing was created.</returns>
    public async Task<Guid?> SeedAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrWhiteSpace(_options.Password))
        {
            logger.LogWarning(
                "BootstrapOwner is enabled but no username or password was supplied; no account was created.");
            return null;
        }

        // The decisive guard: if anyone at all exists, this is not a fresh
        // installation and the bootstrap path must stay shut.
        if (await context.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "BootstrapOwner is enabled but the system already has users; no account was created.");
            return null;
        }

        DateTimeOffset now = clock.UtcNow;

        AppUser owner = new()
        {
            Id = Guid.CreateVersion7(),
            UserName = _options.UserName.Trim(),
            Email = string.IsNullOrWhiteSpace(_options.Email) ? null : _options.Email.Trim(),
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(_options.DisplayName) ? "Owner" : _options.DisplayName.Trim(),
            ApprovalTier = ApprovalTier.Unlimited,
            IsActive = true,
            CreatedAtUtc = now,
        };

        IdentityResult created = await users.CreateAsync(owner, _options.Password).ConfigureAwait(false);

        if (!created.Succeeded)
        {
            // Most often the supplied password fails the length policy. Report
            // it rather than quietly leaving the system unusable.
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Could not create the bootstrap owner: {string.Join("; ", created.Errors.Select(e => e.Description))}"));
        }

        IdentityResult assigned = await users.AddToRoleAsync(owner, Roles.Owner).ConfigureAwait(false);

        if (!assigned.Succeeded)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Could not assign the Owner role: {string.Join("; ", assigned.Errors.Select(e => e.Description))}"));
        }

        logger.LogWarning(
            "Created the bootstrap Owner account {UserName}. Sign in, change the password, and disable BootstrapOwner.",
            owner.UserName);

        return owner.Id;
    }
}
