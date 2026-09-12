using System.Text.RegularExpressions;
using Pos.Domain.Common;

namespace Pos.Domain.Devices;

/// <summary>The platform a device runs on.</summary>
public enum DevicePlatform
{
    /// <summary>Unknown or not yet reported.</summary>
    Unknown = 0,

    /// <summary>Windows POS lane.</summary>
    Windows = 1,

    /// <summary>Android POS or stock terminal.</summary>
    Android = 2,
}

/// <summary>The lifecycle state of a registered device.</summary>
public enum DeviceStatus
{
    /// <summary>An enrolment code has been issued but not yet redeemed.</summary>
    PendingEnrolment = 0,

    /// <summary>Enrolled and permitted to authenticate and synchronize.</summary>
    Active = 1,

    /// <summary>Temporarily blocked. Reversible.</summary>
    Suspended = 2,

    /// <summary>Permanently blocked. A returning device must re-enrol as a new one.</summary>
    Revoked = 3,
}

/// <summary>
/// A registered POS or stock terminal.
/// </summary>
/// <remarks>
/// <para>
/// Devices are first-class principals. An access token is bound to one, sync is
/// refused for any status other than <see cref="DeviceStatus.Active"/>, and the
/// short code is embedded in the document numbers the device issues offline so
/// they cannot collide with another device's.
/// </para>
/// <para>
/// Revocation is deliberately permanent. A device that may have been tampered
/// with does not come back with the same identity; its historical documents keep
/// pointing at the old record, which is what makes the trail readable after the
/// fact.
/// </para>
/// </remarks>
public sealed partial class Device : AggregateRoot<DeviceId>
{
    private Device(
        DeviceId id,
        string shortCode,
        string name,
        LocationId locationId,
        DevicePlatform platform,
        DateTimeOffset registeredAtUtc,
        UserId registeredByUserId)
    {
        Id = id;
        ShortCode = shortCode;
        Name = name;
        LocationId = locationId;
        Platform = platform;
        RegisteredAtUtc = registeredAtUtc;
        RegisteredByUserId = registeredByUserId;
        Status = DeviceStatus.PendingEnrolment;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Device()
    {
        ShortCode = string.Empty;
        Name = string.Empty;
    }

    /// <summary>
    /// Gets the short code embedded in offline document numbers, for example
    /// <c>D03</c> in <c>SAL-2026-D03-000812</c>. Immutable once issued.
    /// </summary>
    public string ShortCode { get; private init; }

    /// <summary>Gets the human-readable device name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets the location this device belongs to.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets the platform the device runs on.</summary>
    public DevicePlatform Platform { get; private set; }

    /// <summary>Gets the application version last reported.</summary>
    public string? AppVersion { get; private set; }

    /// <summary>Gets the operating system version last reported.</summary>
    public string? OsVersion { get; private set; }

    /// <summary>Gets the current lifecycle state.</summary>
    public DeviceStatus Status { get; private set; }

    /// <summary>Gets when the device record was created.</summary>
    public DateTimeOffset RegisteredAtUtc { get; private init; }

    /// <summary>Gets who created the record.</summary>
    public UserId RegisteredByUserId { get; private init; }

    /// <summary>Gets when enrolment completed.</summary>
    public DateTimeOffset? EnrolledAtUtc { get; private set; }

    /// <summary>Gets when the device last contacted the server.</summary>
    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    /// <summary>Gets when the device last synchronized successfully.</summary>
    public DateTimeOffset? LastSyncAtUtc { get; private set; }

    /// <summary>Gets the last observed difference between the device clock and the server's.</summary>
    public decimal? ClockSkewSeconds { get; private set; }

    /// <summary>Gets the thumbprint of the key the device generated at enrolment.</summary>
    public string? PublicKeyThumbprint { get; private set; }

    /// <summary>Gets the reason the device was suspended or revoked.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Gets when the status last changed.</summary>
    public DateTimeOffset? StatusChangedAtUtc { get; private set; }

    /// <summary>Gets who last changed the status.</summary>
    public UserId? StatusChangedByUserId { get; private set; }

    /// <summary>Gets a value indicating whether the device may authenticate and synchronize.</summary>
    public bool IsOperational => Status == DeviceStatus.Active;

    /// <summary>Registers a device, ready to be enrolled.</summary>
    /// <param name="shortCode">The document-number short code. Two to six alphanumerics.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="locationId">The location the device belongs to.</param>
    /// <param name="platform">The platform.</param>
    /// <param name="registeredAtUtc">Server time.</param>
    /// <param name="registeredByUserId">Who registered it.</param>
    /// <returns>The device, or a validation failure.</returns>
    public static Result<Device> Register(
        string shortCode,
        string name,
        LocationId locationId,
        DevicePlatform platform,
        DateTimeOffset registeredAtUtc,
        UserId registeredByUserId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<Device>.Failure(Error.Validation(
                "device.name_required", "A device name is required."));
        }

        string code = (shortCode ?? string.Empty).Trim().ToUpperInvariant();

        if (!ShortCodePattern().IsMatch(code))
        {
            return Result<Device>.Failure(Error.Validation(
                "device.short_code_invalid",
                "A device short code must be two to six upper-case letters or digits."));
        }

        return Result<Device>.Success(new Device(
            DeviceId.New(), code, name.Trim(), locationId, platform, registeredAtUtc, registeredByUserId));
    }

