using Microsoft.AspNetCore.Components;
using Pos.Web.Services;

namespace Pos.Web.Security;

/// <summary>
/// Gives a new circuit back the session its browser already has.
/// </summary>
/// <remarks>
/// <para>
/// A page is rendered twice: once as HTML on the HTTP request, then again on
/// the circuit that takes over. Only the first of those can see cookies, and
/// the two do not share a service scope — which is why, before this existed,
/// reloading signed the operator out.
/// </para>
/// <para>
/// So the first render reads the cookie, restores the session, and leaves a
/// token in the page for the circuit to redeem. The token is good once and for
/// a few seconds, and stands for a session key rather than carrying one: a
/// script that reads it out of the page gets something already spent by the
/// time it could be used, and never a credential.
/// </para>
/// </remarks>
/// <param name="sessions">The signed-in sessions this server holds.</param>
/// <param name="tickets">The prerender-to-circuit handoff tokens.</param>
/// <param name="session">The circuit's session, to populate.</param>
/// <param name="state">The bridge between the two renders.</param>
/// <param name="accessor">The HTTP request, during the first render only.</param>
public sealed class WebSessionRestorer(
    WebSessions sessions,
    OneUseTokens<WebSessionTicket> tickets,
    UserSession session,
    PersistentComponentState state,
    IHttpContextAccessor accessor) : IDisposable
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);
    private PersistingComponentStateSubscription subscription;
    private string? sessionKey;
    private bool done;

    /// <summary>The key of the session this browser is using, once one is found.</summary>
    public string? SessionKey => sessionKey;

    /// <summary>
    /// Restores the session for this render, from the cookie on the first one
    /// and from the handed-over token on the second. Does nothing after the
    /// first call, or once a sign-in has already filled the session.
    /// </summary>
    public void Restore()
    {
        if (done || session.Current is not null)
        {
            done = true;
            return;
        }

        done = true;

        // Which render this is, decided by what is actually there rather than
        // by whether an HttpContext happens to be visible: inside a circuit
        // that accessor is documented as unreliable, and a stale one would
        // send this down the cookie path with no response to persist into.
        RestoreFromTicket();
        if (sessionKey is null && accessor.HttpContext is { } context)
        {
            RestoreFromCookie(context);
        }
    }

    /// <summary>Remembers the session a fresh sign-in created, so signing out can end it.</summary>
    /// <param name="key">The new session's key.</param>
    public void Adopt(string key) => sessionKey = key;

    /// <summary>Ends the stored session behind this browser, if there is one.</summary>
    public void Discard()
    {
        sessions.Remove(sessionKey);
        sessionKey = null;
    }

    private void RestoreFromCookie(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(WebSessionEndpoints.CookieName, out string? key) ||
            sessions.Get(key) is not { } stored)
        {
            return;
        }

        Attach(key, stored);

        // The circuit cannot read the cookie, so leave it a token instead.
        subscription = state.RegisterOnPersisting(() =>
        {
            state.PersistAsJson(nameof(WebSessionTicket), tickets.Issue(new WebSessionTicket(key), TicketLifetime));
            return Task.CompletedTask;
        });
    }

    private void RestoreFromTicket()
    {
        if (!state.TryTakeFromJson(nameof(WebSessionTicket), out string? token) ||
            tickets.Take(token) is not { } ticket ||
            sessions.Get(ticket.SessionKey) is not { } stored)
        {
            return;
        }

        Attach(ticket.SessionKey, stored);
    }

    /// <summary>
    /// Adopts a stored session and keeps it current. The write-through matters:
    /// refresh tokens are single-use, so a stored session still holding the one
    /// just spent would fail the next restore.
    /// </summary>
    private void Attach(string key, WebSession stored)
    {
        sessionKey = key;
        session.Restore(stored.Credentials, stored.WorkLocationId, stored.WorkLocationName, stored.WorkLocationKind);
        session.CredentialsRotated = rotated => stored.Credentials = rotated;
    }

    /// <inheritdoc />
    public void Dispose() => subscription.Dispose();
}
