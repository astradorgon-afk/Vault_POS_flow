using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Configuration;

namespace Pos.Infrastructure.Identity;

/// <summary>The claim types this system issues beyond the registered ones.</summary>
public static class PosClaimTypes
{
    /// <summary>The device the token is bound to.</summary>
    public const string DeviceId = "device_id";

    /// <summary>The user's primary location.</summary>
    public const string PrimaryLocation = "loc";

    /// <summary>Identity's security stamp, which changes when the account changes.</summary>
    public const string SecurityStamp = "sstamp";

    /// <summary>The authorization policy version in force when the token was issued.</summary>
    public const string PolicyVersion = "policy_ver";

    /// <summary>The user's approval tier.</summary>
    public const string ApprovalTier = "tier";

    /// <summary>How the user authenticated: <c>pwd</c> or <c>pin</c>.</summary>
    public const string AuthenticationMethod = "amr";
}

/// <summary>An issued access token.</summary>
/// <param name="Token">The encoded JWT.</param>
/// <param name="ExpiresAtUtc">When it stops being accepted.</param>
/// <param name="TokenId">The token's unique identifier, for correlation.</param>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc, Guid TokenId);

/// <summary>A refresh token, in the only two forms that exist.</summary>
/// <param name="Value">The opaque value handed to the client. Never stored.</param>
/// <param name="Hash">The SHA-256 hash, which is what the database keeps.</param>
public sealed record RefreshTokenMaterial(string Value, byte[] Hash);

/// <summary>Issues access and refresh tokens.</summary>
public interface ITokenService
{
    /// <summary>Issues a short-lived access token.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="displayName">The user's display name.</param>
    /// <param name="securityStamp">Identity's security stamp for the user.</param>
    /// <param name="policyVersion">The authorization policy version.</param>
    /// <param name="approvalTier">The user's approval tier.</param>
    /// <param name="authenticationMethod">How the user authenticated.</param>
    /// <param name="deviceId">The device the token is bound to, if any.</param>
    /// <param name="primaryLocationId">The user's primary location, if any.</param>
    /// <returns>The token.</returns>
    AccessToken IssueAccessToken(
        UserId userId,
        string displayName,
        string securityStamp,
        long policyVersion,
        Domain.Identity.ApprovalTier approvalTier,
        string authenticationMethod,
        DeviceId? deviceId,
        LocationId? primaryLocationId);

    /// <summary>Generates a refresh token and its hash.</summary>
    /// <returns>The value to return to the client, and the hash to persist.</returns>
    RefreshTokenMaterial CreateRefreshToken();

    /// <summary>Hashes a presented refresh token so it can be looked up.</summary>
    /// <param name="value">The value the client presented.</param>
    /// <returns>The hash.</returns>
    byte[] HashRefreshToken(string value);

    /// <summary>Generates a device enrolment code and its hash.</summary>
    /// <returns>The code to show the administrator, and the hash to persist.</returns>
    RefreshTokenMaterial CreateEnrolmentCode();
}

