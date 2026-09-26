using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionClue.Core;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Core.Tests;

public class ProbeClassificationTests
{
    [Theory]
    [InlineData(IPStatus.Success, ProbeStatus.Success, null)]
    [InlineData(IPStatus.TimedOut, ProbeStatus.Timeout, null)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeStatus.HostUnreachable, null)]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeStatus.NetworkUnreachable, null)]
    [InlineData(IPStatus.DestinationProhibited, ProbeStatus.HostUnreachable, "Prohibited")]
    [InlineData(IPStatus.TtlExpired, ProbeStatus.NetworkUnreachable, "TtlExpired")]
    [InlineData(IPStatus.NoResources, ProbeStatus.InternalError, "NoResources")]
    public void Icmp(IPStatus input, ProbeStatus status, string? code) =>
        Assert.Equal((status, code), ProbeClassification.FromIpStatus(input));

    [Theory]
    [InlineData(SocketError.ConnectionRefused, ProbeStatus.Refused, null)]
    [InlineData(SocketError.TimedOut, ProbeStatus.Timeout, null)]
    [InlineData(SocketError.NetworkUnreachable, ProbeStatus.NetworkUnreachable, null)]
    [InlineData(SocketError.AccessDenied, ProbeStatus.PermissionDenied, "AccessDenied")]
    [InlineData(SocketError.HostNotFound, ProbeStatus.DnsFailure, "NxDomain")]
    public void Socket(SocketError input, ProbeStatus status, string? code) =>
        Assert.Equal((status, code), ProbeClassification.FromSocketError(input));

    [Theory]
    [InlineData(0, 2, ProbeStatus.Success, null)]
    [InlineData(0, 0, ProbeStatus.DnsFailure, "NoData")]
    [InlineData(9501, 0, ProbeStatus.DnsFailure, "NoData")]
    [InlineData(9003, 0, ProbeStatus.DnsFailure, "NxDomain")]
    [InlineData(9002, 0, ProbeStatus.DnsFailure, "ServFail")]
    [InlineData(9852, 0, ProbeStatus.DnsFailure, "NoServers")]
    [InlineData(1460, 0, ProbeStatus.Timeout, null)]
    [InlineData(1223, 0, ProbeStatus.Cancelled, null)]
    [InlineData(1234, 0, ProbeStatus.DnsFailure, "Win32_1234")]
    public void Dns(int win32, int records, ProbeStatus status, string? code) =>
        Assert.Equal((status, code), ProbeClassification.FromDns(win32, records));

    [Theory]
    [InlineData(200, false, true, ProbeStatus.Success, null)]
    [InlineData(407, false, true, ProbeStatus.ProxyAuthRequired, null)]
    [InlineData(429, false, true, ProbeStatus.RateLimited, null)]
    [InlineData(301, false, true, ProbeStatus.HttpUnexpected, "Redirect")]
    [InlineData(500, false, true, ProbeStatus.HttpUnexpected, "Status")]
    [InlineData(200, true, true, ProbeStatus.HttpUnexpected, "Oversize")]
    [InlineData(200, false, false, ProbeStatus.HttpUnexpected, "Body")]
    public void Https(int code, bool oversize, bool matched, ProbeStatus status, string? error) =>
        Assert.Equal((status, error), ProbeClassification.FromHttp(code, 200, oversize, matched));
}

