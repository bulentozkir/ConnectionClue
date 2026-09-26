using static ConnectionClue.Analysis.AdvisoryCode;

namespace ConnectionClue.Analysis.Tests;

public class ConfigurationAdvisorTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly HealthReport Clean = new(HealthLevel.NoIssue, []);

    private static IReadOnlyList<Advisory> Eval(SystemFacts facts, Symptom? symptom = null, HealthReport? report = null,
        double? dns = null, double? idle = null, double? loaded = null) =>
        ConfigurationAdvisor.Evaluate(new(facts, symptom, report ?? Clean, dns, idle, loaded, Today));

    [Fact]
    public void Healthy_well_configured_pc_gets_no_advice() =>
        Assert.Empty(Eval(new(new AdapterFacts(ConnectionMedium.Ethernet, 1000, new DateOnly(2026, 1, 1), false, false)), Symptom.Gaming, dns: 20, idle: 20, loaded: 40));

    [Fact]
    public void Global_settings_are_always_reported()
    {
        var codes = Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 400, new DateOnly(2021, 6, 1)), Metered: true, ProxyConfigured: true,
            Ipv6Disabled: true, TcpAutoTuningLimited: true)).Select(a => a.Code);
        Assert.Equal([TcpAutoTuningLimited, OldNetworkDriver, MeteredConnection, ProxyConfigured, Ipv6Disabled], codes);
    }

    [Fact]
    public void Driver_date_is_encoded_for_the_title() =>
        Assert.Equal(202106, Assert.Single(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 400, new DateOnly(2021, 6, 9))))).Value);

    [Fact]
    public void Symptom_specific_advice_needs_the_symptom_or_an_issue()
    {
        var eee = new SystemFacts(new AdapterFacts(ConnectionMedium.Ethernet, 1000, null, PowerOffAllowed: true, EnergyEfficientEthernet: true));
        Assert.Empty(Eval(eee, Symptom.Video));
        Assert.Equal([EnergyEfficientEthernet], Eval(eee, Symptom.Gaming).Select(a => a.Code));
        Assert.Equal([AdapterPowerSaving], Eval(eee, Symptom.Disconnects).Select(a => a.Code));
    }

    [Fact]
    public void Measurements_raise_important_advice_first()
    {
        var r = Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 54)), Symptom.Calls, dns: 400, idle: 30, loaded: 250);
        Assert.Equal([LatencyUnderLoad, WifiLinkSlow, SlowDns], r.Select(a => a.Code));
        Assert.Equal((220, AdvisoryLevel.Important), (r[0].Value, r[0].Level));
        Assert.Equal(AdvisoryLevel.Suggestion, r[2].Level);
    }

    [Fact]
    public void Slow_cable_link_and_battery_power_saving()
    {
        Assert.Equal([EthernetLinkSlow], Eval(new(new AdapterFacts(ConnectionMedium.Ethernet, 100))).Select(a => a.Code));
        Assert.Equal([PowerSaving], Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 600), OnBattery: true, WifiPowerSavingOnBattery: 3)).Select(a => a.Code));
        Assert.Empty(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 600), OnBattery: false, WifiPowerSavingOnBattery: 3)));
    }

    [Fact]
    public void Vpn_advice_only_without_issues_for_realtime_use()
    {
        Assert.Equal([VpnActive], Eval(new(TunnelSuspected: true), Symptom.Gaming).Select(a => a.Code));
        Assert.Empty(Eval(new(TunnelSuspected: true), Symptom.Video));
    }

    [Fact]
    public void Weak_signal_replaces_slow_link_advice()
    {
        var weak = Assert.Single(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 40, SignalBars: 1)), Symptom.Disconnects));
        Assert.Equal((WeakWifiSignal, AdvisoryLevel.Important, 1), (weak.Code, weak.Level, weak.Value));
        Assert.Equal(AdvisoryLevel.Suggestion, Assert.Single(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 400, SignalBars: 2)))).Level);
        Assert.Empty(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 400, SignalBars: 3))));
    }

    [Fact]
    public void Wifi_link_below_the_2_4_GHz_ceiling_is_a_suggestion_unless_it_hurts()
    {
        var wifi = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 120, SignalBars: 3));
        Assert.Equal((WifiLinkSlow, AdvisoryLevel.Suggestion, 120), Only(Eval(wifi, Symptom.Gaming)));
        var local = new HealthReport(HealthLevel.Degraded, [new HealthIssue(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.LocalNetwork, 150, 100)]);
        Assert.Equal(AdvisoryLevel.Important, Eval(wifi, Symptom.Gaming, local).First(a => a.Code == WifiLinkSlow).Level);
    }

    [Fact]
    public void Other_traffic_on_this_pc_is_reported_with_both_directions()
    {
        var facts = new SystemFacts(new AdapterFacts(ConnectionMedium.Ethernet, 1000));
        Assert.Equal((OtherTraffic, AdvisoryLevel.Important, 12.3), Only(Review(facts, Symptom.Calls, traffic: (12.34, 0.2)).Advice));
        Assert.Equal(0.2, Review(facts, Symptom.Calls, traffic: (12.34, 0.2)).Advice[0].Value2);
        var quiet = Review(facts, Symptom.Video, traffic: (0.4, 0.1));
        Assert.Empty(quiet.Advice);
        Assert.Contains(CheckArea.OtherTraffic, quiet.Passed);
    }

    [Fact]
    public void Usb_adapter_power_saving_matters_only_for_disconnects()
    {
        var usb = new SystemFacts(new AdapterFacts(ConnectionMedium.Ethernet, 1000, PowerOffAllowed: false, IsUsb: true, UsbSelectiveSuspend: true));
        Assert.Equal(UsbSelectiveSuspend, Assert.Single(Eval(usb, Symptom.Disconnects)).Code);
        var gaming = Review(usb, Symptom.Gaming);
        Assert.Empty(gaming.Advice);
        Assert.DoesNotContain(CheckArea.UsbPower, gaming.Passed); // enabled but not relevant: neither advice nor "fine"
        Assert.Contains(CheckArea.AdapterPower, gaming.Passed);
    }

    [Fact]
    public void Wifi_power_saving_while_plugged_in_is_reported()
    {
        Assert.Equal(PowerSaving, Assert.Single(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 600), WifiPowerSavingOnAc: 2))).Code);
        Assert.Empty(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 600), WifiPowerSavingOnAc: 0, WifiPowerSavingOnBattery: 3)));
    }

    [Fact]
    public void Healthy_pc_lists_what_was_checked_and_found_fine()
    {
        var facts = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 400, new DateOnly(2026, 4, 13), PowerOffAllowed: true, SignalBars: 4),
            TcpAutoTuningLimited: false, WifiPowerSavingOnAc: 0);
        var r = Review(facts, Symptom.Gaming, dns: 20, idle: 30, loaded: 40, traffic: (0.2, 0.1));
        Assert.Empty(r.Advice);
        Assert.Equal([CheckArea.WifiSignal, CheckArea.LinkSpeed, CheckArea.NetworkDriver, CheckArea.TcpTuning, CheckArea.PowerSaving,
            CheckArea.NameLookups, CheckArea.LatencyUnderLoad, CheckArea.OtherTraffic, CheckArea.Proxy, CheckArea.Vpn,
            CheckArea.MeteredConnection, CheckArea.Ipv6], r.Passed);
    }

    [Fact]
    public void A_flagged_area_is_never_listed_as_fine() =>
        Assert.DoesNotContain(CheckArea.Proxy, Review(new(ProxyConfigured: true)).Passed);

    [Fact]
    public void Other_traffic_names_the_apps_and_picks_their_instructions()
    {
        var facts = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 400, SignalBars: 4));
        AppTraffic[] apps = [new("Microsoft OneDrive", "OneDrive.exe", 13.8, 0.7), new("Visual Studio Code", "Code.exe", 3.4, 4.4), new("Tiny", "tiny.exe", 0.1, 0.1)];
        var a = Assert.Single(ConfigurationAdvisor.Evaluate(new(facts, Symptom.Gaming, Clean, null, null, null, Today, (0.3, 2.2), apps)));
        Assert.Equal(("OneDrive", 0.3, 2.2), (a.Variant, a.Value, a.Value2));
        Assert.Equal(["Microsoft OneDrive", "#13.8", "Visual Studio Code", "#3.4"], a.Args); // upload triggered: sent MB

        var windows = ConfigurationAdvisor.Evaluate(new(facts, Symptom.Gaming, Clean, null, null, null, Today, (8, 0.2), [new("Host", "", 20, 1)]))[0];
        Assert.Equal(("Windows", "@App_Windows"), (windows.Variant, windows.Args![0]));
        var unknown = ConfigurationAdvisor.Evaluate(new(facts, Symptom.Gaming, Clean, null, null, null, Today, (8, 0.2)))[0];
        Assert.Equal((null, null), (unknown.Variant, unknown.Args));
    }

    [Fact]
    public void Upload_traffic_is_blamed_on_the_biggest_sender_not_the_biggest_app()
    {
        AppTraffic[] apps = [new("SmartTicker.Desktop", "SmartTicker.Desktop.exe", 0.1, 4.7), new("Microsoft OneDrive", "OneDrive.exe", 2.7, 0.1)];
        var a = ConfigurationAdvisor.Evaluate(new(new SystemFacts(), Symptom.Gaming, Clean, null, null, null, Today, (0.8, 1.6), apps))[0];
        Assert.Equal(("OneDrive", "Microsoft OneDrive", "#2.7"), (a.Variant, a.Args![0], a.Args[1]));
    }

    [Fact]
    public void Latency_under_load_names_the_direction_and_a_router_limit()
    {
        var r = ConfigurationAdvisor.Evaluate(new(new SystemFacts(), Symptom.Gaming, Clean, null, 48, 180, Today,
            DownloadMbps: 32.9, UploadMbps: 11.3, LoadedDownMs: 60, LoadedUpMs: 180))[0];
        Assert.Equal((LatencyUnderLoad, 132, 10, "Upload"), (r.Code, r.Value, r.Value2, r.Variant)); // 90% of 11.3 Mbps, whole Mbps above 10
    }

    [Fact]
    public void Settings_advice_carries_the_exact_value_found()
    {
        var facts = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 120, SignalBars: 3, Name: "Intel(R) Wi-Fi 6E AX210 160MHz"),
            NetworkName: "HomeNet", DnsServer: "192.168.1.1", DnsIsRouter: true, ProxyConfigured: true, ProxyAddress: "127.0.0.1:8888",
            TcpAutoTuningLimited: true, TcpAutoTuningLevel: 0, Ipv6Disabled: true, Ipv6DisabledComponents: 0xFF, EnergySaverOn: true);
        var r = ConfigurationAdvisor.Evaluate(new(facts, Symptom.Video, Clean, 320, null, null, Today)).ToDictionary(a => a.Code);
        Assert.Equal(["HomeNet", "Intel(R) Wi-Fi 6E AX210 160MHz", "Wi-Fi 6E"], r[WifiLinkSlow].Args);
        Assert.Equal("Capable", r[WifiLinkSlow].Variant);
        Assert.Equal(("Router", "192.168.1.1"), (r[SlowDns].Variant, r[SlowDns].Args![0]));
        Assert.Equal(("Local", "127.0.0.1:8888"), (r[ProxyConfigured].Variant, r[ProxyConfigured].Args![0]));
        Assert.Equal("disabled", r[TcpAutoTuningLimited].Args![0]);
        Assert.Equal(("Registry", "0xFF"), (r[Ipv6Disabled].Variant, r[Ipv6Disabled].Args![0]));
        Assert.Equal("EnergySaver", r[PowerSaving].Variant);
    }

    [Fact]
    public void Wifi_power_saving_variant_names_the_level_and_power_source()
    {
        var a = Assert.Single(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 600), OnBattery: true, WifiPowerSavingOnBattery: 3)));
        Assert.Equal(("WifiBattery", 3, "@WifiPower_3"), (a.Variant, a.Value, a.Args![0]));
    }

    [Theory]
    [InlineData("Intel(R) Wi-Fi 6E AX210 160MHz", "Wi-Fi 6E", true)]
    [InlineData("Intel(R) Wi-Fi 6 AX201 160MHz", "Wi-Fi 6", true)]
    [InlineData("Realtek RTL8852BE WiFi 6 802.11ax PCIe Adapter", "Wi-Fi 6", true)]
    [InlineData("Intel(R) Dual Band Wireless-AC 8265", "Wi-Fi 5", true)]
    [InlineData("Intel(R) Wi-Fi 7 BE200 320MHz", "Wi-Fi 7", true)]
    [InlineData("Realtek RTL8188EU Wireless LAN 802.11n USB 2.0 Network Adapter", "Wi-Fi 4", false)]
    public void Wifi_standard_is_read_from_the_adapter_name(string adapter, string label, bool fiveGhz) =>
        Assert.Equal((label, fiveGhz), ConfigurationAdvisor.WifiStandard(adapter));

    [Fact]
    public void Unknown_adapter_names_fall_back_to_generic_wording()
    {
        Assert.Null(ConfigurationAdvisor.WifiStandard("Qualcomm Atheros QCA9377 Wireless Network Adapter"));
        var a = Assert.Single(Eval(new(new AdapterFacts(ConnectionMedium.WiFi, 120, SignalBars: 3))));
        Assert.Equal((null, null), (a.Args, a.Variant)); // no network or adapter name: plain text
    }

    private static (AdvisoryCode, AdvisoryLevel, double) Only(IReadOnlyList<Advisory> advice)
    {
        var a = Assert.Single(advice);
        return (a.Code, a.Level, a.Value);
    }

    private static AdvisorResult Review(SystemFacts facts, Symptom? symptom = null, HealthReport? report = null,
        double? dns = null, double? idle = null, double? loaded = null, (double, double)? traffic = null) =>
        ConfigurationAdvisor.Review(new(facts, symptom, report ?? Clean, dns, idle, loaded, Today, traffic));
}
