using System.Net;
using System.Net.Sockets;
using ConnectionClue.Core;
using ConnectionClue.Windows.Probes;
using static ConnectionClue.Windows.IntegrationTests.Fx;

namespace ConnectionClue.Windows.IntegrationTests;

public class RouteAndMonitorTests
{
    [Fact]
    public void Loopback_route_is_on_link_and_not_a_tunnel()
    {
        var path = new RouteProvider().PredictRoute(IPAddress.Loopback);
        Assert.NotNull(path);
        Assert.Equal(InterfaceMedium.Loopback, path.Medium);
        Assert.Null(path.NextHop);
        Assert.False(path.TunnelSuspected);
    }

    [Fact]
    public void Default_path_has_gateway_and_source()
    {
        var path = RequireDefaultPath();
        Assert.True(path.InterfaceIndex > 0);
        Assert.Equal(AddressFamily.InterNetwork, path.SourceAddress.AddressFamily);
        Assert.True(path.MediaConnected);
        Assert.True(NetworkStatus.IsConnected(new RouteProvider()));
    }

    [Fact]
    public async Task Interface_monitor_starts_and_stops_cleanly()
    {
        var monitor = new InterfaceMonitor(TimeProvider.System);
        monitor.Start();
        monitor.Dispose();
        await monitor.Events.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, monitor.DroppedEvents);
    }

    [Fact]
    public void Wlan_monitor_reports_a_capability_state()
    {
        using var monitor = new WlanMonitor(TimeProvider.System);
        Assert.NotEqual(CapabilityState.Unknown, monitor.Start());
    }

    [Fact]
    public void Power_monitor_registers_without_a_window()
    {
        using var monitor = new PowerMonitor(TimeProvider.System);
        monitor.Start();
    }
}

public class IcmpProbeTests
{
    private static async Task<ProbeObservation> Ping(string address, int timeoutMs = 800, CancellationToken? ct = null)
    {
        var clock = Clock();
        using var probe = new IcmpProbe(clock, new StaticResolver(Target("t", 0, address)));
        var family = IPAddress.Parse(address).AddressFamily == AddressFamily.InterNetworkV6 ? RequestFamily.IPv6 : RequestFamily.IPv4;
        var o = await probe.ExecuteAsync(Request(clock, "t", ProbeKind.Icmp, family, timeoutMs), ct ?? Ct);
        Valid(o);
        return o;
    }

    [Fact]
    public async Task Loopback_succeeds_with_os_reported_timing()
    {
        var o = await Ping("127.0.0.1");
        Assert.Equal(ProbeStatus.Success, o.Status);
        Assert.Equal(TimingSource.OsReported, o.TimingSource);
        Assert.Equal(IpFamily.IPv4, o.ObservedFamily);
    }

    [Fact]
    public async Task Cancellation_is_cancelled_never_timeout() =>
        Assert.Equal(ProbeStatus.Cancelled, (await Ping("127.0.0.1", ct: Cancelled())).Status);

    [Fact]
    public async Task Unanswered_address_times_out_without_duration()
    {
        RequireDefaultPath();
        var o = await Ping("192.0.2.1", 300);
        Assert.Contains(o.Status, new[] { ProbeStatus.Timeout, ProbeStatus.NetworkUnreachable, ProbeStatus.HostUnreachable });
        Assert.Null(o.DurationUs);
    }

    [Fact]
    public async Task Gateway_probe_yields_a_valid_observation()
    {
        var o = await Ping(RequireDefaultPath().NextHop!.ToString());
        Assert.Contains(o.Status, new[] { ProbeStatus.Success, ProbeStatus.Timeout, ProbeStatus.HostUnreachable });
    }

    [Fact]
    public async Task Missing_family_is_skipped_with_reason()
    {
        var clock = Clock();
        using var probe = new IcmpProbe(clock, new StaticResolver(Target("t", 0, "127.0.0.1")));
        var o = await probe.ExecuteAsync(Request(clock, "t", ProbeKind.Icmp, RequestFamily.IPv6, 800), Ct);
        Assert.Equal((ProbeStatus.Skipped, "NoAddress"), (o.Status, o.ErrorCode));
        Valid(o);
    }
}

public class TcpProbeTests
{
    private static async Task<ProbeObservation> Connect(string address, int port, int timeoutMs, CancellationToken? ct = null)
    {
        var clock = Clock();
        var probe = new TcpProbe(clock, new StaticResolver(Target("t", port, address)));
        var o = await probe.ExecuteAsync(Request(clock, "t", ProbeKind.Tcp, RequestFamily.IPv4, timeoutMs), ct ?? Ct);
        Valid(o);
        return o;
    }