public class ObservationInvariantsTests
{
    private static ProbeRequest Req(ProbeKind kind, RequestFamily family) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, new StreamKey("t", kind, family), 10, TimeSpan.FromSeconds(1));

    private static ProbeObservation Obs(ProbeKind kind, RequestFamily family, ProbeStatus status, long? duration,
        Attribution attribution = Attribution.RoutePredicted, long? started = 10) =>
        new(Req(kind, family), status, attribution, null, started, started is null ? null : 20, duration,
            duration is null ? null : TimingSource.OsReported, status == ProbeStatus.Skipped ? "Reason" : null, new EmptyDetail());

    [Fact]
    public void Valid_success_has_no_violations() =>
        Assert.Empty(ObservationInvariants.Violations(Obs(ProbeKind.Icmp, RequestFamily.IPv4, ProbeStatus.Success, 5000)));

    [Fact]
    public void Timeout_with_duration_is_rejected() =>
        Assert.Contains("Timeout with duration",
            ObservationInvariants.Violations(Obs(ProbeKind.Icmp, RequestFamily.IPv4, ProbeStatus.Timeout, 800_000)));

    [Fact]
    public void Https_requires_any_family() =>
        Assert.Contains("Https <=> Any family",
            ObservationInvariants.Violations(Obs(ProbeKind.Https, RequestFamily.IPv4, ProbeStatus.Success, 1, Attribution.SocketObserved)));

    [Fact]
    public void Skipped_must_not_start() =>
        Assert.Contains("Skipped needs reason and no start",
            ObservationInvariants.Violations(Obs(ProbeKind.Tcp, RequestFamily.IPv4, ProbeStatus.Skipped, null)));

    [Fact]
    public void Proxied_only_for_https() =>
        Assert.Contains("Proxied outside Https",
            ObservationInvariants.Violations(Obs(ProbeKind.Tcp, RequestFamily.IPv4, ProbeStatus.Success, 1, Attribution.Proxied)));

    [Fact]
    public void Skipped_factory_is_valid() =>
        Assert.Empty(ObservationInvariants.Violations(Observations.Skipped(Req(ProbeKind.Icmp, RequestFamily.IPv6), "NoAddress", Attribution.RoutePredicted)));
}

public class AddressPolicyTests
{
    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Classifies(string address, bool isPublic) =>
        Assert.Equal(isPublic, AddressPolicy.IsPublicUnicast(IPAddress.Parse(address)));
}

public class ResolvedTargetTests
{
    private static readonly ResolvedTarget Target = new("a", "op", "probe.example", 443, "/",
        [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::7")],
        PinnedPrefixes: [IPNetwork.Parse("203.0.113.0/24")], ExpectedTlsIssuers: ["CN=Issuer"]);

    [Fact]
    public void Selects_address_by_family()
    {
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Target.AddressFor(RequestFamily.IPv4));
        Assert.Equal(IPAddress.Parse("2001:db8::7"), Target.AddressFor(RequestFamily.IPv6));
    }

    [Fact]
    public void Enforces_pinned_prefixes()
    {
        Assert.True(Target.IsPinned(IPAddress.Parse("203.0.113.200")));
        Assert.True(Target.IsPinned(IPAddress.Parse("::ffff:203.0.113.9")));
        Assert.False(Target.IsPinned(IPAddress.Parse("198.51.100.1")));
        Assert.True(Target with { PinnedPrefixes = null } is { } open && open.IsPinned(IPAddress.Loopback));
    }

    [Fact]
    public void Checks_expected_issuer()
    {
        Assert.True(Target.IsExpectedIssuer("cn=issuer"));
        Assert.False(Target.IsExpectedIssuer("CN=Inspector"));
        Assert.Null((Target with { ExpectedTlsIssuers = null }).IsExpectedIssuer("CN=Any"));
    }
}

public class SessionClockTests
{
    [Fact]
    public void Offsets_are_monotonic_microseconds()
    {
        var time = new FakeTimeProvider();
        var clock = new SessionClock(time);
        Assert.Equal(0, clock.NowUs);
        time.Advance(TimeSpan.FromMilliseconds(1500.25));
        Assert.Equal(1_500_250, clock.NowUs);
    }

    [Fact]
    public void Wall_clock_changes_do_not_move_offsets()
    {
        var time = new ManualTimeProvider();
        var clock = new SessionClock(time);
        time.Timestamp += time.TimestampFrequency;
        time.UtcNow = time.UtcNow.AddHours(-3);
        Assert.Equal(1_000_000, clock.NowUs);
    }

    /// <summary>FakeTimeProvider couples UTC and timestamps; this one lets the wall clock jump independently.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public long Timestamp { get; set; } = 1_000;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

public class ThroughputMathTests
{
    [Fact]
    public void Excludes_the_first_second_ramp_up() =>
        Assert.Equal(200, ThroughputMath.Mbps([(0, 0), (1, 1_000_000), (2, 26_000_000), (3, 51_000_000), (4, 76_000_000)])!.Value, 3);

    [Fact]
    public void Short_capped_transfer_excludes_its_first_quarter() =>
        Assert.Equal(800, ThroughputMath.Mbps([(0, 0), (0.1, 0), (0.2, 10_000_000), (0.4, 30_000_000)])!.Value, 3);

    [Fact]
    public void Nothing_transferred_has_no_result()
    {
        Assert.Null(ThroughputMath.Mbps([(0, 0), (1, 0)]));
        Assert.Null(ThroughputMath.Mbps([(0, 0)]));
    }
}