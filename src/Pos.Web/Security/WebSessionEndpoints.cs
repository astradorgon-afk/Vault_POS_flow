namespace Pos.Web.Security;

/// <summary>
/// The two HTTP calls a Blazor circuit cannot make for itself: setting the
/// session cookie after a sign-in, and clearing it on the way out.
/// </summary>
/// <remarks>
/// A circuit has no HTTP response to write a cookie onto. So the circuit
/// completes the sign-in, parks the result under a one-use token, and asks the
/// browser to post that token here, where there is a response.
/// </remarks>
public static class WebSessionEndpoints
{
    /// <summary>The cookie naming the server-side session.</summary>
    public const string CookieName = "vaultflow.session";

    /// <summary>Maps the session cookie endpoints.</summary>
    public static IEndpointRouteBuilder MapWebSessionEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/auth/session");

        group.MapPost(string.Empty, (
            HttpContext context,
            WebSessions sessions,
            OneUseTokens<PendingWebSignIn> pending,
            WebSessionRequest body) =>
        {
            if (!SameOrigin(context))
            {
                return Results.StatusCode(403);
            }

            if (pending.Take(body.Token) is not { } signIn)
            {
                return Results.Unauthorized();
            }

            // A browser that was already signed in here loses the old session
            // rather than leaving it resident until it expires.
            if (context.Request.Cookies.TryGetValue(CookieName, out string? previous))
            {
                sessions.Remove(previous);
            }

            string key = sessions.Add(signIn.Session);
            context.Response.Cookies.Append(CookieName, key, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
            });

            return Results.NoContent();
        });

        group.MapPost("/end", (HttpContext context, WebSessions sessions) =>
        {
            if (!SameOrigin(context))
            {
                return Results.StatusCode(403);
            }

            if (context.Request.Cookies.TryGetValue(CookieName, out string? key))
            {
                sessions.Remove(key);
            }

            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// Refuses a request another site made on the operator's behalf. The custom
    /// header cannot be set by a plain form post, which is what a cross-site
    /// attempt would have to use.
    /// </summary>
    private static bool SameOrigin(HttpContext context)
    {
        if (context.Request.Headers["X-VaultFlow-Session"] != "1")
        {
            return false;
        }

        string? origin = context.Request.Headers.Origin;
        return string.IsNullOrEmpty(origin)
            || string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The one-use token a circuit parked its completed sign-in under.</summary>
/// <param name="Token">The token issued by the circuit.</param>
public sealed record WebSessionRequest(string Token);
