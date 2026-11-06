using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pos.Web.Security;
using Pos.Web.Services;

namespace Pos.Web.Tests;

/// <summary>
/// What a full page load is allowed to open. The authorization middleware
/// judges it from <c>HttpContext.User</c>, so a reload only keeps the operator
/// signed in while the request itself carries the session its browser holds.
/// </summary>
public sealed class WebSessionAuthenticationTests
{
    private static readonly Guid StoreOne = Guid.Parse("01a09bc3-2b37-7d90-8660-25e2ff65024f");

    [Fact]
    public async Task ABrowserHoldingASession_IsAuthenticatedWithItsPermissions()
    {
        WebSessions sessions = new();
        string key = sessions.Add(new WebSession { Credentials = Credentials(), WorkLocationId = StoreOne });

        AuthenticateResult result = await AuthenticateAsync(sessions, key);

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.HasClaim("permission", "inventory.view").Should().BeTrue();
        result.Principal.HasClaim(ClaimTypes.Role, "Manager").Should().BeTrue();
        result.Principal.HasClaim("location", StoreOne.ToString("D")).Should().BeTrue();
    }

    [Fact]
    public async Task ARequestWithoutTheCookie_IsNotAuthenticated()
        => (await AuthenticateAsync(new WebSessions(), cookie: null)).None.Should().BeTrue();

    [Fact]
    public async Task ACookieNamingASessionThisServerNoLongerHolds_IsNotAuthenticated()
    {
        // What a restart or a sign-out leaves behind. The browser keeps the
        // cookie; the session it names is gone, so the request stays anonymous
        // and the operator is asked to sign in rather than let through.
        WebSessions sessions = new();
        string key = sessions.Add(new WebSession { Credentials = Credentials() });
        sessions.Remove(key);

        (await AuthenticateAsync(sessions, key)).None.Should().BeTrue();
    }

    private static SignInResponse Credentials() => new(
        "access-token",
        DateTimeOffset.UtcNow.AddMinutes(10),
        "refresh-token",
        DateTimeOffset.UtcNow.AddDays(30),
        new SignedInUser(
            Guid.Parse("01a145db-0186-76cc-bfbd-35948cebaf64"),
            "Ako Lang",
            ["Manager"],
            ["inventory.view", "product.view"],
            [StoreOne],
            HasAllLocations: false,
            PolicyVersion: 1));

    private static async Task<AuthenticateResult> AuthenticateAsync(WebSessions sessions, string? cookie)
    {
        DefaultHttpContext context = new();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{WebSessionEndpoints.CookieName}={cookie}";
        }

        WebSessionAuthenticationHandler handler = new(
            sessions, new SchemeOptions(), NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(
                WebSessionAuthenticationHandler.SchemeName,
                displayName: null,
                typeof(WebSessionAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    /// <summary>The scheme has no options of its own; the handler still asks for them.</summary>
    private sealed class SchemeOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<AuthenticationSchemeOptions, string?> listener)
            => new Subscription();

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
