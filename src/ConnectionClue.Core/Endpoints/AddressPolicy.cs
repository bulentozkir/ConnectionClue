using System.Net;

namespace ConnectionClue.Core;

public static class AddressPolicy
{
    // Loopback, private, CGNAT, link-local, documentation, benchmarking, multicast and reserved ranges.
    private static readonly IPNetwork[] NonPublic =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24",
            "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
            "::/128", "::1/128", "100::/64", "2001:db8::/32", "fc00::/7", "fe80::/10", "ff00::/8",
        }.Select(IPNetwork.Parse),
    ];

    /// <summary>External targets must resolve to public unicast addresses (DNS rebinding and reflection guard).</summary>
    public static bool IsPublicUnicast(IPAddress address)
    {
        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return !NonPublic.Any(n => n.Contains(a));
    }
}