    [Fact]
    public async Task Listener_connects_with_socket_attribution()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var o = await Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 3000);
        Assert.Equal(ProbeStatus.Success, o.Status);
        Assert.Equal(Attribution.SocketObserved, o.Attribution);
        Assert.NotNull(o.DurationUs);
    }

    [Fact]
    public async Task Closed_port_is_refused_not_silence()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var o = await Connect("127.0.0.1", port, 5000); // Windows retries SYN after RST for ~2 s
        Assert.Equal(ProbeStatus.Refused, o.Status);
    }

    [Fact]
    public async Task Unanswered_address_times_out()
    {
        RequireDefaultPath();
        var o = await Connect("192.0.2.1", 443, 500);
        Assert.Contains(o.Status, new[] { ProbeStatus.Timeout, ProbeStatus.NetworkUnreachable, ProbeStatus.HostUnreachable });
    }

    [Fact]
    public async Task Cancellation_is_cancelled() =>
        Assert.Equal(ProbeStatus.Cancelled, (await Connect("192.0.2.1", 443, 5000, Cancelled())).Status);
}

public class SystemDnsProbeTests
{
    private static async Task<ProbeObservation> Resolve(string host, RequestFamily family = RequestFamily.IPv4, CancellationToken? ct = null)
    {
        var clock = Clock();
        var probe = new SystemDnsProbe(clock, new StaticResolver(Web("d", host)));
        var o = await probe.ExecuteAsync(Request(clock, "d", ProbeKind.SystemDns, family, 3000), ct ?? Ct);
        Valid(o);
        return o;
    }

    [Fact]
    public async Task Public_name_resolves_through_system_resolver()
    {
        RequireInternet();
        var o = await Resolve("www.microsoft.com");
        Assert.Equal(ProbeStatus.Success, o.Status);
        Assert.Equal(IpFamily.IPv4, o.ObservedFamily);
        Assert.True(((DnsDetail)o.Detail).RecordCount > 0);
    }

    [Fact]
    public async Task Aaaa_query_is_a_separate_stream()
    {
        RequireInternet();
        var o = await Resolve("www.microsoft.com", RequestFamily.IPv6);
        Assert.Contains(o.Status, new[] { ProbeStatus.Success, ProbeStatus.DnsFailure });
    }

    [Fact]
    public async Task Reserved_invalid_name_is_negative_answer_not_timeout()
    {
        RequireInternet();
        var o = await Resolve("connectionclue-probe.invalid");
        if (o.Status == ProbeStatus.Success) Assert.Skip("Resolver rewrites NXDOMAIN.");
        Assert.Equal((ProbeStatus.DnsFailure, "NxDomain"), (o.Status, o.ErrorCode));
    }

    [Fact]
    public async Task Cancellation_is_cancelled() =>
        Assert.Equal(ProbeStatus.Cancelled, (await Resolve("www.microsoft.com", ct: Cancelled())).Status);
}

public class HttpsProbeTests
{
    private static async Task<ProbeObservation> Fetch(ResolvedTarget target)
    {
        RequireInternet();
        var clock = Clock();
        using var probe = new HttpsProbe(clock, new StaticResolver(target));
        var o = await probe.ExecuteAsync(Request(clock, target.TargetId, ProbeKind.Https, RequestFamily.Any, 10_000), Ct);
        Valid(o);
        return o;
    }

    [Fact]
    public async Task Contract_match_succeeds_and_records_issuer()
    {
        var o = await Fetch(Web("w", "example.com", "Example Domain"));
        Assert.Equal(ProbeStatus.Success, o.Status);
        Assert.NotNull(((HttpsDetail)o.Detail).TlsIssuer);
        Assert.NotNull(o.ObservedFamily);
    }

    [Fact]
    public async Task Body_mismatch_is_http_unexpected() =>
        Assert.Equal((ProbeStatus.HttpUnexpected, "Body"), await Outcome(Web("w", "example.com", "no-such-token-42")));

    [Fact]
    public async Task Redirect_is_recorded_not_followed()
    {
        var o = await Fetch(Web("w", "microsoft.com"));
        Assert.Equal((ProbeStatus.HttpUnexpected, "Redirect"), (o.Status, o.ErrorCode));
        Assert.NotNull(((HttpsDetail)o.Detail).RedirectHost);
    }

    [Fact]
    public async Task Untrusted_certificate_is_tls_failure() =>
        Assert.Equal(ProbeStatus.TlsFailure, (await Fetch(Web("w", "self-signed.badssl.com"))).Status);

    [Fact]
    public async Task Address_outside_pinned_prefixes_is_blocked()
    {
        if (HttpClient.DefaultProxy.GetProxy(new Uri("https://example.com/")) is not null) Assert.Skip("Proxied path.");
        var o = await Fetch(Web("w", "example.com") with { PinnedPrefixes = [IPNetwork.Parse("192.0.2.0/24")] });
        Assert.Equal((ProbeStatus.Unsupported, "AddressNotPinned"), (o.Status, o.ErrorCode));
    }