/// <summary>
/// Issues RS256-signed access tokens and cryptographically random refresh tokens.
/// </summary>
/// <remarks>
/// <para>
/// Access tokens carry identity and context but <b>not</b> a permission list.
/// The catalogue has dozens of entries, embedding them would bloat every
/// request, and worse, a permission removed from a role would keep working
/// until the token expired.
/// </para>
/// <para>
/// Refresh tokens are 256 bits of cryptographic randomness. Only their hash is
/// stored, so reading the database does not hand anyone a live session.
/// </para>
/// </remarks>
/// <param name="options">Token settings.</param>
/// <param name="keyRing">The signing keys.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class JwtTokenService(
    IOptions<JwtOptions> options,
    SigningKeyRing keyRing,
    ISystemClock clock) : ITokenService
{
    private const int RefreshTokenBytes = 32;
    private const int EnrolmentCodeBytes = 8;

    private readonly JwtOptions _options = options.Value;

    /// <inheritdoc />
    public AccessToken IssueAccessToken(
        UserId userId,
        string displayName,
        string securityStamp,
        long policyVersion,
        Domain.Identity.ApprovalTier approvalTier,
        string authenticationMethod,
        DeviceId? deviceId,
        LocationId? primaryLocationId)
    {
        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset expires = now.Add(_options.AccessTokenLifetime);
        Guid tokenId = Guid.CreateVersion7();

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, userId.Value.ToString("D", CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, tokenId.ToString("D", CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Name, displayName),
            new(PosClaimTypes.SecurityStamp, securityStamp),
            new(PosClaimTypes.PolicyVersion, policyVersion.ToString(CultureInfo.InvariantCulture)),
            new(PosClaimTypes.ApprovalTier, ((int)approvalTier).ToString(CultureInfo.InvariantCulture)),
            new(PosClaimTypes.AuthenticationMethod, authenticationMethod),
        ];

        if (deviceId is { } device)
        {
            claims.Add(new Claim(PosClaimTypes.DeviceId, device.Value.ToString("D", CultureInfo.InvariantCulture)));
        }

        if (primaryLocationId is { } location)
        {
            claims.Add(new Claim(
                PosClaimTypes.PrimaryLocation,
                location.Value.ToString("D", CultureInfo.InvariantCulture)));
        }

        JwtSecurityToken token = new(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: keyRing.SigningCredentials);

        string encoded = new JwtSecurityTokenHandler().WriteToken(token);

        return new AccessToken(encoded, expires, tokenId);
    }

    /// <inheritdoc />
    public RefreshTokenMaterial CreateRefreshToken()
    {
        byte[] raw = RandomNumberGenerator.GetBytes(RefreshTokenBytes);
        string value = Base64UrlEncoder.Encode(raw);

        return new RefreshTokenMaterial(value, HashRefreshToken(value));
    }

    /// <inheritdoc />
    public byte[] HashRefreshToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    /// <inheritdoc />
    public RefreshTokenMaterial CreateEnrolmentCode()
    {
        // Short enough for an administrator to read out over the phone, long
        // enough that guessing it inside its fifteen-minute window is hopeless.
        byte[] raw = RandomNumberGenerator.GetBytes(EnrolmentCodeBytes);
        string value = Convert.ToHexString(raw);

        return new RefreshTokenMaterial(value, HashRefreshToken(value));
    }
}

/// <summary>
/// The signing keys in use, current plus one predecessor.
/// </summary>
/// <remarks>
/// Keeping the previous key for validation means a key can be rotated without
/// signing everyone out: tokens minted under the old key stay valid until they
/// expire, which for a ten-minute access token is a ten-minute overlap.
/// </remarks>
public sealed class SigningKeyRing : IDisposable
{
    private readonly RSA _current;
    private readonly RSA? _previous;
    private bool _disposed;

    /// <summary>Initializes the key ring from configuration.</summary>
    /// <param name="options">Token settings carrying the PEM-encoded keys.</param>
    /// <exception cref="InvalidOperationException">The current key is missing or malformed.</exception>
    public SigningKeyRing(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        JwtOptions value = options.Value;

        _current = RSA.Create();

        try
        {
            _current.ImportFromPem(value.SigningKeyPem);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKeyPem is not a valid PEM-encoded RSA key.", ex);
        }

        if (_current.KeySize < 2048)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The token signing key must be at least 2048 bits; it is {_current.KeySize}."));
        }

        SigningCredentials = new SigningCredentials(
            new RsaSecurityKey(_current) { KeyId = Thumbprint(_current) },
            SecurityAlgorithms.RsaSha256);

        if (!string.IsNullOrWhiteSpace(value.PreviousSigningKeyPem))
        {
            _previous = RSA.Create();
            _previous.ImportFromPem(value.PreviousSigningKeyPem);
        }
    }

    /// <summary>Gets the credentials used to sign new tokens.</summary>
    public SigningCredentials SigningCredentials { get; }

    /// <summary>Gets every key a token may legitimately have been signed with.</summary>
    /// <returns>The validation keys.</returns>
    public IEnumerable<SecurityKey> ValidationKeys()
    {
        yield return new RsaSecurityKey(_current) { KeyId = Thumbprint(_current) };

        if (_previous is not null)
        {
            yield return new RsaSecurityKey(_previous) { KeyId = Thumbprint(_previous) };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _current.Dispose();
        _previous?.Dispose();
        _disposed = true;
    }

    private static string Thumbprint(RSA key)
        => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];
}
