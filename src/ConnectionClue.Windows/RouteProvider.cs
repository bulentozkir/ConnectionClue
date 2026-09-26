using System.Net;
using ConnectionClue.Core;
using ConnectionClue.Windows.Interop;

namespace ConnectionClue.Windows;

/// <summary>GetBestRoute2-based path prediction. It is a prediction, not packet-path proof.</summary>
public sealed unsafe class RouteProvider : INetworkContextProvider
{
    // Documentation prefixes follow the default route; GetBestRoute2 sends no packets.
    private static readonly IPAddress DefaultRouteV4 = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress DefaultRouteV6 = IPAddress.Parse("2001:db8::1");

    public PathContext? GetDefaultPath(IpFamily family) =>
        PredictRoute(family == IpFamily.IPv4 ? DefaultRouteV4 : DefaultRouteV6);

    public PathContext? PredictRoute(IPAddress destination)
    {
        var dest = SockAddr.From(destination);
        MIB_IPFORWARD_ROW2 route;
        SOCKADDR_INET source;
        if (PInvoke.GetBestRoute2(null, 0, null, &dest, 0, &route, &source) != WIN32_ERROR.NO_ERROR) return null;
        if (SockAddr.ToAddress(source) is not { } src) return null;

        var hop = SockAddr.ToAddress(route.NextHop);
        if (hop is not null && (hop.Equals(IPAddress.Any) || hop.Equals(IPAddress.IPv6Any))) hop = null;

        var row = new MIB_IF_ROW2 { InterfaceLuid = route.InterfaceLuid };
        var (medium, hardware, connected) = PInvoke.GetIfEntry2(&row) == WIN32_ERROR.NO_ERROR
            ? Describe(in row)
            : (InterfaceMedium.Other, false, false);

        // Conservative: virtual adapters (VPN, some virtual switches) suppress Wi-Fi-vs-upstream localization.
        bool tunnel = medium != InterfaceMedium.Loopback
            && (!hardware || medium is InterfaceMedium.Tunnel or InterfaceMedium.Ppp);

        return new PathContext(IpFamilies.Of(destination), route.InterfaceLuid.Value, route.InterfaceIndex, hop, src,
            medium, hardware, tunnel, connected);
    }

    internal static (InterfaceMedium Medium, bool Hardware, bool Connected) Describe(in MIB_IF_ROW2 row)
    {
        var medium = row.Type switch
        {
            6 => InterfaceMedium.Ethernet,      // IF_TYPE_ETHERNET_CSMACD
            71 => InterfaceMedium.WiFi,         // IF_TYPE_IEEE80211
            131 or 53 => InterfaceMedium.Tunnel, // IF_TYPE_TUNNEL, IF_TYPE_PROP_VIRTUAL
            23 => InterfaceMedium.Ppp,
            24 => InterfaceMedium.Loopback,
            _ => InterfaceMedium.Other,
        };
        bool hardware = (row.InterfaceAndOperStatusFlags._bitfield & 0x1) != 0;
        bool connected = (int)row.OperStatus == 1 && (int)row.MediaConnectState == 1;
        return (medium, hardware, connected);
    }
}
