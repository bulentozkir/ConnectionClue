using ConnectionClue.Core;

namespace ConnectionClue.Windows;

/// <summary>
/// Whether this PC has any network at all. Only "no route anywhere" (Wi-Fi off, cable unplugged, airplane mode) counts
/// as disconnected. Wi-Fi or a cable without internet still counts as connected, because a check then shows where the
/// path breaks. GetBestRoute2 sends no packets, so this is cheap enough to call on every network change.
/// </summary>
public static class NetworkStatus
{
    public static bool IsConnected(INetworkContextProvider routes) =>
        routes.GetDefaultPath(IpFamily.IPv4) is { MediaConnected: true } || routes.GetDefaultPath(IpFamily.IPv6) is { MediaConnected: true };
}