    /// <summary>Completes enrolment once a valid code has been redeemed.</summary>
    /// <param name="publicKeyThumbprint">Thumbprint of the key the device generated.</param>
    /// <param name="appVersion">Reported application version.</param>
    /// <param name="osVersion">Reported operating system version.</param>
    /// <param name="atUtc">Server time.</param>
    /// <returns>Success, or the reason enrolment was refused.</returns>
    public Result CompleteEnrolment(
        string publicKeyThumbprint,
        string? appVersion,
        string? osVersion,
        DateTimeOffset atUtc)
    {
        if (Status != DeviceStatus.PendingEnrolment)
        {
            return Result.Failure(Error.Conflict(
                "device.already_enrolled",
                "This device has already been enrolled."));
        }

        if (string.IsNullOrWhiteSpace(publicKeyThumbprint))
        {
            return Result.Failure(Error.Validation(
                "device.key_required", "A device must present a key at enrolment."));
        }

        PublicKeyThumbprint = publicKeyThumbprint.Trim();
        AppVersion = appVersion;
        OsVersion = osVersion;
        Status = DeviceStatus.Active;
        EnrolledAtUtc = atUtc;
        StatusChangedAtUtc = atUtc;

        return Result.Success();
    }

    /// <summary>Records a successful contact from the device.</summary>
    /// <param name="atUtc">Server time.</param>
    /// <param name="appVersion">Reported application version.</param>
    /// <param name="clockSkewSeconds">Observed clock difference.</param>
    public void RecordContact(DateTimeOffset atUtc, string? appVersion = null, decimal? clockSkewSeconds = null)
    {
        LastSeenAtUtc = atUtc;

        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            AppVersion = appVersion;
        }

        if (clockSkewSeconds is not null)
        {
            ClockSkewSeconds = clockSkewSeconds;
        }
    }

    /// <summary>Records a successful synchronization.</summary>
    /// <param name="atUtc">Server time.</param>
    public void RecordSync(DateTimeOffset atUtc)
    {
        LastSyncAtUtc = atUtc;
        LastSeenAtUtc = atUtc;
    }

    /// <summary>Moves the device to a new location.</summary>
    /// <param name="locationId">The new location.</param>
    /// <param name="atUtc">Server time.</param>
    /// <param name="byUserId">Who moved it.</param>
    public void Reassign(LocationId locationId, DateTimeOffset atUtc, UserId byUserId)
    {
        LocationId = locationId;
        StatusChangedAtUtc = atUtc;
        StatusChangedByUserId = byUserId;
    }

    /// <summary>Renames the device.</summary>
    /// <param name="name">The new name.</param>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>Temporarily blocks the device.</summary>
    /// <param name="reason">Why. Required.</param>
    /// <param name="atUtc">Server time.</param>
    /// <param name="byUserId">Who suspended it.</param>
    /// <returns>Success, or the reason the change was refused.</returns>
    public Result Suspend(string reason, DateTimeOffset atUtc, UserId byUserId)
    {
        if (Status == DeviceStatus.Revoked)
        {
            return Result.Failure(Error.Conflict(
                "device.revoked", "A revoked device cannot be suspended; revocation is final."));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(Error.Validation(
                "device.reason_required", "Suspending a device requires a reason."));
        }

        Status = DeviceStatus.Suspended;
        StatusReason = reason.Trim();
        StatusChangedAtUtc = atUtc;
        StatusChangedByUserId = byUserId;

        return Result.Success();
    }

    /// <summary>Lifts a suspension.</summary>
    /// <param name="atUtc">Server time.</param>
    /// <param name="byUserId">Who reactivated it.</param>
    /// <returns>Success, or the reason the change was refused.</returns>
    public Result Reactivate(DateTimeOffset atUtc, UserId byUserId)
    {
        if (Status != DeviceStatus.Suspended)
        {
            return Result.Failure(Error.Conflict(
                "device.not_suspended", "Only a suspended device can be reactivated."));
        }

        Status = DeviceStatus.Active;
        StatusReason = null;
        StatusChangedAtUtc = atUtc;
        StatusChangedByUserId = byUserId;

        return Result.Success();
    }

    /// <summary>Permanently blocks the device.</summary>
    /// <param name="reason">Why. Required.</param>
    /// <param name="atUtc">Server time.</param>
    /// <param name="byUserId">Who revoked it.</param>
    /// <returns>Success, or the reason the change was refused.</returns>
    /// <remarks>
    /// The device's queued but unsent sales are not affected: the client keeps
    /// its outbox and is told to wipe only cached master data, so revoking a
    /// stolen terminal never destroys money that was actually taken.
    /// </remarks>
    public Result Revoke(string reason, DateTimeOffset atUtc, UserId byUserId)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(Error.Validation(
                "device.reason_required", "Revoking a device requires a reason."));
        }

        Status = DeviceStatus.Revoked;
        StatusReason = reason.Trim();
        StatusChangedAtUtc = atUtc;
        StatusChangedByUserId = byUserId;

        return Result.Success();
    }

    [GeneratedRegex(@"^[A-Z0-9]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortCodePattern();
}
