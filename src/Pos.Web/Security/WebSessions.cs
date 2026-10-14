using System.Collections.Concurrent;
using System.Security.Cryptography;
using Pos.Web.Services;

namespace Pos.Web.Security;

/// <summary>
/// One signed-in browser's API session, held on the server and found again
/// through an HttpOnly cookie.
/// </summary>
/// <remarks>
/// The access and refresh tokens never leave this process. The browser holds
/// only an opaque key, so a script on the page cannot read a credential even
/// if one got onto the page.
/// </remarks>
public sealed class WebSession
{
    /// <summary>The API credentials for this browser.</summary>
    public required SignInResponse Credentials { get; set; }

    /// <summary>The work location chosen at sign-in, if one was.</summary>
    public Guid? WorkLocationId { get; init; }

    /// <summary>The name shown for the chosen work location.</summary>
    public string? WorkLocationName { get; init; }

    /// <summary>The kind of the chosen work location (0 main warehouse, 1 store).</summary>
    public int? WorkLocationKind { get; init; }

    /// <summary>When this session stops being usable without signing in again.</summary>
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.AddHours(12);
}

/// <summary>
/// The signed-in web sessions this server is holding.
/// </summary>
/// <remarks>
/// This is what keeps a reload, a second tab and a navigation from signing the
/// operator out: the circuit is gone but the session is not. It does not
/// survive a restart of this process, so a deployment still signs everyone
/// out — making it durable is a separate decision, and the PWA gateway's
/// session store has the same limit.
/// </remarks>
public sealed class WebSessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, WebSession> sessions = new(StringComparer.Ordinal);

    /// <summary>Stores a session and returns the key the cookie carries.</summary>
    public string Add(WebSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[key] = session;
        return key;
    }

    /// <summary>Finds a live session and extends it, or returns null.</summary>
    public WebSession? Get(string? key)
    {
        if (string.IsNullOrEmpty(key) || !sessions.TryGetValue(key, out WebSession? session))
        {
            return null;
        }

        if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(key, out _);
            return null;
        }

        // Someone who is using the workspace should not be signed out mid-shift.
        session.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(Lifetime);
        return session;
    }

    /// <summary>Ends a session.</summary>
    public WebSession? Remove(string? key)
        => string.IsNullOrEmpty(key) ? null : sessions.TryRemove(key, out WebSession? session) ? session : null;

    /// <summary>Drops every expired session. Returns how many went.</summary>
    public int RemoveExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int removed = 0;
        foreach (KeyValuePair<string, WebSession> entry in sessions)
        {
            if (entry.Value.ExpiresAtUtc > now)
            {
                continue;
            }

            if (sessions.TryRemove(entry))
            {
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>
/// One-use, short-lived tokens that carry something across a boundary where a
/// secret cannot go: a circuit handing a fresh sign-in to the HTTP response
/// that must set the cookie, and a prerendered page handing its session to the
/// circuit that follows it.
/// </summary>
/// <typeparam name="T">What the token stands for.</typeparam>
public sealed class OneUseTokens<T>
    where T : class
{
    private readonly ConcurrentDictionary<string, (T Value, DateTimeOffset ExpiresAtUtc)> entries = new(StringComparer.Ordinal);

    /// <summary>Issues a token for a value, good once and briefly.</summary>
    public string Issue(T value, TimeSpan lifetime)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        entries[token] = (value, DateTimeOffset.UtcNow.Add(lifetime));
        return token;
    }

    /// <summary>Spends a token. A second attempt with the same token gets nothing.</summary>
    public T? Take(string? token)
    {
        if (string.IsNullOrEmpty(token) || !entries.TryRemove(token, out (T Value, DateTimeOffset ExpiresAtUtc) entry))
        {
            return null;
        }

        return entry.ExpiresAtUtc > DateTimeOffset.UtcNow ? entry.Value : null;
    }

    /// <summary>Drops every token that was issued and never spent. Returns how many went.</summary>
    public int RemoveExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int removed = 0;
        foreach (KeyValuePair<string, (T Value, DateTimeOffset ExpiresAtUtc)> entry in entries)
        {
            if (entry.Value.ExpiresAtUtc > now)
            {
                continue;
            }

            if (entries.TryRemove(entry))
            {
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>A sign-in a circuit has completed, waiting for the HTTP request that will set its cookie.</summary>
public sealed record PendingWebSignIn(WebSession Session);

/// <summary>A prerendered page's session, waiting for the circuit that follows it.</summary>
public sealed record WebSessionTicket(string SessionKey);
