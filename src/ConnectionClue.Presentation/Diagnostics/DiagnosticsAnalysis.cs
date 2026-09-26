using System.Net;
using System.Net.Sockets;

namespace ConnectionClue.Presentation.Diagnostics;

/// <summary>DelayHop adds the most delay (null: none notable); LossFromHop starts loss that lasts to the destination.</summary>
public sealed record TraceSummary(int? DelayHop, double? DelayIncreaseMs, string? DelayAddress, int? LossFromHop);

/// <summary>Fastest is worth switching to; KeepCurrent means this PC's own resolver is as fast, within the margin.</summary>
public sealed record DnsChoice(DnsResolverMeasurement? Fastest, bool KeepCurrent);

/// <summary>Least crowded channel per band, and whether a faster band than the connected one is in use nearby.</summary>
public sealed record WifiAdvice(WifiChannelSummary? Connected, IReadOnlyList<WifiChannelSummary> Best, bool FasterBandNearby, WifiChannelSummary? BetterChannel);

public static class DiagnosticsAnalysis
{
    /// <summary>Hop-to-hop increases below this are ordinary variation.</summary>
    public const double NotableIncreaseMs = 10;

    /// <summary>Switching DNS pays off only when the public service is clearly faster: 5 ms and 20 % at least.</summary>
    public const double MinDnsGainMs = 5, MinDnsGainRatio = 0.2;

    /// <summary>
    /// Forwarding delay can only grow along a route, so a hop's path delay is at most the fastest reply from it or any later
    /// hop. That envelope ignores routers that answer pings slowly while forwarding normally; the hop where it rises most
    /// adds the lasting delay. Loss counts only when it continues to a destination that answered.
    /// </summary>
    public static TraceSummary Summarize(IReadOnlyList<TraceHop> hops)
    {
        var answered = hops.Where(h => h.MedianRoundTripMilliseconds is not null).ToList();
        var envelope = new double[answered.Count];
        for (int i = answered.Count - 1; i >= 0; i--)
            envelope[i] = Math.Min(answered[i].MedianRoundTripMilliseconds!.Value, i + 1 < answered.Count ? envelope[i + 1] : double.MaxValue);
        TraceHop? worst = null;
        double largest = 0;
        for (int i = 1; i < answered.Count; i++)
        {
            double increase = envelope[i] - envelope[i - 1];
            if (increase >= NotableIncreaseMs && increase > largest) (worst, largest) = (answered[i], increase);
        }

        int? lossFrom = null;
        if (hops.Count > 0 && hops[^1] is { Status: NetworkToolStatus.Success } destination && destination.Replies < destination.Probes)
            for (int i = hops.Count - 1; i >= 0 && hops[i].Replies < hops[i].Probes; i--) lossFrom = hops[i].Hop;
        return new(worst?.Hop, worst is null ? null : largest, worst?.Address, lossFrom);
    }

    /// <summary>Private (RFC 1918) and carrier-grade NAT addresses: a home network or a provider's access network.</summary>
    public static bool IsPrivate(string? address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    public static DnsChoice ChooseDns(IReadOnlyList<DnsResolverMeasurement> results)
    {
        var answered = results.Where(r => r.Status == NetworkToolStatus.Success && r.MedianMilliseconds is not null).ToList();
        var fastest = answered.Where(r => !r.IsCurrent && r.Provider.Length > 0).MinBy(r => r.MedianMilliseconds);
        var current = answered.FirstOrDefault(r => r.IsCurrent);
        if (fastest is null || current?.MedianMilliseconds is not { } now) return new(fastest, false);
        double gain = now - fastest.MedianMilliseconds!.Value;
        return gain >= MinDnsGainMs && gain >= MinDnsGainRatio * now ? new(fastest, false) : new(null, true);
    }

    public static WifiAdvice AdviseWifi(IReadOnlyList<WifiChannelSummary> channels)
    {
        var connected = channels.FirstOrDefault(c => c.Connected);
        var best = channels.Where(c => c.Recommended).OrderBy(c => BandOrder(c.Band)).ToList();
        bool fasterBand = connected is not null && channels.Any(c => BandOrder(c.Band) > BandOrder(connected.Band));
        var better = connected is null ? null
            : best.FirstOrDefault(b => b.Band == connected.Band && b.Channel != connected.Channel && b.NearbyNetworks + 1 < connected.NearbyNetworks);
        return new(connected, best, fasterBand, better);
    }

    private static int BandOrder(string band) => band switch { "2.4" => 0, "5" => 1, "6" => 2, _ => -1 };
}
