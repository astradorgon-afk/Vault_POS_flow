using System.Net;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Pos.Web.Services;

/// <summary>
/// The browser behind this server's API calls, passed on to the API as
/// X-Forwarded-For and X-Forwarded-Proto.
/// </summary>
/// <remarks>
/// Every API call leaves from this server, so without these the API sees a
/// single caller. Its sign-in limits are per address: ten attempts in five
/// minutes, and a lockout once an address has failed twenty times. Counted
/// against this server, one busy or hostile visitor would lock every visitor
/// out. The scheme matters less: forwarded, it tells the API the browser was
/// already on HTTPS, so it stops trying to redirect a call it cannot redirect.
/// </remarks>
public sealed class ForwardedCaller
{
    /// <summary>The browser's address, once known.</summary>
    public IPAddress? Address { get; private set; }

    /// <summary>Whether the browser reached this server over HTTPS.</summary>
    public bool IsHttps { get; private set; }

    /// <summary>Takes the caller from a request this server is handling. The
    /// address is already the browser's when a trusted proxy said so.</summary>
    /// <param name="context">The request.</param>
    public void CaptureFrom(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Address = context.Connection.RemoteIpAddress;
        IsHttps = context.Request.IsHttps;
    }

    /// <summary>Adds this caller's headers to an API request.</summary>
    /// <param name="request">The outgoing request.</param>
    public void ApplyTo(HttpRequestMessage request) => Apply(request, Address, IsHttps);

    /// <summary>Adds the forwarding headers to an API request. Nothing is added
    /// for an unknown address: the API then counts this server, as it always has.</summary>
    /// <param name="request">The outgoing request.</param>
    /// <param name="address">The browser's address, if known.</param>
    /// <param name="isHttps">Whether the browser used HTTPS.</param>
    public static void Apply(HttpRequestMessage request, IPAddress? address, bool isHttps)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (address is null)
        {
            return;
        }

        // A dual-stack socket reports an IPv4 browser as ::ffff:a.b.c.d; the API
        // should count it under the same key whichever way it arrived.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        request.Headers.Remove("X-Forwarded-For");
        request.Headers.Remove("X-Forwarded-Proto");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", address.ToString());
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", isHttps ? "https" : "http");
    }
}

/// <summary>
/// Gives a circuit its <see cref="ForwardedCaller"/>. A circuit's scope never
/// sees the page request, only the connection the circuit runs over, which
/// comes from the same browser.
/// </summary>
/// <remarks>
/// The HttpContext of an interactive circuit is documented as unreliable. Here
/// that costs little: when it is missing the address stays unknown, and the
/// API falls back to counting this server, as it did before.
/// </remarks>
/// <param name="caller">The circuit's caller.</param>
/// <param name="accessor">The connection's HTTP context, when visible.</param>
internal sealed class ForwardedCallerCircuitHandler(ForwardedCaller caller, IHttpContextAccessor accessor) : CircuitHandler
{
    /// <inheritdoc />
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Runs again on every reconnect, which may come from a new address.
        if (accessor.HttpContext is { Connection.RemoteIpAddress: not null } context)
        {
            caller.CaptureFrom(context);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Forwards the browser on calls made while handling one of its
/// requests, such as the offline PWA's gateway, where the request is always
/// at hand.</summary>
/// <param name="accessor">The request being handled.</param>
internal sealed class ForwardedCallerHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext is { } context)
        {
            ForwardedCaller.Apply(request, context.Connection.RemoteIpAddress, context.Request.IsHttps);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
