using ConnectionClue.Core;

namespace ConnectionClue.Analysis.Tests;

public class NetworkSettingsTests
{
    private static readonly HealthReport Clean = new(HealthLevel.NoIssue, []);
    private static readonly HealthReport LocalDelay = new(HealthLevel.Degraded,
        [new(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.LocalNetwork, 50, 20)]);
    private static readonly HealthReport RemoteDelay = new(HealthLevel.Degraded,
        [new(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter, 50, 20)]);

    private static DriverSetting Setting(string keyword, string value, string defaultValue = "1") =>
        new(keyword, keyword, value, value == "0" ? "Disabled" : "Enabled", defaultValue,
            defaultValue == "0" ? "Disabled" : "Enabled", [new("0", "Disabled"), new("1", "Enabled")]);

    private static SystemFacts Facts(IReadOnlyList<DriverSetting>? settings = null, NetworkConfiguration? configuration = null,
        bool tunnel = false, ConnectionMedium medium = ConnectionMedium.Ethernet) =>
        new(new(medium, 1000, Name: "Test adapter", Alias: "Ethernet", Settings: settings),
            TunnelSuspected: tunnel, Configuration: configuration);

    private static AdvisorInput Input(SystemFacts facts, Symptom? symptom = Symptom.Video, HealthReport? report = null, double? dns = null) =>
        new(facts, symptom, report ?? Clean, dns, null, null, new(2026, 11, 1));

