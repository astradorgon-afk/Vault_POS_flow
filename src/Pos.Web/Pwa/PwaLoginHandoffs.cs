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

    public Entry? Take(string token)
    {
        if (!entries.TryRemove(token, out Entry? entry))
        {
            return null;
        }
        return entry.ExpiresAtUtc > DateTimeOffset.UtcNow ? entry : null;
    }

    public sealed record Entry(SignInResponse Credentials, Guid? WorkLocationId, DateTimeOffset ExpiresAtUtc);
}
