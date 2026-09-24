using System.ComponentModel.DataAnnotations;

namespace Pos.Infrastructure.Configuration;

/// <summary>Token issuance and validation settings.</summary>
/// <remarks>
/// Access tokens are deliberately short-lived and carry no permission list: the
/// API resolves permissions per request from a cache keyed by the policy
/// version, so revoking authority takes effect immediately rather than at the
/// next expiry.
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Jwt";

    /// <summary>Gets or sets the token issuer.</summary>
    [Required]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the intended audience.</summary>
    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>Gets or sets the PEM-encoded RSA private key used to sign tokens.</summary>
    [Required]
    public string SigningKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the previous signing key, kept during a rotation so tokens
    /// issued before the change still validate until they expire.
    /// </summary>
    public string? PreviousSigningKeyPem { get; set; }

    /// <summary>Gets or sets the access token lifetime in minutes.</summary>
    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>Gets or sets the refresh token lifetime in days for password sign-in.</summary>
    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the refresh token lifetime in hours for PIN sign-in.
    /// </summary>
    /// <remarks>
    /// Shorter than a password session because a PIN is entered on a shared
    /// terminal in front of customers and is far easier to shoulder-surf.
    /// </remarks>
    [Range(1, 72)]
    public int PinRefreshTokenHours { get; set; } = 12;

    /// <summary>Gets or sets the tolerated clock skew in seconds when validating.</summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Gets the access token lifetime.</summary>
    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    /// <summary>Gets the refresh token lifetime for password sign-in.</summary>
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);

    /// <summary>Gets the refresh token lifetime for PIN sign-in.</summary>
    public TimeSpan PinRefreshTokenLifetime => TimeSpan.FromHours(PinRefreshTokenHours);
}

/// <summary>Authentication hardening settings.</summary>
public sealed class SecurityOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Security";

    /// <summary>
    /// Gets or sets the password hashing iteration count.
    /// </summary>
    /// <remarks>
    /// Configurable so it can be raised as hardware improves. Identity stores a
    /// format marker with each hash, so raising this does not invalidate
    /// existing passwords; they are re-hashed at the next successful sign-in.
    /// </remarks>
    [Range(100_000, 5_000_000)]
    public int PasswordHashIterations { get; set; } = 600_000;

    /// <summary>Gets or sets the minimum password length.</summary>
    [Range(8, 128)]
    public int MinimumPasswordLength { get; set; } = 12;

    /// <summary>Gets or sets the minimum cashier PIN length.</summary>
    [Range(4, 12)]
    public int MinimumPinLength { get; set; } = 6;

    /// <summary>
    /// Gets or sets whether a device sign-in requires the user to be assigned
    /// to the location the device belongs to.
    /// </summary>
    /// <remarks>
    /// Enforced in production. Development turns this off so a single account
    /// can test a register enrolled at any store without editing assignments.
    /// </remarks>
    public bool RequireDeviceLocationAssignment { get; set; } = true;

    /// <summary>Gets or sets how many failed attempts trigger a lockout.</summary>
    [Range(3, 20)]
    public int MaxFailedAccessAttempts { get; set; } = 5;

    /// <summary>Gets or sets the lockout duration in minutes.</summary>
    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Gets or sets how many PIN attempts a device may make per window.</summary>
    [Range(3, 20)]
    public int MaxPinAttemptsPerDevice { get; set; } = 5;

    /// <summary>Gets or sets the PIN attempt window in minutes.</summary>
    [Range(1, 60)]
    public int PinAttemptWindowMinutes { get; set; } = 5;

    /// <summary>Gets or sets whether administrators and owners must use a second factor.</summary>
    public bool RequireTwoFactorForAdmins { get; set; } = true;

    /// <summary>Gets or sets the enrolment code lifetime in minutes.</summary>
    [Range(1, 120)]
    public int DeviceEnrolmentCodeMinutes { get; set; } = 15;

    /// <summary>
    /// Gets or sets how long a device may use a cached permission snapshot
    /// before it must reconnect.
    /// </summary>
    [Range(1, 168)]
    public int PermissionSnapshotHours { get; set; } = 72;

    /// <summary>Gets the lockout duration.</summary>
    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(LockoutMinutes);

    /// <summary>Gets the PIN attempt window.</summary>
    public TimeSpan PinAttemptWindow => TimeSpan.FromMinutes(PinAttemptWindowMinutes);

    /// <summary>Gets the enrolment code lifetime.</summary>
    public TimeSpan DeviceEnrolmentCodeLifetime => TimeSpan.FromMinutes(DeviceEnrolmentCodeMinutes);
}

