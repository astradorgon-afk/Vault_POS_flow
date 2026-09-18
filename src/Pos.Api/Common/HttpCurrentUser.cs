using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Pos.Api.Middleware;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Identity;

namespace Pos.Api.Common;

/// <summary>
/// Reads the caller's identity from the current request.
/// </summary>
/// <remarks>
/// <para>
/// Everything here comes from the validated token or from middleware, never from
/// a request body. A caller cannot nominate who they are.
/// </para>
/// <para>
/// Note what is deliberately absent: the caller's permissions. They are resolved
/// server-side per request from the database, so authority taken away is gone at
/// once rather than at the token's next expiry.
/// </para>
/// </remarks>
/// <param name="accessor">Access to the current request.</param>
/// <param name="replay">A trusted scoped identity used by server-side event replay.</param>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor, ICurrentUserOverride replay) : ICurrentUser
{
    /// <inheritdoc />
    public UserId? UserId
        => replay.UserId
           ?? (ReadGuidClaim(JwtRegisteredClaimNames.Sub) is { } value ? new UserId(value) : null);

    /// <inheritdoc />
    public DeviceId? DeviceId
    {
        get
        {
            if (replay.DeviceId is { } replayDevice)
            {
                return replayDevice;
            }

            // The token's binding wins over the header. A device-bound token
            // presented with someone else's device header is not a different
            // device, it is a misuse of that token.
            if (ReadGuidClaim(PosClaimTypes.DeviceId) is { } fromToken)
            {
                return new DeviceId(fromToken);
            }

            return accessor.HttpContext?.Items.TryGetValue(RequestContextMiddleware.DeviceItemKey, out object? raw) == true
                   && raw is Guid header
                ? new DeviceId(header)
                : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<LocationId> AssignedLocations
        => replay.LocationId is { } location
            ? [location]
            : ReadGuidClaim(PosClaimTypes.PrimaryLocation) is { } claimLocation
                ? [new LocationId(claimLocation)]
                : [];

    /// <inheritdoc />
    public bool HasAllLocations => false;

    /// <inheritdoc />
    public CorrelationId CorrelationId
        => replay.CorrelationId.Value != Guid.Empty
            ? replay.CorrelationId
            : accessor.HttpContext?.Items.TryGetValue(RequestContextMiddleware.CorrelationItemKey, out object? raw) == true
           && raw is Guid correlation
            ? new CorrelationId(correlation)
            : new CorrelationId(Guid.Empty);

    /// <inheritdoc />
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <inheritdoc />
    public string? UserAgent
    {
        get
        {
            string? value = accessor.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : Truncate(value, 512);
        }
    }

    /// <inheritdoc />
    public string? RoleSnapshot
    {
        get
        {
            string[] roles = accessor.HttpContext?.User
                .FindAll(ClaimTypes.Role)
                .Select(c => c.Value)
                .ToArray() ?? [];

            return roles.Length == 0 ? null : string.Join(", ", roles);
        }
    }

    /// <summary>Gets the authorization policy version the caller's token was issued under.</summary>
    public long? PolicyVersion
    {
        get
        {
            string? raw = accessor.HttpContext?.User.FindFirst(PosClaimTypes.PolicyVersion)?.Value;

            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : null;
        }
    }

    private Guid? ReadGuidClaim(string claimType)
    {
        string? raw = accessor.HttpContext?.User.FindFirst(claimType)?.Value;
        return Guid.TryParse(raw, out Guid value) ? value : null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
