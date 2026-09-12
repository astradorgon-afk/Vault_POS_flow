using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Api.Authorization;

/// <summary>Where an endpoint's location scope comes from.</summary>
public enum ScopeSource
{
    /// <summary>The permission is checked without a location. Business-wide actions only.</summary>
    None = 0,

    /// <summary>A route value names the location, by default <c>locationId</c>.</summary>
    RouteValue = 1,

    /// <summary>A query-string value names the location.</summary>
    QueryValue = 2,

    /// <summary>The calling device's own location. Used by point-of-sale and sync endpoints.</summary>
    CallingDevice = 3,
}

/// <summary>
/// Declares the permission an endpoint requires, and where its location scope
/// comes from.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint that touches business data carries one of these. An endpoint
/// without it — and without an explicit anonymous marker — fails an architecture
/// test, so forgetting to protect a new route breaks the build rather than
/// shipping quietly.
/// </para>
/// <para>
/// This is one of two independent server-side checks. The application pipeline
/// checks again, so a command reaching the handler from a background worker or
/// the sync processor is authorized identically.
/// </para>
/// </remarks>
/// <param name="permission">The permission code required.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequirePermissionAttribute(string permission)
    : AuthorizeAttribute(BuildPolicyName(permission, ScopeSource.None, null))
{
    /// <summary>The prefix marking a dynamically built permission policy.</summary>
    public const string PolicyPrefix = "perm:";

    private ScopeSource _scope = ScopeSource.None;
    private string? _scopeKey;

    /// <summary>Gets the permission code required.</summary>
    public string Permission { get; } = permission;

    /// <summary>Gets or sets where the location scope comes from.</summary>
    public ScopeSource Scope
    {
        get => _scope;
        set
        {
            _scope = value;
            Policy = BuildPolicyName(Permission, _scope, _scopeKey);
        }
    }

    /// <summary>
    /// Gets or sets the route or query key naming the location. Defaults to
    /// <c>locationId</c>.
    /// </summary>
    public string? ScopeKey
    {
        get => _scopeKey;
        set
        {
            _scopeKey = value;
            Policy = BuildPolicyName(Permission, _scope, _scopeKey);
        }
    }

    /// <summary>Encodes a requirement as a policy name.</summary>
    /// <param name="permission">The permission code.</param>
    /// <param name="scope">Where the location comes from.</param>
    /// <param name="scopeKey">The route or query key, if any.</param>
    /// <returns>The policy name.</returns>
    public static string BuildPolicyName(string permission, ScopeSource scope, string? scopeKey)
        => FormattableString.Invariant($"{PolicyPrefix}{permission}|{scope}|{scopeKey}");

    /// <summary>Decodes a policy name back into a requirement.</summary>
    /// <param name="policyName">The policy name.</param>
    /// <returns>The requirement, or null when the name is not one of ours.</returns>
    public static PermissionRequirement? Parse(string policyName)
    {
        if (!policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = policyName[PolicyPrefix.Length..].Split('|');

        if (parts.Length != 3 || !Enum.TryParse(parts[1], out ScopeSource scope))
        {
            return null;
        }

        return new PermissionRequirement(
            parts[0],
            scope,
            string.IsNullOrEmpty(parts[2]) ? null : parts[2]);
    }
}

/// <summary>
/// Marks an endpoint that is deliberately reachable without authentication.
/// </summary>
/// <remarks>
/// Sign-in, token refresh, device enrolment and the health probes need this.
/// Requiring the marker rather than allowing silence means "this route is
/// public" is always a decision someone wrote down.
/// </remarks>
/// <param name="reason">Why the endpoint is public.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class PublicEndpointAttribute(string reason) : Attribute
{
    /// <summary>Gets the stated reason the endpoint is public.</summary>
    public string Reason { get; } = reason;
}

/// <summary>An authorization requirement for one permission at one scope.</summary>
/// <param name="Permission">The permission code.</param>
/// <param name="Scope">Where the location comes from.</param>
/// <param name="ScopeKey">The route or query key naming the location.</param>
public sealed record PermissionRequirement(
    string Permission,
    ScopeSource Scope,
    string? ScopeKey) : IAuthorizationRequirement;

/// <summary>
/// Materialises a policy for each distinct permission requirement on demand.
/// </summary>
/// <remarks>
/// The catalogue has dozens of permissions, each usable at several scopes.
/// Registering every combination up front would be hundreds of policies, most
/// never used; building them lazily from the policy name keeps registration to
/// nothing and cannot drift from the attributes.
/// </remarks>
/// <param name="options">Authorization options.</param>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        PermissionRequirement? requirement = RequirePermissionAttribute.Parse(policyName);

        if (requirement is null)
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(requirement)
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>Evaluates a permission requirement against the caller.</summary>
/// <param name="permissions">The permission evaluator.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="httpContextAccessor">Access to the request, for route and query scope.</param>
public sealed class PermissionAuthorizationHandler(
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (currentUser.UserId is not { } userId)
        {
            return;
        }

        // A permission that does not exist in the catalogue would otherwise be
        // an unsatisfiable check that reads like a working one.
        if (!Permissions.IsDefined(requirement.Permission))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Endpoint requires permission {requirement.Permission}, which is not in the catalogue."));
        }

        LocationId? scope = ResolveScope(requirement);
        CancellationToken cancellationToken = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        bool authorized = await permissions
            .HasPermissionAsync(userId, requirement.Permission, scope, cancellationToken)
            .ConfigureAwait(false);

        if (authorized)
        {
            context.Succeed(requirement);
        }
    }

    private LocationId? ResolveScope(PermissionRequirement requirement)
    {
        HttpContext? http = httpContextAccessor.HttpContext;

        if (http is null)
        {
            return null;
        }

        string key = requirement.ScopeKey ?? "locationId";

        return requirement.Scope switch
        {
            ScopeSource.RouteValue => Parse(http.Request.RouteValues.TryGetValue(key, out object? v)
                ? v?.ToString()
                : null),
            ScopeSource.QueryValue => Parse(http.Request.Query.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues q)
                ? q.ToString()
                : null),
            ScopeSource.CallingDevice => http.User.FindFirst(Infrastructure.Identity.PosClaimTypes.PrimaryLocation) is
                { Value: { Length: > 0 } claim }
                ? Parse(claim)
                : null,
            _ => null,
        };
    }

    private static LocationId? Parse(string? raw)
        => Guid.TryParse(raw, out Guid value) ? new LocationId(value) : null;
}