/// <summary>Business-wide settings.</summary>
public sealed class OrganizationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Organization";

    /// <summary>Gets or sets the organization's display name.</summary>
    [Required]
    public string Name { get; set; } = "VaultFlow";

    /// <summary>Gets or sets the default IANA timezone for business dates.</summary>
    [Required]
    public string TimeZoneId { get; set; } = "Asia/Manila";

    /// <summary>Gets or sets the ISO-4217 currency code.</summary>
    [Required]
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Currency must be a three-letter ISO-4217 code.")]
    public string CurrencyCode { get; set; } = "PHP";

    /// <summary>Gets or sets the smallest circulating cash denomination.</summary>
    [Range(typeof(decimal), "0.01", "10.00")]
    public decimal CashRoundingIncrement { get; set; } = 0.01m;

    /// <summary>Gets or sets the approval ceiling for each tier, in the organization currency.</summary>
    public ApprovalTierLimits ApprovalLimits { get; set; } = new();
}

/// <summary>The monetary ceiling attached to each approval tier.</summary>
public sealed class ApprovalTierLimits
{
    /// <summary>Gets or sets the store-manager ceiling.</summary>
    [Range(typeof(decimal), "0", "100000000")]
    public decimal Tier1 { get; set; } = 5_000m;

    /// <summary>Gets or sets the main-inventory-manager ceiling.</summary>
    [Range(typeof(decimal), "0", "100000000")]
    public decimal Tier2 { get; set; } = 50_000m;

    /// <summary>Gets or sets the administrator ceiling.</summary>
    [Range(typeof(decimal), "0", "100000000")]
    public decimal Tier3 { get; set; } = 250_000m;

    /// <summary>
    /// Gets or sets the value above which a document's creator may not approve
    /// their own work. Zero means never.
    /// </summary>
    [Range(typeof(decimal), "0", "100000000")]
    public decimal SelfApprovalLimit { get; set; }
}

/// <summary>Development seeding behaviour.</summary>
/// <remarks>
/// Splitting the switch from <c>Database:SeedDevelopmentData</c> lets a developer
/// exercise the master data and the staff accounts independently, and lets a
/// shared dev database keep its reference data while accounts stay off.
/// </remarks>
public sealed class SeedingOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Seeding";

    /// <summary>
    /// Gets or sets whether development staff accounts are created with a known
    /// password. Never enabled outside development: these accounts carry
    /// standard-role authority and a published credential.
    /// </summary>
    public bool EnableDevelopmentAccounts { get; set; }
}

/// <summary>
/// Request rate limits.
/// </summary>
/// <remarks>
/// Configuration rather than constants, because the right numbers depend on the
/// deployment: several tills behind one shop router share an address, and an
/// integration suite hammers sign-in from a single loopback address. Hard-coding
/// them would mean either a limit too loose to help or one that fails honest
/// traffic.
/// </remarks>
public sealed class RateLimitOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "RateLimits";

    /// <summary>Gets or sets sign-in attempts allowed per address per window.</summary>
    [Range(1, 10_000)]
    public int LoginPermitLimit { get; set; } = 10;

    /// <summary>Gets or sets the sign-in window, in minutes.</summary>
    [Range(1, 1440)]
    public int LoginWindowMinutes { get; set; } = 5;

    /// <summary>Gets or sets token exchanges allowed per device per window.</summary>
    [Range(1, 10_000)]
    public int RefreshPermitLimit { get; set; } = 30;

    /// <summary>Gets or sets the token-exchange window, in minutes.</summary>
    [Range(1, 1440)]
    public int RefreshWindowMinutes { get; set; } = 60;

    /// <summary>Gets or sets synchronization batches allowed per device per minute.</summary>
    [Range(1, 10_000)]
    public int SyncPushPermitLimit { get; set; } = 60;

    /// <summary>Gets or sets requests allowed per caller per minute overall.</summary>
    [Range(1, 100_000)]
    public int GlobalPermitLimit { get; set; } = 300;
}

