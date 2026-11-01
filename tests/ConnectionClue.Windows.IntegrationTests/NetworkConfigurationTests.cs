using ConnectionClue.Core;
using System.Net;
using System.Net.NetworkInformation;

namespace ConnectionClue.Windows.IntegrationTests;

[Collection("SystemInspector")]
public class NetworkConfigurationTests
{
    [Fact]
    public void Dhcp_fallback_requires_no_usable_ipv4_or_ipv6_and_unknown_is_not_healthy()
    {
        static (IPAddress, DuplicateAddressDetectionState) Address(string value,
            DuplicateAddressDetectionState state = DuplicateAddressDetectionState.Preferred) => (IPAddress.Parse(value), state);
        Assert.True(NetworkConfigurationReader.HasOnlyLinkLocalAddresses([Address("169.254.1.2"), Address("fe80::1")]));
        Assert.False(NetworkConfigurationReader.HasOnlyLinkLocalAddresses([Address("169.254.1.2"), Address("2001:db8::1")]));
        Assert.False(NetworkConfigurationReader.HasOnlyLinkLocalAddresses([Address("192.168.1.2"), Address("fe80::1")]));
        Assert.True(NetworkConfigurationReader.HasOnlyLinkLocalAddresses(
            [Address("169.254.1.2"), Address("2001:db8::1", DuplicateAddressDetectionState.Tentative)]));
        Assert.Null(NetworkConfigurationReader.HasOnlyLinkLocalAddresses([]));
        Assert.Null(NetworkConfigurationReader.HasOnlyLinkLocalAddresses([Address("fe80::1")]));
        Assert.Null(NetworkConfigurationReader.HasOnlyLinkLocalAddresses([Address("0.0.0.0"), Address("::"), Address("127.0.0.1")]));
    }

    [Fact]
    public void Every_readable_property_is_retained_and_adapter_queries_are_scoped()
    {
        var queries = new List<string>();
        IReadOnlyList<IReadOnlyDictionary<string, string>> Query(string query, TimeSpan remaining)
        {
            Assert.True(remaining > TimeSpan.Zero);
            queries.Add(query);
            return [new Dictionary<string, string> { ["UnrecognisedVendorProperty"] = "preserved" }];
        }
        var result = NetworkConfigurationReader.ReadSources(17, @"Vendor's\Adapter", Query);
        Assert.Equal(15, result.Sources.Count);
        Assert.Empty(result.Unavailable);
        Assert.All(result.Sources, s => Assert.Equal("preserved", s.Rows[0]["UnrecognisedVendorProperty"]));
        Assert.Contains(queries, q => q == "SELECT * FROM MSFT_NetIPInterface WHERE InterfaceIndex = 17");
        Assert.Contains(queries, q => q == @"SELECT * FROM MSFT_NetAdapterRssSettingData WHERE InterfaceDescription = 'Vendor\'s\\Adapter'");
    }

    [Fact]
    public void One_denied_source_does_not_hide_the_remaining_sources()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> Query(string query, TimeSpan _) =>
            query.Contains("MSFT_NetOffloadGlobalSetting", StringComparison.Ordinal) ? throw new UnauthorizedAccessException() : [];
        var result = NetworkConfigurationReader.ReadSources(1, "Adapter", Query);
        Assert.Equal(["MSFT_NetOffloadGlobalSetting"], result.Unavailable);
        Assert.Equal(15, result.Sources.Count);
        Assert.Equal("UnauthorizedAccessException", result.Sources[0].Error);
    }

    [Fact]
    public void A_budget_exhausted_before_a_query_never_starts_it_or_claims_success()
    {
        var result = NetworkConfigurationReader.ReadSources(1, "Adapter", (_, _) => throw new InvalidOperationException(),
            budget: TimeSpan.Zero);
        Assert.Equal(15, result.Unavailable.Count);
        Assert.All(result.Sources, s => Assert.Equal("TimeBudgetExceeded", s.Error));
    }

    [Fact]
    public void Unknown_adapter_identity_does_not_read_other_adapters()
    {
        var queries = new List<string>();
        var result = NetworkConfigurationReader.ReadSources(1, null, (query, _) => { queries.Add(query); return []; });
        Assert.Equal(11, result.Sources.Count);
        Assert.Equal(["AdapterSettings"], result.Unavailable);
        Assert.DoesNotContain(queries, q => q.Contains("NetAdapter", StringComparison.Ordinal));
    }

    [Fact]
    public void Unexpected_programming_errors_are_not_silently_swallowed()
    {
        Assert.Throws<InvalidOperationException>(() => NetworkConfigurationReader.ReadSources(1, "Adapter", (_, _) => throw new InvalidOperationException()));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkConfigurationReader.ReadSources(0, null, (_, _) => []));
    }

    [Fact]
    public void Live_read_is_read_only_and_keeps_each_source_outcome()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
            && n.Supports(NetworkInterfaceComponent.IPv4) && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        if (nic is null) Assert.Skip("No active IPv4 adapter is available for the read-only inventory.");
        var result = NetworkConfigurationReader.WithAddressing(
            NetworkConfigurationReader.Read(nic.GetIPProperties().GetIPv4Properties().Index, nic.Description), nic);
        Assert.Equal(15, result.Sources.Count(s => s.Name != "Addressing"));
        Assert.All(result.Sources, s => Assert.True(s.Error is not null || s.Rows is not null));
        Assert.Contains(result.Sources, s => s.Name == "MSFT_NetOffloadGlobalSetting");
        Assert.NotNull(result.DhcpEnabled);
        TestContext.Current.TestOutputHelper?.WriteLine("Readable sources: {0}; unavailable: {1}",
            result.Sources.Count(s => s.Error is null && s.Rows.Count > 0), string.Join(", ", result.Unavailable));
    }

    [Fact]
    public void Background_checks_reuse_only_the_slow_inventory_and_manual_checks_refresh_it()
    {
        var path = Fx.RequireDefaultPath();
        var fresh = SystemInspector.Inspect(path).Configuration;
        Assert.NotNull(fresh);
        var scheduled = SystemInspector.Inspect(path, reuseSlowFacts: true).Configuration;
        Assert.NotNull(scheduled);
        Assert.Same(fresh.Sources, scheduled.Sources);
        Assert.NotSame(fresh, scheduled);
        Assert.NotSame(fresh.Sources, SystemInspector.Inspect(path).Configuration!.Sources);
    }
}
