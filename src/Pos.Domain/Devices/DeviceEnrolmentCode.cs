using Pos.Domain.Common;

namespace Pos.Domain.Devices;

/// <summary>
/// A one-time code that lets a specific device complete enrolment.
/// </summary>
/// <remarks>
/// <para>
/// Enrolment is the moment an untrusted machine becomes a principal, so the code
/// is short-lived, single-use, bound to one pre-registered device, and stored
/// only as a hash. An attacker who reads the database cannot enrol a terminal
/// with what they find there.
/// </para>
/// <para>
/// Redemption failures are counted: a code that is guessed at repeatedly is
/// burned rather than left available for the rest of its window.
/// </para>
/// </remarks>
public sealed class DeviceEnrolmentCode : Entity<Guid>
{
    /// <summary>How many wrong attempts a code tolerates before it is burned.</summary>
    public const int MaxRedemptionAttempts = 5;

    private DeviceEnrolmentCode(
        Guid id,
        DeviceId deviceId,
        byte[] codeHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        UserId issuedByUserId)
    {
        Id = id;
        DeviceId = deviceId;
        CodeHash = codeHash;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        IssuedByUserId = issuedByUserId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private DeviceEnrolmentCode()
    {
        CodeHash = [];
    }

    /// <summary>Gets the device this code enrols.</summary>
    public DeviceId DeviceId { get; private init; }

    /// <summary>Gets the SHA-256 hash of the code. The code itself is never stored.</summary>
    public byte[] CodeHash { get; private init; }

    /// <summary>Gets when the code was issued.</summary>
    public DateTimeOffset IssuedAtUtc { get; private init; }

    /// <summary>Gets when the code stops working.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private init; }

    /// <summary>Gets who issued the code.</summary>
    public UserId IssuedByUserId { get; private init; }

    /// <summary>Gets when the code was redeemed, if it has been.</summary>
    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    /// <summary>Gets how many times redemption has been attempted and failed.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Gets a value indicating whether the code was burned before use.</summary>
    public bool IsBurned { get; private set; }

    /// <summary>Issues a code for a registered device.</summary>
    /// <param name="deviceId">The device to enrol.</param>
    /// <param name="codeHash">SHA-256 hash of the generated code.</param>
    /// <param name="issuedAtUtc">Server time.</param>
    /// <param name="lifetime">How long the code remains valid.</param>
    /// <param name="issuedByUserId">Who issued it.</param>
    /// <returns>The code record.</returns>
    public static DeviceEnrolmentCode Issue(
        DeviceId deviceId,
        byte[] codeHash,
        DateTimeOffset issuedAtUtc,
        TimeSpan lifetime,
        UserId issuedByUserId)
    {
        ArgumentNullException.ThrowIfNull(codeHash);

        return new DeviceEnrolmentCode(
            Guid.CreateVersion7(),
            deviceId,
            codeHash,
            issuedAtUtc,
            issuedAtUtc.Add(lifetime),
            issuedByUserId);
    }

    /// <summary>Determines whether the code can still be redeemed.</summary>
    /// <param name="atUtc">The moment to test.</param>
    /// <returns><see langword="true"/> when the code is live.</returns>
    public bool IsRedeemableAt(DateTimeOffset atUtc)
        => RedeemedAtUtc is null
           && !IsBurned
           && FailedAttempts < MaxRedemptionAttempts
           && ExpiresAtUtc > atUtc;

    /// <summary>Marks the code as used.</summary>
    /// <param name="atUtc">Server time.</param>
    /// <returns>Success, or the reason redemption was refused.</returns>
    public Result Redeem(DateTimeOffset atUtc)
    {
        if (!IsRedeemableAt(atUtc))
        {
            return Result.Failure(new Error(
                "device.enrolment_code_invalid",
                "This enrolment code is not valid. Ask an administrator for a new one.",
                ErrorType.Unauthenticated));
        }

        RedeemedAtUtc = atUtc;
        return Result.Success();
    }

    /// <summary>Records a failed redemption attempt, burning the code if repeated.</summary>
    public void RecordFailedAttempt()
    {
        FailedAttempts++;

        if (FailedAttempts >= MaxRedemptionAttempts)
        {
            IsBurned = true;
        }
    }
}

/// <summary>
/// One period during which a user was signed in on a device.
/// </summary>
/// <remarks>
/// Kept separately from tokens so an administrator can see and end a session
/// without reasoning about token chains, and so "who was on lane 3 at 14:20" is
/// answerable after the tokens have long expired.
/// </remarks>
public sealed class DeviceSession : Entity<Guid>
{
    private DeviceSession(
        Guid id,
        DeviceId deviceId,
        UserId userId,
        Guid refreshTokenFamilyId,
        DateTimeOffset startedAtUtc,
        string? ipAddress,
        string? appVersion)
    {
        Id = id;
        DeviceId = deviceId;
        UserId = userId;
        RefreshTokenFamilyId = refreshTokenFamilyId;
        StartedAtUtc = startedAtUtc;
        IpAddress = ipAddress;
        AppVersion = appVersion;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private DeviceSession()
    {
    }

    /// <summary>Gets the device.</summary>
    public DeviceId DeviceId { get; private init; }

    /// <summary>Gets the signed-in user.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets the token family backing the session.</summary>
    public Guid RefreshTokenFamilyId { get; private init; }

    /// <summary>Gets when the session started.</summary>
    public DateTimeOffset StartedAtUtc { get; private init; }

    /// <summary>Gets when the session ended, if it has.</summary>
    public DateTimeOffset? EndedAtUtc { get; private set; }

    /// <summary>Gets why the session ended.</summary>
    public string? EndedReason { get; private set; }

    /// <summary>Gets the address the session started from.</summary>
    public string? IpAddress { get; private init; }

    /// <summary>Gets the application version reported at sign-in.</summary>
    public string? AppVersion { get; private init; }

    /// <summary>Opens a session.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="userId">The user.</param>
    /// <param name="refreshTokenFamilyId">The token family.</param>
    /// <param name="startedAtUtc">Server time.</param>
    /// <param name="ipAddress">Caller address.</param>
    /// <param name="appVersion">Reported application version.</param>
    /// <returns>The session.</returns>
    public static DeviceSession Start(
        DeviceId deviceId,
        UserId userId,
        Guid refreshTokenFamilyId,
        DateTimeOffset startedAtUtc,
        string? ipAddress,
        string? appVersion)
        => new(Guid.CreateVersion7(), deviceId, userId, refreshTokenFamilyId, startedAtUtc, ipAddress, appVersion);

    /// <summary>Closes the session.</summary>
    /// <param name="atUtc">Server time.</param>
    /// <param name="reason">Why it ended.</param>
    public void End(DateTimeOffset atUtc, string reason)
    {
        if (EndedAtUtc is not null)
        {
            return;
        }

        EndedAtUtc = atUtc;
        EndedReason = reason;
    }
}
