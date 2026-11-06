using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pos.Web.Services;

namespace Pos.Web.Security;

/// <summary>Projects an API session into the claims the app authorizes on.</summary>
/// <remarks>
/// One projection, used by both gates: the authorization middleware on an HTTP
/// request and <see cref="VaultFlowAuthenticationStateProvider"/> inside the
/// circuit. A page that opens after a reload must be the same page that opens
/// after a click, so the two cannot be allowed to drift.
/// </remarks>
public static class WebSessionClaims
{
    /// <summary>Builds the principal for a signed-in session.</summary>
    /// <param name="credentials">The API credentials held for this browser.</param>
    /// <param name="authenticationType">The name this identity was authenticated by.</param>
    /// <returns>A principal carrying the account's roles, permissions and locations.</returns>
    public static ClaimsPrincipal Principal(SignInResponse credentials, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, credentials.User.Id.ToString("D")),
            new(ClaimTypes.Name, credentials.User.DisplayName),
        ];
        claims.AddRange(credentials.User.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(credentials.User.Permissions.Select(permission => new Claim("permission", permission)));
        claims.AddRange(credentials.User.Locations.Select(location => new Claim("location", location.ToString("D"))));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }
}

/// <summary>
/// Authenticates an HTTP request from the session its browser already holds.
/// </summary>
/// <remarks>
/// <para>
/// A page marked <c>[Authorize]</c> carries that attribute as endpoint
/// metadata, so the authorization middleware judges every full page load from
/// <see cref="HttpContext.User"/> — long before the component renders. The
/// signed-in session lives in <see cref="WebSessions"/>, not in an
/// authentication cookie, so that user was always anonymous and the middleware
/// challenged: reloading signed the operator out even though the server still
/// held the session, while clicking through the app, which never leaves the
/// circuit and never reaches the middleware, kept working.
/// </para>
/// <para>
/// Reading the session here also means the access and refresh tokens stay in
/// this process. The request carries nothing but the opaque cookie key.
/// </para>
/// </remarks>
public sealed class WebSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The name of this authentication scheme.</summary>
    public const string SchemeName = "VaultFlowSession";

    private readonly WebSessions sessions;

    /// <summary>Creates the handler.</summary>
    /// <param name="sessions">The signed-in sessions this server holds.</param>
    /// <param name="options">The scheme options monitor.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    public WebSessionAuthenticationHandler(
        WebSessions sessions,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
        => this.sessions = sessions;

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Context.Request.Cookies.TryGetValue(WebSessionEndpoints.CookieName, out string? key) ||
            sessions.Get(key) is not { } session)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            WebSessionClaims.Principal(session.Credentials, SchemeName), SchemeName)));
    }

    /// <summary>Sends an unauthenticated request to the sign-in form, with where it was headed.</summary>
    /// <param name="properties">The challenge properties.</param>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        string wanted = $"{Context.Request.Path}{Context.Request.QueryString}";
        Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(wanted)}");
        return Task.CompletedTask;
    }
}

/// <summary>Registers the session authentication scheme.</summary>
public static class WebSessionAuthenticationExtensions
{
    /// <summary>Adds the scheme that authenticates a request from its stored session.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddWebSessionAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(WebSessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, WebSessionAuthenticationHandler>(
                WebSessionAuthenticationHandler.SchemeName, displayName: null, configureOptions: _ => { });
        return services;
    }
}
