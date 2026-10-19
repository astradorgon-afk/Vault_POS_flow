using System.Net;

namespace Pos.Web.Security;

/// <summary>
/// The reverse proxies allowed to say who the browser is, from the
/// <c>ForwardedHeaders</c> configuration section.
/// </summary>
/// <remarks>
/// A container behind Caddy or a Cloudflare tunnel sees every request arrive
/// from its network's gateway, at an address Docker picks. Listing that network
/// here lets the X-Forwarded-For the proxy sets through, so the API can tell
/// visitors apart. Anything that can reach this server directly from a listed
/// network can claim any address, so list only what the proxy uses.
/// </remarks>
public static class TrustedProxies
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Adds the configured proxies and networks, in addition to the
    /// loopback ones the framework trusts already.</summary>
    /// <param name="options">The forwarded-headers options to extend.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <exception cref="InvalidOperationException">An entry is not an address or a network.</exception>
    public static void AddFrom(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SectionName);

        foreach (string entry in Entries(section, "KnownProxies"))
        {
            // A misread entry would quietly trust nothing, or the wrong thing;
            // refuse to start instead.
            if (!IPAddress.TryParse(entry, out IPAddress? address))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownProxies has '{entry}', which is not an IP address.");
            }

            options.KnownProxies.Add(address);
        }

        foreach (string entry in Entries(section, "KnownNetworks"))
        {
            if (!IPNetwork.TryParse(entry, out IPNetwork network))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownNetworks has '{entry}', which is not a network such as 172.16.0.0/12.");
            }

            options.KnownIPNetworks.Add(network);
        }
    }

    private static IEnumerable<string> Entries(IConfigurationSection section, string key)
        => section.GetSection(key).GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!);
}
