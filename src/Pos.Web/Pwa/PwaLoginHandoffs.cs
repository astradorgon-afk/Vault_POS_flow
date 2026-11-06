using System.Collections.Concurrent;
using System.Security.Cryptography;
using Pos.Web.Services;

namespace Pos.Web.Pwa;

/// <summary>Short-lived, one-use bridge from a successful server login to PWA preparation.</summary>
public sealed class PwaLoginHandoffs
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public string Issue(SignInResponse credentials, Guid? workLocationId)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        entries[token] = new(credentials, workLocationId, DateTimeOffset.UtcNow.AddMinutes(2));
        return token;
    }

    public Entry? Take(string? token)
    {
        if (string.IsNullOrEmpty(token) || !entries.TryRemove(token, out Entry? entry))
        {
            return null;
        }
        return entry.ExpiresAtUtc > DateTimeOffset.UtcNow ? entry : null;
    }

    /// <summary>Drops every handoff that was issued and never redeemed. Returns how many went.</summary>
    /// <remarks>
    /// <see cref="Take"/> is the only other way out of this dictionary, so a
    /// token the browser never came back for would otherwise hold a full set
    /// of credentials — access and refresh token — for the life of the process,
    /// long past the two minutes it was valid for.
    /// </remarks>
    public int RemoveExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int removed = 0;
        foreach (KeyValuePair<string, Entry> entry in entries)
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

    public sealed record Entry(SignInResponse Credentials, Guid? WorkLocationId, DateTimeOffset ExpiresAtUtc);
}
