using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.Presentation.Tests;

public sealed class DiagnosticsAnalysisTests
{
    private static TraceHop Hop(int n, string address, double? ms, double? increase, int replies = 3, NetworkToolStatus status = NetworkToolStatus.Responded) =>
        new(n, address, ms, replies, 3, increase, status);

    [Fact]
    public void Trace_names_the_hop_that_adds_lasting_delay()
    {
        var s = DiagnosticsAnalysis.Summarize([Hop(1, "192.168.1.1", 2, null), Hop(2, "100.64.0.1", 30, 28), Hop(3, "203.0.113.9", 32, 2),
            Hop(4, "1.1.1.1", 33, 1, status: NetworkToolStatus.Success)]);
        Assert.Equal((2, 28.0, "100.64.0.1", (int?)null), (s.DelayHop!.Value, s.DelayIncreaseMs!.Value, s.DelayAddress, s.LossFromHop));
        Assert.True(DiagnosticsAnalysis.IsPrivate("100.64.0.1"));
        Assert.False(DiagnosticsAnalysis.IsPrivate("203.0.113.9"));
    }

    [Fact]
    public void Slow_ping_replies_at_one_hop_and_silent_hops_are_not_blamed()
    {
        // Hop 2 answers pings slowly but forwards normally; hop 3 never answers pings.
        var s = DiagnosticsAnalysis.Summarize([Hop(1, "192.168.1.1", 2, null), Hop(2, "198.51.100.1", 80, 78), Hop(3, "198.51.100.2", null, null, 0,
            NetworkToolStatus.TimedOut), Hop(4, "1.1.1.1", 8, 0, status: NetworkToolStatus.Success)]);
        Assert.Null(s.DelayHop);
        Assert.Null(s.LossFromHop);
    }

    [Fact]
    public void A_slow_ping_hop_does_not_hide_a_lasting_increase_behind_it()
    {
        var s = DiagnosticsAnalysis.Summarize([Hop(1, "192.168.1.1", 2, null), Hop(2, "198.51.100.1", 60, 58), Hop(3, "198.51.100.2", 12, 0),
            Hop(4, "203.0.113.4", 45, 33), Hop(5, "1.1.1.1", 46, 1, status: NetworkToolStatus.Success)]);
        Assert.Equal((4, 33.0), (s.DelayHop!.Value, s.DelayIncreaseMs!.Value));
    }

    [Fact]
    public void Loss_counts_only_when_it_lasts_to_an_answering_destination()
    {
        var hops = new[] { Hop(1, "192.168.1.1", 2, null), Hop(2, "198.51.100.1", 10, 8, 2), Hop(3, "1.1.1.1", 12, 2, 1, NetworkToolStatus.Success) };
        Assert.Equal(2, DiagnosticsAnalysis.Summarize(hops).LossFromHop);
        Assert.Null(DiagnosticsAnalysis.Summarize([.. hops[..2], Hop(3, "1.1.1.1", null, null, 0, NetworkToolStatus.TimedOut)]).LossFromHop);
    }

    [Fact]
    public void Dns_switch_is_offered_only_for_a_clear_gain()
    {
        DnsResolverMeasurement Current(double ms) => new("", "192.168.1.1", ms, 3, NetworkToolStatus.Success, IsCurrent: true);
        var publics = new DnsResolverMeasurement[] { new("Cloudflare", "1.1.1.1", 12, 3, NetworkToolStatus.Success),
            new("Google", "8.8.8.8", 18, 3, NetworkToolStatus.Success), new("Quad9", "9.9.9.9", null, 0, NetworkToolStatus.TimedOut) };

        Assert.Equal("Cloudflare", DiagnosticsAnalysis.ChooseDns([Current(40), .. publics]).Fastest!.Provider);
        Assert.True(DiagnosticsAnalysis.ChooseDns([Current(14), .. publics]) is { Fastest: null, KeepCurrent: true }); // 2 ms is noise
        Assert.Equal("Cloudflare", DiagnosticsAnalysis.ChooseDns(publics).Fastest!.Provider); // current unknown
        // Already on Cloudflare: only another service can be suggested.
        var onCloudflare = publics.Select(p => p.Provider == "Cloudflare" ? p with { IsCurrent = true } : p).ToArray();
        Assert.True(DiagnosticsAnalysis.ChooseDns(onCloudflare) is { Fastest: null, KeepCurrent: true });
    }

    [Fact]
    public void Wifi_advice_names_the_connected_channel_a_quieter_one_and_faster_bands()
    {
        var advice = DiagnosticsAnalysis.AdviseWifi([new("2.4", 1, 0, true), new("2.4", 6, 7, false, Connected: true), new("2.4", 11, 3, false),
            new("5", 36, 2, false), new("5", 149, 0, true)]);
        Assert.Equal(6, advice.Connected!.Channel);
        Assert.Equal(1, advice.BetterChannel!.Channel);
        Assert.True(advice.FasterBandNearby);
        Assert.Equal([1, 149], advice.Best.Select(b => b.Channel));

        var fine = DiagnosticsAnalysis.AdviseWifi([new("5", 36, 1, true, Connected: true), new("5", 149, 1, false)]);
        Assert.True(fine is { BetterChannel: null, FasterBandNearby: false });
    }
}