    [Fact]
    public void Unknown_settings_produce_neither_tuning_advice_nor_a_healthy_inventory()
    {
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts())));
        Assert.DoesNotContain(CheckArea.SettingsCoverage, ConfigurationAdvisor.Review(Input(Facts())).Passed);
        var unreadable = new NetworkConfiguration([new("MSFT_NetOffloadGlobalSetting", [], "AccessDenied")]);
        var advice = Assert.Single(NetworkSettingsRules.Evaluate(Input(Facts(configuration: unreadable))));
        Assert.Equal(AdvisoryCode.NetworkSettingsUnavailable, advice.Code);
        Assert.Equal(["MSFT_NetOffloadGlobalSetting"], advice.Args);
        Assert.DoesNotContain(CheckArea.SettingsCoverage, ConfigurationAdvisor.Review(Input(Facts(configuration: unreadable))).Passed);
    }

    [Theory]
    [InlineData("0", null, true, null)]
    [InlineData("1", "0", true, "Global")]
    [InlineData("1", "1", false, null)]
    [InlineData("1", null, false, null)]
    public void Rss_distinguishes_global_and_adapter_settings(string adapter, string? global, bool expected, string? variant)
    {
        var config = global is null ? null : new NetworkConfiguration([new("MSFT_NetOffloadGlobalSetting",
            [new Dictionary<string, string> { ["ReceiveSideScaling"] = global }])]);
        var advice = NetworkSettingsRules.Evaluate(Input(Facts([Setting("*RSS", adapter)], config)));
        Assert.Equal(expected, advice.Any());
        if (expected) Assert.Equal((AdvisoryCode.ReceiveScalingDisabled, variant), (advice[0].Code, advice[0].Variant));
    }

    [Fact]
    public void Rss_requires_proven_support_and_never_suggests_tuning_a_tunnel_or_wifi()
    {
        var config = new NetworkConfiguration([new("MSFT_NetOffloadGlobalSetting",
            [new Dictionary<string, string> { ["ReceiveSideScaling"] = "0" }])]);
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts(configuration: config))));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts([Setting("*RSS", "0", "0")], config))));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts([Setting("*RSS", "0")], config, tunnel: true))));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts([Setting("*RSS", "0")], config, medium: ConnectionMedium.WiFi))));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts([Setting("*RSS", "0")]), Symptom.Gaming)));
    }

    [Fact]
    public void Offloads_are_one_card_and_only_recommend_supported_driver_defaults()
    {
        var facts = Facts([Setting("*TCPChecksumOffloadIPv4", "0"), Setting("*LsoV2IPv4", "0"),
            Setting("*UDPChecksumOffloadIPv6", "0", "0"), Setting("VendorUnknown", "0")]);
        var advice = Assert.Single(NetworkSettingsRules.Evaluate(Input(facts)));
        Assert.Equal(AdvisoryCode.AdapterOffloadDisabled, advice.Code);
        Assert.Contains("*TCPChecksumOffloadIPv4", advice.Args![1]);
        Assert.Contains("*LsoV2IPv4", advice.Args[1]);
        Assert.DoesNotContain("*UDPChecksumOffloadIPv6", advice.Args[1]);
        Assert.DoesNotContain("VendorUnknown", advice.Args[1]);
        Assert.DoesNotContain(CheckArea.DriverSettings, ConfigurationAdvisor.Review(Input(facts)).Passed);
        Assert.DoesNotContain(CheckArea.DriverSettings, ConfigurationAdvisor.Review(Input(facts, Symptom.Gaming)).Passed);
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts with { TcpTweaks = ["DisableTaskOffload=1"] })));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts with { TunnelSuspected = true })));
    }

    [Fact]
    public void A_global_offload_disable_is_distinguished_from_an_adapter_setting()
    {
        var config = new NetworkConfiguration([new("MSFT_NetOffloadGlobalSetting",
            [new Dictionary<string, string> { ["TaskOffload"] = "0" }])]);
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(Facts(configuration: config))));
        var advice = Assert.Single(NetworkSettingsRules.Evaluate(Input(Facts([Setting("*LsoV2IPv4", "1")], config))));
        Assert.Equal((AdvisoryCode.AdapterOffloadDisabled, "Global"), (advice.Code, advice.Variant));
    }

    [Fact]
    public void Interrupt_moderation_is_a_trial_only_for_measured_local_realtime_delay()
    {
        var facts = Facts([Setting("*InterruptModeration", "1")]);
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts, Symptom.Gaming)));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts, Symptom.Gaming, RemoteDelay)));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts, Symptom.Video, LocalDelay)));
        var advice = Assert.Single(NetworkSettingsRules.Evaluate(Input(facts, Symptom.Calls, LocalDelay)));
        Assert.Equal((AdvisoryCode.InterruptModeration, AdvisoryLevel.Suggestion), (advice.Code, advice.Level));
        Assert.DoesNotContain(CheckArea.DriverSettings, ConfigurationAdvisor.Review(Input(facts, Symptom.Calls, LocalDelay)).Passed);
    }

    [Theory]
    [InlineData(true, true, AdvisoryCode.LinkLocalAddress)]
    [InlineData(false, true, AdvisoryCode.StaticNetworkConfiguration)]
    [InlineData(false, false, AdvisoryCode.StaticNetworkConfiguration)]
    public void Addressing_advice_uses_known_settings_not_an_assumed_router_fault(bool dhcp, bool localOnly, AdvisoryCode code)
    {
        var facts = Facts(configuration: new([], DhcpEnabled: dhcp, LinkLocalAddressOnly: localOnly));
        var advice = Assert.Single(NetworkSettingsRules.Evaluate(Input(facts, report: RemoteDelay)));
        Assert.Equal(code, advice.Code);
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts with { TunnelSuspected = true }, report: RemoteDelay)));
    }

    [Fact]
    public void Manual_dns_alone_is_not_a_fault_and_requires_slow_lookups_with_a_measured_problem()
    {
        var facts = Facts(configuration: new([], DhcpEnabled: true, StaticDns: true, LinkLocalAddressOnly: false));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts)));
        Assert.Empty(NetworkSettingsRules.Evaluate(Input(facts, report: RemoteDelay)));
        var advice = Assert.Single(NetworkSettingsRules.Evaluate(Input(facts, report: RemoteDelay, dns: 200)));
        Assert.Equal((AdvisoryCode.StaticNetworkConfiguration, "Dns"), (advice.Code, advice.Variant));
        Assert.DoesNotContain(CheckArea.Addressing, ConfigurationAdvisor.Review(Input(facts, report: RemoteDelay, dns: 200)).Passed);
    }
}
