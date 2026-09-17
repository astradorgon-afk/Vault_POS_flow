using Pos.Infrastructure.Offline;
using Pos.Shared.Devices;

namespace Pos.Client.Storage;

/// <summary>
/// Answers whether head office is reachable from the platform's own view of the
/// network.
/// </summary>
/// <remarks>
/// It reports the link, not the server: only <see cref="NetworkAccess.Internet"/>
/// counts as online, so a device on a shop's Wi-Fi with no route out reads as
/// offline rather than claiming a connection it does not have. A reachable link
/// with an unreachable server still looks online here; the sync client is what
/// finds that out, and its failures are what the banner will read once Phase 13
/// lands.
/// </remarks>
public sealed class NetworkConnectivityProbe : IDeviceConnectivityProbe
{
    /// <inheritdoc />
    public DeviceConnectivityState Current
        => Connectivity.Current.NetworkAccess == NetworkAccess.Internet
            ? DeviceConnectivityState.Online
            : DeviceConnectivityState.Offline;
}
