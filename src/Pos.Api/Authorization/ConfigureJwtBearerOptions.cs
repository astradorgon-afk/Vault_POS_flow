using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Identity;

namespace Pos.Api.Authorization;

/// <summary>
/// Configures bearer-token validation from the application's own settings and
/// key ring.
/// </summary>
/// <remarks>
/// Done through <see cref="IConfigureNamedOptions{TOptions}"/> rather than in the
/// <c>AddJwtBearer</c> callback, because that callback has no service provider
/// and reaching for one there would build a second container: a separate set of
/// singletons, including a second signing key ring, that nothing else uses.
/// </remarks>
/// <param name="jwtOptions">Token settings.</param>
/// <param name="keyRing">The signing keys.</param>
public sealed class ConfigureJwtBearerOptions(
    IOptions<JwtOptions> jwtOptions,
    SigningKeyRing keyRing) : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    /// <inheritdoc />
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (!string.Equals(name, JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return;
        }

        Configure(options);
    }

    /// <inheritdoc />
    public void Configure(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Claim names are used exactly as issued. The default inbound mapping
        // rewrites "sub" to a long URI, which then silently fails to match the
        // claim this application reads.
        options.MapInboundClaims = false;
        options.SaveToken = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keyRing.ValidationKeys(),

            // Pinned. Accepting whichever algorithm a token nominates is how
            // algorithm-confusion attacks get in, including the "none" family.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],

            ClockSkew = TimeSpan.FromSeconds(_jwt.ClockSkewSeconds),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };

        // A valid signature proves only what was true when the token was issued.
        // These events re-check the account and the device on every request.
        options.EventsType = typeof(PosJwtBearerEvents);
    }
}
