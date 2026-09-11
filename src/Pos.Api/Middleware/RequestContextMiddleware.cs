using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace Pos.Api.Middleware;

/// <summary>
/// Establishes the correlation identifier for a request and attaches it to
/// logging, the response, and the ambient request context.
/// </summary>
/// <remarks>
/// A correlation identifier is what lets an operator follow one POS sale from
/// the device's outbox, through the sync batch, into the ledger and the audit
/// log. Clients may supply one; anything malformed is replaced rather than
/// trusted, because it ends up in logs.
/// </remarks>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    /// <summary>The header carrying the correlation identifier.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>The header carrying the calling device's identifier.</summary>
    public const string DeviceHeader = "X-Device-Id";

    /// <summary>The key under which the correlation identifier is stored on the request.</summary>
    public const string CorrelationItemKey = "pos.correlationId";

    /// <summary>The key under which the device identifier is stored on the request.</summary>
    public const string DeviceItemKey = "pos.deviceId";

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task that completes when the pipeline completes.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Guid correlationId = ReadGuidHeader(context, CorrelationHeader) ?? Guid.CreateVersion7();
        Guid? deviceId = ReadGuidHeader(context, DeviceHeader);

        context.Items[CorrelationItemKey] = correlationId;

        if (deviceId is { } device)
        {
            context.Items[DeviceItemKey] = device;
        }

        context.Response.Headers[CorrelationHeader] = correlationId.ToString("D");

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("DeviceId", deviceId))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    private static Guid? ReadGuidHeader(HttpContext context, string header)
    {
        if (!context.Request.Headers.TryGetValue(header, out StringValues values))
        {
            return null;
        }

        string? raw = values.Count > 0 ? values[0] : null;

        return Guid.TryParse(raw, out Guid parsed) ? parsed : null;
    }
}

/// <summary>
/// Adds the response headers that harden the browser-facing surface.
/// </summary>
/// <remarks>
/// The reverse proxy sets these in production too. Setting them here as well
/// means a misconfigured proxy, or a developer running the API directly, still
/// gets the protections rather than silently losing them.
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>Runs the middleware.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task that completes when the pipeline completes.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IHeaderDictionary headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(self), geolocation=(), microphone=(), payment=()";

        // The API serves JSON only; no document is ever rendered from it.
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

        await next(context).ConfigureAwait(false);
    }
}
