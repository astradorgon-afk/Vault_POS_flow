using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pos.Web.Security;
using Pos.Web.Services;

namespace Pos.Web.Tests;

/// <summary>
/// Who the API is told is signing in. Every web visitor's call leaves from this
/// server, so unless the browser is passed on, the API's per-address sign-in
/// limits count all of them together and one visitor can lock out the rest.
/// </summary>
public sealed class ForwardedCallerTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("172.18.0.1");
    private static readonly IPAddress Visitor = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task SigningIn_TellsTheApiWhichBrowserIsAsking()
    {
        Recorder recorder = new();
        ForwardedCaller caller = new();
        caller.CaptureFrom(Request(Visitor, https: true));

        await Client(recorder, caller).SignInAsync("cashier", "wrong-password", null, CancellationToken.None);

        recorder.Request!.RequestUri!.AbsolutePath.Should().Be("/api/v1/auth/login");
        recorder.Request.Headers.GetValues("X-Forwarded-For").Should().Equal("203.0.113.7");
        recorder.Request.Headers.GetValues("X-Forwarded-Proto").Should().Equal("https");
    }

    [Fact]
    public void AnIpv4BrowserOnADualStackSocket_IsCountedUnderItsIpv4Address()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/products");

        ForwardedCaller.Apply(request, Visitor.MapToIPv6(), isHttps: false);

        request.Headers.GetValues("X-Forwarded-For").Should().Equal("203.0.113.7");
    }

    [Fact]
    public void AnUnknownBrowser_ForwardsNothing()
    {
        // The API then counts this server, as it did before forwarding existed.
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/products");

        new ForwardedCaller().ApplyTo(request);

        request.Headers.Contains("X-Forwarded-For").Should().BeFalse();
        request.Headers.Contains("X-Forwarded-Proto").Should().BeFalse();
    }

    [Fact]
    public async Task AConfiguredProxyNetwork_MayNameTheVisitor()
    {
        // What the Cloudflare tunnel looks like from inside the web container:
        // the Docker gateway, carrying the visitor in X-Forwarded-For.
        HttpContext context = Request(Gateway, https: false);
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.20, 203.0.113.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await Forward(context, ("ForwardedHeaders:KnownNetworks:0", "172.16.0.0/12"));

        // Only the hop the proxy added counts; what the browser wrote is ignored.
        context.Connection.RemoteIpAddress.Should().Be(Visitor);
        context.Request.IsHttps.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnlistedPeer_CannotNameItsOwnAddress()
    {
        HttpContext context = Request(IPAddress.Parse("198.51.100.20"), https: false);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        await Forward(context, ("ForwardedHeaders:KnownNetworks:0", "172.16.0.0/12"));

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("198.51.100.20"));
    }

    [Fact]
    public async Task AConfiguredProxyAddress_IsTrustedOnItsOwn()
    {
        HttpContext context = Request(Gateway, https: false);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        await Forward(context, ("ForwardedHeaders:KnownProxies:0", "172.18.0.1"));

        context.Connection.RemoteIpAddress.Should().Be(Visitor);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "172.16.0.0")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "docker")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "172.18.0.0/16")]
    public void AnEntryThatIsNotAnAddress_StopsStartup(string key, string value)
    {
        // Skipping it would quietly trust nothing, or something unintended.
        Action configure = () => TrustedProxies.AddFrom(new ForwardedHeadersOptions(), Configuration((key, value)));

        configure.Should().Throw<InvalidOperationException>().WithMessage($"*'{value}'*");
    }

    private static VaultFlowApiClient Client(Recorder recorder, ForwardedCaller caller)
        => new(new HttpClient(recorder) { BaseAddress = new Uri("http://api:8080") }, new UserSession(), caller);

    private static DefaultHttpContext Request(IPAddress remote, bool https)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = remote;
        context.Request.Scheme = https ? "https" : "http";
        return context;
    }

    private static async Task Forward(HttpContext context, params (string Key, string Value)[] settings)
    {
        ForwardedHeadersOptions options = new()
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        TrustedProxies.AddFrom(options, Configuration(settings));

        ForwardedHeadersMiddleware middleware = new(
            _ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    /// <summary>Answers every call with 401 and keeps the request it was sent.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
