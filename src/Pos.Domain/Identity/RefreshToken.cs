using Pos.Domain.Common;

namespace Pos.Domain.Identity;

/// <summary>Why a refresh token stopped being usable.</summary>
public enum RefreshTokenRevocationReason
{
    /// <summary>Still valid.</summary>
    None = 0,

    /// <summary>Rotated normally: the holder exchanged it for a successor.</summary>
    Rotated = 1,

    /// <summary>The user signed out.</summary>
    SignedOut = 2,

    /// <summary>An administrator revoked the session.</summary>
    AdministrativelyRevoked = 3,

    /// <summary>The user was disabled or their password changed.</summary>
    SecurityStampChanged = 4,

    /// <summary>The device was suspended or revoked.</summary>
    DeviceRevoked = 5,

    /// <summary>
    /// An already-rotated token was presented, so the whole family was burned.
    /// </summary>
    ReuseDetected = 6,
}

/// <summary>
/// A refresh token, stored only as a hash.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are single-use and rotate on every refresh. The predecessor is kept,
/// marked <see cref="RefreshTokenRevocationReason.Rotated"/> and pointed at its
/// successor, which is what makes reuse detectable: if a rotated token is ever
/// presented again, either it was stolen or the legitimate holder's successor
/// was stolen. Either way the entire family is revoked and the user re-
/// authenticates.
/// </para>
/// <para>
/// The raw token value never touches the database. Only a SHA-256 hash is
/// stored, so a database disclosure does not hand out live sessions.
/// </para>
/// </remarks>
public sealed class RefreshToken : Entity<Guid>
{
    private RefreshToken(
        Guid id,
        Guid familyId,
        UserId userId,
        DeviceId? deviceId,
        byte[] tokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string? issuedToIpAddress)
    {
        Id = id;
        FamilyId = familyId;
        UserId = userId;
        DeviceId = deviceId;
        TokenHash = tokenHash;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        IssuedToIpAddress = issuedToIpAddress;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private RefreshToken()
    {
        TokenHash = [];
    }

    /// <summary>
    /// Gets the identifier shared by every token descended from one sign-in.
    /// Revoking a family ends the whole chain at once.
    /// </summary>
    public Guid FamilyId { get; private init; }

    /// <summary>Gets the user the token authenticates.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets the device the token is bound to, when issued to one.</summary>
    public DeviceId? DeviceId { get; private init; }

    /// <summary>Gets the SHA-256 hash of the token value. The value itself is never stored.</summary>
    public byte[] TokenHash { get; private init; }

    /// <summary>Gets when the token was issued.</summary>
    public DateTimeOffset IssuedAtUtc { get; private init; }

    /// <summary>Gets when the token expires.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private init; }

    /// <summary>Gets the address the token was issued to.</summary>
    public string? IssuedToIpAddress { get; private init; }

    /// <summary>Gets when the token was revoked, if it has been.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Gets why the token was revoked.</summary>
    public RefreshTokenRevocationReason RevocationReason { get; private set; }

    /// <summary>Gets the token that replaced this one when it rotated.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    /// <summary>Gets a value indicating whether a rotated token was presented again.</summary>
    public bool ReuseDetected { get; private set; }

    /// <summary>Determines whether the token can still be exchanged.</summary>
    /// <param name="atUtc">The moment to test.</param>
    /// <returns><see langword="true"/> when the token is live.</returns>
    public bool IsActiveAt(DateTimeOffset atUtc) => RevokedAtUtc is null && ExpiresAtUtc > atUtc;

    /// <summary>Gets a value indicating whether this token was already exchanged.</summary>
    public bool WasRotated => RevocationReason == RefreshTokenRevocationReason.Rotated;

    /// <summary>Issues the first token of a new family, at sign-in.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device, when the session is device-bound.</param>
    /// <param name="tokenHash">SHA-256 hash of the token value.</param>
    /// <param name="issuedAtUtc">Server time.</param>
    /// <param name="lifetime">How long the token remains valid.</param>
    /// <param name="ipAddress">The address it was issued to.</param>
    /// <returns>The token.</returns>
    public static RefreshToken IssueNewFamily(
        UserId userId,
        DeviceId? deviceId,
        byte[] tokenHash,
        DateTimeOffset issuedAtUtc,
        TimeSpan lifetime,
        string? ipAddress)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        return new RefreshToken(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            userId,
            deviceId,
            tokenHash,
            issuedAtUtc,
            issuedAtUtc.Add(lifetime),
            ipAddress);
    }

    /// <summary>
    /// Exchanges this token for a successor in the same family.
    /// </summary>
    /// <param name="tokenHash">SHA-256 hash of the new token value.</param>
    /// <param name="atUtc">Server time.</param>
    /// <param name="lifetime">How long the successor remains valid.</param>
    /// <param name="ipAddress">The address the successor is issued to.</param>
    /// <returns>The successor token, or the reason the exchange was refused.</returns>
    public Result<RefreshToken> Rotate(
        byte[] tokenHash,
        DateTimeOffset atUtc,
        TimeSpan lifetime,
        string? ipAddress)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (!IsActiveAt(atUtc))
        {
            return Result<RefreshToken>.Failure(new Error(
                "auth.refresh_token_invalid",
                "This session has expired. Sign in again.",
                ErrorType.Unauthenticated));
        }

        RefreshToken successor = new(
            Guid.CreateVersion7(),
            FamilyId,
            UserId,
            DeviceId,
            tokenHash,
            atUtc,
            atUtc.Add(lifetime),
            ipAddress);

        RevokedAtUtc = atUtc;
        RevocationReason = RefreshTokenRevocationReason.Rotated;
        ReplacedByTokenId = successor.Id;

        return Result<RefreshToken>.Success(successor);
    }

    /// <summary>Revokes the token.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="atUtc">Server time.</param>
    public void Revoke(RefreshTokenRevocationReason reason, DateTimeOffset atUtc)
    {
        if (RevokedAtUtc is not null && reason != RefreshTokenRevocationReason.ReuseDetected)
        {
            return;
        }

        RevokedAtUtc ??= atUtc;
        RevocationReason = reason;
    }

    /// <summary>
    /// Records that this already-rotated token was presented again, which is
    /// evidence of theft.
    /// </summary>
    /// <param name="atUtc">Server time.</param>
    public void FlagReuse(DateTimeOffset atUtc)
    {
        ReuseDetected = true;
        RevokedAtUtc ??= atUtc;
        RevocationReason = RefreshTokenRevocationReason.ReuseDetected;
    }
}
