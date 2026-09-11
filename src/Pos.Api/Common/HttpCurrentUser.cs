using Pos.Api.Middleware;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Api.Common;

/// <summary>
/// Reads the caller's identity from the current HTTP request.
/// </summary>
/// <remarks>
/// Phase 1 wires the shape of the caller without an identity provider behind it:
/// until Phase 2 lands, no user is authenticated and every permission-bearing
/// message therefore fails closed with an authentication error. That is the
/// intended behaviour — an unauthenticated request must never be treated as an
/// authorized one, not even temporarily during development.
/// </remarks>
/// <param name="accessor">Access to the current request.</param>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <inheritdoc />
    public UserId? UserId => ReadGuidClaim("sub") is { } value ? new UserId(value) : null;

    /// <inheritdoc />
    public DeviceId? DeviceId
        => accessor.HttpContext?.Items.TryGetValue(RequestContextMiddleware.DeviceItemKey, out object? raw) == true
           && raw is Guid device
            ? new DeviceId(device)
            : null;

    /// <inheritdoc />
    public IReadOnlyCollection<LocationId> AssignedLocations { get; } = [];

    /// <inheritdoc />
    public bool HasAllLocations => false;

    /// <inheritdoc />
    public CorrelationId CorrelationId
        => accessor.HttpContext?.Items.TryGetValue(RequestContextMiddleware.CorrelationItemKey, out object? raw) == true
           && raw is Guid correlation
            ? new CorrelationId(correlation)
            : new CorrelationId(Guid.Empty);

    /// <inheritdoc />
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private Guid? ReadGuidClaim(string claimType)
    {
        string? raw = accessor.HttpContext?.User.FindFirst(claimType)?.Value;
        return Guid.TryParse(raw, out Guid value) ? value : null;
    }
}

/// <summary>
/// Refuses every permission until the identity module is in place.
/// </summary>
/// <remarks>
/// Failing closed is deliberate. A development stub that returns
/// <see langword="true"/> is how authorization holes ship: this one makes an
/// unfinished module obvious rather than invisible.
/// </remarks>
public sealed class DenyAllPermissionEvaluator : IPermissionEvaluator
{
    /// <inheritdoc />
    public Task<bool> HasPermissionAsync(
        UserId userId,
        string permissionCode,
        LocationId? locationId,
        CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        UserId userId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
}
