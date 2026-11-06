using Pos.Client.Services;
using Pos.Infrastructure.Offline;
using Pos.Shared.Devices;

namespace Pos.Client.Storage;

/// <summary>
/// Answers whether head office is reachable, from the platform's view of the
/// network and from what the last head-office call found.
/// </summary>
/// <remarks>
/// Only <see cref="NetworkAccess.Internet"/> counts as a link, so a device on a
/// shop's Wi-Fi with no route out reads as offline rather than claiming a
/// connection it does not have. A working link with an unreachable or failing
/// server also reads as offline: the register finds that out on its next call
/// and says so, instead of offering online-only work that would fail.
/// </remarks>
/// <param name="reachability">What the last head-office call found.</param>
public sealed class NetworkConnectivityProbe(HeadOfficeReachability reachability) : IDeviceConnectivityProbe
{
    /// <inheritdoc />
    public DeviceConnectivityState Current
        => Connectivity.Current.NetworkAccess == NetworkAccess.Internet && !reachability.LastCallFailed
            ? DeviceConnectivityState.Online
            : DeviceConnectivityState.Offline;
}