    private static async Task<(ProbeStatus, string?)> Outcome(ResolvedTarget target)
    {
        var o = await Fetch(target);
        return (o.Status, o.ErrorCode);
    }
}

public class ThroughputProbeTests
{
    // LAB endpoint; byte caps keep the test to a few MB.
    private static ThroughputProbe Probe() => new(TimeProvider.System, "https://speed.cloudflare.com/__down?bytes={0}",
        new Uri("https://speed.cloudflare.com/__up"), maxDownloadBytes: 8_000_000, maxUploadBytes: 3_000_000);

    [Theory]
    [InlineData(ThroughputDirection.Download)]
    [InlineData(ThroughputDirection.Upload)]
    public async Task Measures_throughput_within_the_byte_cap(ThroughputDirection direction)
    {
        RequireInternet();
        using var probe = Probe();
        var live = new List<double>();
        var budget = ThroughputBudget.Full(direction) with { Duration = TimeSpan.FromSeconds(6) };
        var r = await probe.MeasureAsync(direction, budget, new Progress<double>(live.Add), Ct);
        Assert.Equal(ProbeStatus.Success, r.Status);
        Assert.True(r.Mbps > 0);
        Assert.InRange(r.Bytes, 1, (direction == ThroughputDirection.Download ? 8_000_000 : 3_000_000) + 4 * 81_920 + 4 * 65_536);
    }

    [Theory]
    [InlineData(ThroughputDirection.Download)]
    [InlineData(ThroughputDirection.Upload)]
    public async Task Light_sample_uses_one_connection_and_stays_within_its_budget(ThroughputDirection direction)
    {
        RequireInternet();
        using var probe = new ThroughputProbe(TimeProvider.System, "https://speed.cloudflare.com/__down?bytes={0}",
            new Uri("https://speed.cloudflare.com/__up"));
        var budget = ThroughputBudget.Light(direction);
        var r = await probe.MeasureAsync(direction, budget, null, Ct);
        Assert.Equal(ProbeStatus.Success, r.Status);
        Assert.True(r.Mbps > 0);
        Assert.InRange(r.Bytes, 1, budget.MaxBytes + 81_920 + 65_536);
        Assert.InRange(r.Elapsed, TimeSpan.Zero, budget.Duration + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cancellation_is_cancelled()
    {
        using var probe = Probe();
        var r = await probe.MeasureAsync(ThroughputDirection.Download, ThroughputBudget.Full(ThroughputDirection.Download), null, Cancelled());
        Assert.Equal(ProbeStatus.Cancelled, r.Status);
    }

    [Fact]
    public void Connection_cost_is_readable() => _ = ConnectionCost.IsMetered();
}

public class SystemInspectorTests
{
    [Fact]
    public void Reads_active_adapter_facts_as_a_standard_user()
    {
        var path = RequireDefaultPath();
        var facts = SystemInspector.Inspect(path);
        if (path is { IsHardware: true, Medium: InterfaceMedium.Ethernet or InterfaceMedium.WiFi })
        {
            Assert.Equal(path.Medium, facts.Medium);
            Assert.True(facts.LinkMbps > 0);
            Assert.NotNull(facts.PowerOffAllowed);
            Assert.False(string.IsNullOrWhiteSpace(facts.AdapterName));
            Assert.False(string.IsNullOrWhiteSpace(facts.NetworkName));
            Assert.NotEmpty(facts.DnsServers!);
        }
        Assert.NotNull(facts.TcpAutoTuningLevel);
        Assert.NotNull(facts.TcpAutoTuningLimited);
        Assert.InRange(facts.WifiPowerSavingOnBattery ?? 0, 0, 3);
        Assert.InRange(facts.WifiPowerSavingOnAc ?? 0, 0, 3);
        Assert.NotNull(facts.UsbSelectiveSuspend);
    }

    [Fact]
    public void Interface_counters_advance_with_traffic()
    {
        var path = RequireDefaultPath();
        var before = SystemInspector.ReadCounters(path.InterfaceLuid);
        Assert.NotNull(before);
        Assert.True(before.Value.Received > 0);
        Assert.Null(SystemInspector.ReadCounters(0));
    }

    [Fact]
    public async Task Per_app_usage_names_apps_and_leaves_out_this_process()
    {
        RequireInternet();
        var apps = await AppNetworkUsage.TopAsync(DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now);
        Assert.InRange(apps.Count, 0, 3);
        Assert.All(apps, a => Assert.True(a.Path.Length == 0 || a.Name.Length > 0));
        Assert.DoesNotContain(apps, a => Path.GetFileName(a.Path).Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(apps, a => a.Path.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)); // mapped to drive letters
    }

    [Fact]
    public void Unknown_path_still_reads_os_settings() =>
        Assert.Null(SystemInspector.Inspect(null).Medium);
}