/// <summary>Database behaviour settings.</summary>
public sealed class DatabaseOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Gets or sets whether the application applies migrations at start-up.
    /// </summary>
    /// <remarks>
    /// False everywhere but development. Replicas racing to migrate on boot is a
    /// well-known way to take a system down, and leaving it off is what lets the
    /// running application's database role hold no schema rights.
    /// </remarks>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Gets or sets whether development seed data is loaded at start-up.</summary>
    public bool SeedDevelopmentData { get; set; }

    /// <summary>
    /// Gets or sets which database provider the host talks to.
    /// </summary>
    /// <remarks>
    /// PostgreSQL for the server, SQLite for a device and for integration tests.
    /// Making this configuration rather than a compile-time choice is what lets a
    /// test host the real application unchanged, instead of unpicking its
    /// registrations afterwards and hoping nothing was missed.
    /// </remarks>
    public string Provider { get; set; } = "Postgres";
}

/// <summary>Inventory balance reconciliation settings.</summary>
public sealed class ReconciliationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Reconciliation";

    /// <summary>Gets or sets whether the background reconciliation worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many hours between reconciliation passes.</summary>
    [Range(1, 168)]
    public int IntervalHours { get; set; } = 24;

    /// <summary>Gets or sets whether a reconciliation pass runs on application start.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Gets the interval as a time span.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours);
}

/// <summary>Expiry quarantine worker settings.</summary>
public sealed class ExpiryOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Expiry";

    /// <summary>Gets or sets whether the background expiry worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many hours between expiry sweeps.</summary>
    [Range(1, 24)]
    public int IntervalHours { get; set; } = 6;

    /// <summary>Gets or sets whether an expiry sweep runs on application start.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Gets the interval as a time span.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours);
}

/// <summary>Low-stock alert worker settings.</summary>
public sealed class LowStockOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "LowStock";

    /// <summary>Gets or sets whether the background low-stock worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many hours between low-stock sweeps.</summary>
    [Range(1, 24)]
    public int IntervalHours { get; set; } = 1;

    /// <summary>Gets or sets whether a low-stock sweep runs on application start.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Gets the interval as a time span.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours);
}

/// <summary>Receiving and transfer discrepancy alert worker settings.</summary>
public sealed class DiscrepancyAlertOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "DiscrepancyAlerts";

    /// <summary>Gets or sets whether the background discrepancy alert worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many minutes between discrepancy sweeps.</summary>
    [Range(5, 1440)]
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Gets or sets how many days back a sweep looks for unresolved discrepancies.</summary>
    [Range(1, 90)]
    public int LookbackDays { get; set; } = 7;

    /// <summary>Gets or sets whether a discrepancy sweep runs on application start.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Gets the interval as a time span.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    /// <summary>Gets the lookback as a time span.</summary>
    public TimeSpan Lookback => TimeSpan.FromDays(LookbackDays);
}

/// <summary>Emergency-transfer alert worker settings.</summary>
public sealed class EmergencyTransferAlertOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "EmergencyTransferAlerts";

    /// <summary>Gets or sets whether the background emergency-transfer alert worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many minutes between emergency-transfer sweeps.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Gets or sets how many days back a sweep looks for committed emergency transfers.</summary>
    [Range(1, 90)]
    public int LookbackDays { get; set; } = 30;

    /// <summary>Gets or sets whether an emergency-transfer sweep runs on application start.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Gets the interval as a time span.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    /// <summary>Gets the lookback as a time span.</summary>
    public TimeSpan Lookback => TimeSpan.FromDays(LookbackDays);
}

/// <summary>Cashier shift force-close worker settings.</summary>
public sealed class ShiftForceCloseOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "ShiftForceClose";

    /// <summary>Gets or sets whether the background shift force-close worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how many hours between force-close passes. An hourly sweep
    /// bounds how long an abandoned shift may sit open past its location's
    /// <c>MaxShiftHours</c> (POS.md §1).
    /// </summary>
    [Range(1, 24)]
    public int IntervalHours { get; set; } = 1;

    /// <summary>Gets or sets whether a force-close pass runs on application start.</summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Gets the interval as a time span.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours);
}

/// <summary>Maintenance-gated operations such as balance rebuilding.</summary>
public sealed class MaintenanceOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Maintenance";

    /// <summary>
    /// Gets or sets whether the <c>POST /api/v1/inventory/rebuild-balances</c>
    /// endpoint may execute. Must be true in production; keeping it off in
    /// development prevents accidental drops.
    /// </summary>
    public bool AllowBalanceRebuild { get; set; }
}
