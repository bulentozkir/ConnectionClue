using static ConnectionClue.Analysis.AdvisoryCode;

namespace ConnectionClue.Analysis.Tests;

/// <summary>What the advisor makes of the radio, the driver's advanced settings and the adapter's counters: each rule fires on
/// the evidence that proves it, stays quiet without a symptom where the setting may be intentional, and never calls an area
/// fine that it could not see or that it held back advice about.</summary>
public class AdapterAdviceTests
{
    private const string WifiName = "Intel(R) Wi-Fi 6E AX210 160MHz", Network = "HomeNet";
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly HealthReport Clean = new(HealthLevel.NoIssue, []);
    private static readonly HealthReport Lossy = new(HealthLevel.Degraded, [new HealthIssue(IssueKind.Loss, HealthLevel.Degraded, IssueLocation.BeyondRouter, 3, 2)]);
    private static readonly HealthReport Choppy = new(HealthLevel.Degraded, [new HealthIssue(IssueKind.Variation, HealthLevel.Degraded, IssueLocation.LocalNetwork, 80, 30)]);

    private static AdvisorInput Input(SystemFacts facts, Symptom? symptom, HealthReport? report) => new(facts, symptom, report ?? Clean, null, null, null, Today);

    private static IReadOnlyList<Advisory> Eval(SystemFacts facts, Symptom? symptom = null, HealthReport? report = null) =>
        ConfigurationAdvisor.Evaluate(Input(facts, symptom, report));

    private static AdvisorResult Review(SystemFacts facts, Symptom? symptom = null, HealthReport? report = null) =>
        ConfigurationAdvisor.Review(Input(facts, symptom, report));

    private static IEnumerable<AdvisoryCode> Codes(SystemFacts facts, Symptom? symptom = null, HealthReport? report = null) =>
        Eval(facts, symptom, report).Select(a => a.Code);

    private static SystemFacts Wifi(double mbps = 600, int? bars = 4, int? dbm = null, int? channel = null, double? retry = null, long frames = 0,
        string? cipher = null, IReadOnlyList<DriverSetting>? settings = null) => new(new AdapterFacts(ConnectionMedium.WiFi, mbps, SignalBars: bars,
            Name: WifiName, SignalDbm: dbm, WifiChannel: channel, WifiRetryPercent: retry, WifiFrames: frames, WifiEncryption: cipher, Settings: settings),
            NetworkName: Network);

    private static SystemFacts Wired(double mbps = 1000, long errors = 0, long packets = 0, int? mtu = null, bool? eee = null, bool metered = false,
        IReadOnlyList<DriverSetting>? settings = null) => new(new AdapterFacts(ConnectionMedium.Ethernet, mbps, EnergyEfficientEthernet: eee,
            Name: "Realtek PCIe GbE", PacketErrors: errors, Packets: packets, Mtu: mtu, Alias: "Ethernet", Settings: settings), Metered: metered);

    private static DriverSetting Setting(string keyword, string label, string value, string defaultValue, params (string Value, string Text)[] options)
    {
        string Text(string v) => options.FirstOrDefault(o => o.Value == v).Text ?? v;
        return new(keyword, label, value, Text(value), defaultValue, Text(defaultValue), [.. options.Select(o => new DriverOption(o.Value, o.Text))]);
    }

    // The Intel Wi-Fi 6E keywords and wording exactly as the driver on a real PC declares them.
    private static DriverSetting Mode(string v) => Setting("IEEE11nMode", "802.11n/ac/ax Wireless Mode", v, "3",
        ("0", "1. Disabled"), ("1", "2. 802.11n"), ("2", "3. 802.11ac"), ("3", "4. 802.11ax"));
    private static DriverSetting Width5(string v) => Setting("ChannelWidth52", "Channel Width for 5GHz", v, "1", ("0", "20 MHz Only"), ("1", "Auto"));
    private static DriverSetting Width6(string v) => Setting("ChannelWidth6", "Channel Width for 6GHz", v, "1", ("0", "20 MHz Only"), ("1", "Auto"));
    private static DriverSetting Mimo(string v) => Setting("MIMOPowerSaveMode", "MIMO Power Save Mode", v, "0",
        ("0", "Auto SMPS"), ("1", "Static SMPS"), ("2", "Dynamic SMPS"), ("3", "No SMPS"));
    private static DriverSetting Power(string v) => Setting("IbssTxPower", "Transmit Power", v, "100",
        ("0", "1. Lowest"), ("25", "2. Medium-low"), ("50", "3. Medium"), ("75", "4. Medium-High"), ("100", "5. Highest"));
    private static DriverSetting Band(string v) => Setting("RoamingPreferredBandType", "Preferred Band", v, "0",
        ("0", "1. No Preference"), ("1", "2. Prefer 2.4GHz band"), ("2", "3. Prefer 5GHz band"), ("4", "5. Prefer 5GHz + 6GHz band"));
    private static DriverSetting Roaming(string v) => Setting("RoamAggressiveness", "Roaming Aggressiveness", v, "2",
        ("0", "1. Lowest"), ("1", "2. Medium-low"), ("2", "3. Medium"), ("3", "4. Medium-High"), ("4", "5. Highest"));
    private static DriverSetting Duplex(string v) => Setting("*SpeedDuplex", "Speed & Duplex", v, "0", ("0", "Auto Negotiation"),
        ("1", "10 Mbps Half Duplex"), ("2", "10 Mbps Full Duplex"), ("3", "100 Mbps Half Duplex"), ("4", "100 Mbps Full Duplex"), ("6", "1.0 Gbps Full Duplex"));
    private static DriverSetting Jumbo(string v) => Setting("*JumboPacket", "Jumbo Packet", v, "1514", ("1514", "Disabled"), ("4088", "4088 Bytes"), ("9014", "9014 Bytes"));
    private static DriverSetting Green(string v) => Setting("EnableGreenEthernet", "Green Ethernet", v, "0", ("0", "Disabled"), ("1", "Enabled"));

    // Signal ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_weak_dBm_is_called_out_even_though_windows_still_shows_three_bars()
    {
        var weak = Assert.Single(Eval(Wifi(180, bars: 3, dbm: -73), Symptom.Gaming));
        Assert.Equal((WeakWifiSignal, AdvisoryLevel.Important, 3, 180, "Dbm"), (weak.Code, weak.Level, weak.Value, weak.Value2, weak.Variant));
        Assert.Equal([Network, WifiName, "#-73"], weak.Args);
        Assert.Equal(AdvisoryLevel.Suggestion, Assert.Single(Eval(Wifi(180, bars: 3, dbm: -72), Symptom.Video)).Level); // not real-time, not very weak
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(Wifi(180, bars: 3, dbm: -80), Symptom.Video)).Level);
    }

    [Fact]
    public void A_good_dBm_with_three_bars_is_fine_and_a_missing_one_is_never_called_fine()
    {
        var good = Review(Wifi(600, bars: 3, dbm: -65));
        Assert.Empty(good.Advice);
        Assert.Contains(CheckArea.WifiSignal, good.Passed);
        Assert.Contains(CheckArea.WifiSignal, Review(Wifi(600, bars: null, dbm: -60)).Passed); // the radio alone is enough
        Assert.DoesNotContain(CheckArea.WifiSignal, Review(Wifi(600, bars: 3, dbm: -73)).Passed);
        Assert.DoesNotContain(CheckArea.WifiSignal, Review(Wifi(600, bars: null, dbm: null)).Passed);
    }

    [Fact]
    public void A_slow_link_on_5_GHz_is_about_distance_not_about_switching_bands()
    {
        var five = Assert.Single(Eval(Wifi(120, channel: 100)));
        Assert.Equal((WifiLinkSlow, "Band5"), (five.Code, five.Variant));
        Assert.Equal([Network, WifiName, "5 GHz"], five.Args);
        Assert.Equal("Capable", Assert.Single(Eval(Wifi(120, channel: 6))).Variant); // on 2.4 GHz the advice is still to join the 5 GHz network
        Assert.Equal("Capable", Assert.Single(Eval(Wifi(120))).Variant); // an unknown band keeps the old advice
    }

    [Theory]
    [InlineData(null, WifiBand.Unknown)]
    [InlineData(0, WifiBand.Unknown)]
    [InlineData(1, WifiBand.Ghz24)]
    [InlineData(14, WifiBand.Ghz24)]
    [InlineData(36, WifiBand.Ghz5)]
    [InlineData(100, WifiBand.Ghz5)]
    [InlineData(177, WifiBand.Ghz5)]
    [InlineData(181, WifiBand.Ghz6)]
    [InlineData(233, WifiBand.Ghz6)]
    [InlineData(300, WifiBand.Unknown)]
    public void The_band_comes_from_the_channel_number(int? channel, WifiBand band) => Assert.Equal(band, DriverRules.BandOf(channel));

    // Retries and security ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Many_retried_frames_point_at_the_radio_link_and_name_the_band()
    {
        var some = Assert.Single(Eval(Wifi(retry: 12, frames: 5000, channel: 6), Symptom.Video));
        Assert.Equal((WifiRetries, AdvisoryLevel.Suggestion, 12, 5, "Band24"), (some.Code, some.Level, some.Value, some.Value2, some.Variant));
        Assert.Equal([Network], some.Args);
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(Wifi(retry: 12, frames: 5000), Symptom.Gaming)).Level); // real-time use
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(Wifi(retry: 25, frames: 5000), Symptom.Video)).Level); // failing link
        Assert.Equal("Band5", Assert.Single(Eval(Wifi(retry: 12, frames: 5000, channel: 100), Symptom.Video)).Variant);
        Assert.Null(Assert.Single(Eval(Wifi(retry: 12, frames: 5000), Symptom.Video)).Variant);
    }

    [Fact]
    public void Retries_need_enough_traffic_and_are_not_repeated_when_a_weak_signal_already_explains_them()
    {
        Assert.Empty(Eval(Wifi(retry: 40, frames: 1999)));
        Assert.DoesNotContain(CheckArea.WifiRetries, Review(Wifi(retry: 40, frames: 1999)).Passed);
        Assert.Empty(Eval(Wifi(retry: 9.9, frames: 5000)));
        Assert.Contains(CheckArea.WifiRetries, Review(Wifi(retry: 6.9, frames: 5000)).Passed);
        Assert.Equal([WeakWifiSignal], Codes(Wifi(180, bars: 1, retry: 40, frames: 5000), Symptom.Gaming));
    }

    [Theory]
    [InlineData("Tkip", "@Cipher_Tkip")]
    [InlineData("Wep", "@Cipher_Wep")]
    [InlineData("Wep40", "@Cipher_Wep")]
    [InlineData("Wep104", "@Cipher_Wep")]
    public void Tkip_and_wep_cap_the_link_so_they_replace_the_slow_link_advice(string cipher, string label)
    {
        var insecure = Assert.Single(Eval(Wifi(54, cipher: cipher), Symptom.Video));
        Assert.Equal((WifiInsecureCipher, AdvisoryLevel.Important), (insecure.Code, insecure.Level));
        Assert.Equal([Network, label], insecure.Args);
        Assert.DoesNotContain(CheckArea.WifiSecurity, Review(Wifi(54, cipher: cipher)).Passed);
    }

    [Theory]
    [InlineData("Ccmp", true)]
    [InlineData("Gcmp", true)]
    [InlineData("Gcmp256", true)]
    [InlineData("None", false)]   // an open network is not flagged, but it is not "checked, no change needed" either
    [InlineData("Unknown", false)]
    [InlineData("RsnUseGroup", false)]
    public void Only_a_known_strong_cipher_is_called_fine(string cipher, bool fine)
    {
        var result = Review(Wifi(600, cipher: cipher));
        Assert.Empty(result.Advice);
        Assert.Equal(fine, result.Passed.Contains(CheckArea.WifiSecurity));
    }

    // Wi-Fi driver settings -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_driver_setting_that_caps_the_link_is_named_with_the_drivers_own_words_and_replaces_the_slow_link_advice()
    {
        var mode = Assert.Single(Eval(Wifi(54, settings: [Mode("1")]), Symptom.Video));
        Assert.Equal((WifiDriverLimit, AdvisoryLevel.Important, "Mode"), (mode.Code, mode.Level, mode.Variant));
        Assert.Equal([WifiName, "802.11n/ac/ax Wireless Mode", "2. 802.11n", "4. 802.11ax"], mode.Args);

        Assert.Equal("Width", Assert.Single(Eval(Wifi(120, channel: 100, settings: [Width5("0")]), Symptom.Video)).Variant);
        Assert.Equal("Width", Assert.Single(Eval(Wifi(120, channel: 200, settings: [Width6("0")]), Symptom.Video)).Variant); // 6 GHz has its own setting
        Assert.Equal("Mimo", Assert.Single(Eval(Wifi(120, settings: [Mimo("1")]), Symptom.Video)).Variant);
        Assert.Equal("Mimo", Assert.Single(Eval(Wifi(120, settings: [Mimo("2")]), Symptom.Video)).Variant);
        Assert.Equal("TxPower", Assert.Single(Eval(Wifi(120, settings: [Power("25")]), Symptom.Video)).Variant);
        Assert.Equal("Band", Assert.Single(Eval(Wifi(120, channel: 6, settings: [Band("1")]), Symptom.Video)).Variant);
    }

    [Fact]
    public void Driver_settings_that_are_fine_or_do_not_apply_to_the_band_in_use_are_left_alone()
    {
        Assert.DoesNotContain(WifiDriverLimit, Codes(Wifi(54, settings: [Mode("3"), Width5("1"), Mimo("0"), Power("100"), Band("0")])));
        Assert.DoesNotContain(WifiDriverLimit, Codes(Wifi(54, channel: 6, settings: [Width5("0")]))); // a 20 MHz cap is not a loss on 2.4 GHz
        Assert.DoesNotContain(WifiDriverLimit, Codes(Wifi(54, channel: 100, settings: [Width6("0")]))); // that setting is for 6 GHz
        Assert.DoesNotContain(WifiDriverLimit, Codes(Wifi(54, channel: 100, settings: [Band("1")]))); // already on 5 GHz: the 2.4 GHz preference costs nothing
        Assert.DoesNotContain(WifiDriverLimit, Codes(Wifi(54, settings: [Mimo("3")]))); // No SMPS is the fastest choice
        Assert.Contains(WifiLinkSlow, Codes(Wifi(54, settings: [Mode("3")]))); // nothing explains the slow link, so the general advice stays
    }

    [Fact]
    public void A_driver_limit_waits_for_a_slow_link_or_a_local_problem_but_still_keeps_the_area_off_the_fine_list()
    {
        var fast = Wifi(600, settings: [Mimo("1")]);
        Assert.Empty(Eval(fast, Symptom.Video));
        Assert.DoesNotContain(CheckArea.DriverSettings, Review(fast, Symptom.Video).Passed);
        Assert.Contains(CheckArea.DriverSettings, Review(Wifi(600, settings: [Mimo("0"), Mode("3")])).Passed);
        Assert.DoesNotContain(CheckArea.DriverSettings, Review(Wifi(600)).Passed); // nothing was read, so nothing is called fine

        var local = Assert.Single(Eval(fast, Symptom.Video, Choppy));
        Assert.Equal((WifiDriverLimit, AdvisoryLevel.Suggestion), (local.Code, local.Level)); // not the cause of a slow link, so not Important
    }

    [Fact]
    public void High_roaming_is_a_suggestion_for_stationary_pcs_with_lag_and_never_beside_a_weak_signal()
    {
        Assert.Empty(Eval(Wifi(settings: [Roaming("4")])));
        var gaming = Assert.Single(Eval(Wifi(settings: [Roaming("4")]), Symptom.Gaming));
        Assert.Equal((WifiRoaming, AdvisoryLevel.Suggestion), (gaming.Code, gaming.Level));
        Assert.Equal([WifiName, "Roaming Aggressiveness", "5. Highest", "3. Medium", "1. Lowest"], gaming.Args);
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(Wifi(settings: [Roaming("3")]), Symptom.Video, Choppy)).Level);
        Assert.Empty(Eval(Wifi(settings: [Roaming("2")]), Symptom.Gaming)); // the default
        Assert.Empty(Eval(Wifi(settings: [Roaming("0")]), Symptom.Gaming)); // lower than the default
        Assert.DoesNotContain(WifiRoaming, Codes(Wifi(180, bars: 1, settings: [Roaming("4")]), Symptom.Gaming));
    }

    // Wired ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_speed_typed_in_by_hand_replaces_the_generic_slow_cable_advice()
    {
        var forced = Assert.Single(Eval(Wired(100, settings: [Duplex("4")])));
        Assert.Equal((EthernetSpeedForced, AdvisoryLevel.Important, 100, (string?)null), (forced.Code, forced.Level, forced.Value, forced.Variant));
        Assert.Equal(["Realtek PCIe GbE", "Speed & Duplex", "100 Mbps Full Duplex", "Auto Negotiation"], forced.Args);

        var half = Assert.Single(Eval(Wired(100, settings: [Duplex("3")])));
        Assert.Equal((AdvisoryLevel.Important, "Half"), (half.Level, half.Variant));
        Assert.Equal([EthernetLinkSlow], Codes(Wired(100, settings: [Duplex("0")]))); // automatic: the cable is the suspect
    }

    [Fact]
    public void Forcing_gigabit_is_only_a_suggestion_and_a_driver_without_an_automatic_default_is_not_judged()
    {
        var gigabit = Assert.Single(Eval(Wired(1000, settings: [Duplex("6")])));
        Assert.Equal((EthernetSpeedForced, AdvisoryLevel.Suggestion), (gigabit.Code, gigabit.Level));
        var odd = Setting("*SpeedDuplex", "Speed & Duplex", "4", "4", ("0", "Auto Negotiation"), ("4", "100 Mbps Full Duplex"));
        Assert.Empty(Eval(Wired(1000, settings: [odd])));
    }

    [Fact]
    public void Jumbo_frames_matter_only_when_something_is_wrong()
    {
        Assert.Empty(Eval(Wired(settings: [Jumbo("9014")])));
        Assert.DoesNotContain(CheckArea.DriverSettings, Review(Wired(settings: [Jumbo("9014")])).Passed);
        var jumbo = Assert.Single(Eval(Wired(settings: [Jumbo("9014")]), report: Lossy));
        Assert.Equal((JumboFrames, AdvisoryLevel.Suggestion), (jumbo.Code, jumbo.Level));
        Assert.Equal(["Realtek PCIe GbE", "Jumbo Packet", "9014 Bytes", "Disabled"], jumbo.Args);
        Assert.Empty(Eval(Wired(settings: [Jumbo("1514")]), report: Lossy));
    }

    [Fact]
    public void Green_ethernet_is_the_same_advice_as_energy_efficient_ethernet_and_never_twice()
    {
        var green = Assert.Single(Eval(Wired(settings: [Green("1")]), Symptom.Gaming));
        Assert.Equal((EnergyEfficientEthernet, AdvisoryLevel.Important, "Green"), (green.Code, green.Level, green.Variant));
        Assert.Equal(["Realtek PCIe GbE", "Green Ethernet", "Disabled"], green.Args);
        Assert.Empty(Eval(Wired(settings: [Green("1")]))); // without a symptom nothing is said
        Assert.Empty(Eval(Wired(settings: [Green("0")]), Symptom.Gaming));
        Assert.Null(Assert.Single(Eval(Wired(eee: true, settings: [Green("1")]), Symptom.Gaming)).Variant); // the EEE advice already covers it
    }

    // Counters, MTU and TCP/IP -----------------------------------------------------------------------------------------------

    [Fact]
    public void Packet_errors_on_a_cable_link_are_important_once_they_are_a_real_share_of_the_traffic()
    {
        var errors = Assert.Single(Eval(Wired(errors: 500, packets: 100_000)));
        Assert.Equal((AdapterErrors, AdvisoryLevel.Important, 500, 0.5, "Ethernet"), (errors.Code, errors.Level, errors.Value, errors.Value2, errors.Variant));
        Assert.Equal(["Realtek PCIe GbE"], errors.Args);
        Assert.Equal(AdvisoryLevel.Suggestion, Assert.Single(Eval(Wired(errors: 150, packets: 100_000))).Level); // 0.15%: worth knowing
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(Wired(errors: 150, packets: 100_000), report: Choppy)).Level); // and a local problem
    }

    [Fact]
    public void A_few_errors_are_noise_and_a_clean_adapter_is_fine()
    {
        Assert.Empty(Eval(Wired(errors: 200, packets: 1_000_000))); // 0.02%
        Assert.Empty(Eval(Wired(errors: 50, packets: 100))); // too few to mean anything
        Assert.Contains(CheckArea.AdapterErrors, Review(Wired(errors: 3, packets: 1_000_000)).Passed);
        Assert.DoesNotContain(CheckArea.AdapterErrors, Review(Wired(errors: 500, packets: 100_000)).Passed);
        Assert.DoesNotContain(CheckArea.AdapterErrors, Review(Wired()).Passed); // no packets counted: unknown
    }

    [Fact]
    public void Wifi_errors_are_a_suggestion()
    {
        var facts = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 600, Name: WifiName, PacketErrors: 300, Packets: 100_000), NetworkName: Network);
        var errors = Assert.Single(Eval(facts));
        Assert.Equal((AdvisoryLevel.Suggestion, "WiFi", 0.3), (errors.Level, errors.Variant, errors.Value2));
    }

    [Fact]
    public void A_custom_small_packet_size_is_only_reported_with_a_problem_and_never_on_a_hotspot()
    {
        Assert.Empty(Eval(Wired(mtu: 1280)));
        var small = Assert.Single(Eval(Wired(mtu: 1280), report: Lossy));
        Assert.Equal((MtuUnusual, AdvisoryLevel.Suggestion, 1280), (small.Code, small.Level, small.Value));
        Assert.Equal(["Ethernet"], small.Args);
        Assert.DoesNotContain(MtuUnusual, Codes(Wired(mtu: 1280, metered: true), report: Lossy)); // phone hotspots hand out their own
        Assert.Empty(Eval(Wired(mtu: 1492), report: Lossy)); // PPPoE
        Assert.Contains(CheckArea.Mtu, Review(Wired(mtu: 1500)).Passed);
        Assert.DoesNotContain(CheckArea.Mtu, Review(Wired(mtu: 1280)).Passed);
        Assert.DoesNotContain(CheckArea.Mtu, Review(Wired(mtu: 9000)).Passed); // jumbo is judged through the driver setting
    }

    [Fact]
    public void Limiting_tcp_tweaks_are_always_reported_and_the_worst_ones_are_important()
    {
        var pmtu = Assert.Single(Eval(new SystemFacts(TcpTweaks: ["EnablePMTUDiscovery=0", "DisableTaskOffload=1"])));
        Assert.Equal((TcpTweaks, AdvisoryLevel.Important), (pmtu.Code, pmtu.Level));
        Assert.Equal(["EnablePMTUDiscovery=0, DisableTaskOffload=1"], pmtu.Args);
        Assert.Equal(AdvisoryLevel.Suggestion, Assert.Single(Eval(new SystemFacts(TcpTweaks: ["DisableTaskOffload=1"]))).Level);
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(new SystemFacts(TcpTweaks: ["Tcp1323Opts=0"]))).Level);
        Assert.Equal(AdvisoryLevel.Important, Assert.Single(Eval(new SystemFacts(TcpTweaks: ["SackOpts=0"]))).Level);
    }

    [Fact]
    public void Tcp_settings_are_fine_only_when_they_were_read_and_nothing_limits()
    {
        Assert.Contains(CheckArea.TcpIpSettings, Review(new SystemFacts(TcpTweaks: [])).Passed);
        Assert.DoesNotContain(CheckArea.TcpIpSettings, Review(new SystemFacts(TcpTweaks: null)).Passed);
        Assert.DoesNotContain(CheckArea.TcpIpSettings, Review(new SystemFacts(TcpTweaks: ["SackOpts=0"])).Passed);
    }

    [Fact]
    public void Everything_the_advisor_knows_about_a_healthy_pc_is_reported_fine_and_nothing_else()
    {
        var healthy = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 866, SignalBars: 4, Name: WifiName, SignalDbm: -58, WifiChannel: 100,
            WifiRetryPercent: 4, WifiFrames: 50_000, WifiEncryption: "Ccmp", Mtu: 1500, PacketErrors: 2, Packets: 900_000, Alias: "Wi-Fi",
            Settings: [Mode("3"), Width5("1"), Mimo("0"), Power("100"), Band("4"), Roaming("2")]), NetworkName: Network, TcpTweaks: []);
        var result = Review(healthy, Symptom.Gaming);
        Assert.Empty(result.Advice);
        Assert.Equal(new[] { CheckArea.WifiSignal, CheckArea.LinkSpeed, CheckArea.PowerSaving, CheckArea.WifiSecurity, CheckArea.WifiRetries,
            CheckArea.DriverSettings, CheckArea.TcpIpSettings, CheckArea.AdapterErrors, CheckArea.Mtu, CheckArea.Proxy, CheckArea.Vpn,
            CheckArea.MeteredConnection, CheckArea.Ipv6 }.Order(), result.Passed.Order());
    }

    // Helpers ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_advisory_belongs_to_an_area_so_a_flagged_area_is_never_also_listed_as_fine()
    {
        Assert.All(Enum.GetValues<AdvisoryCode>(), code => Assert.True(Enum.IsDefined(ConfigurationAdvisor.AreaOf(code)), code.ToString()));
        Assert.Equal(CheckArea.WifiRetries, ConfigurationAdvisor.AreaOf(WifiRetries));
        Assert.Equal(CheckArea.WifiSecurity, ConfigurationAdvisor.AreaOf(WifiInsecureCipher));
        Assert.Equal(CheckArea.LinkSpeed, ConfigurationAdvisor.AreaOf(EthernetSpeedForced));
        Assert.Equal(CheckArea.DriverSettings, ConfigurationAdvisor.AreaOf(JumboFrames));
        Assert.Equal(CheckArea.DriverSettings, ConfigurationAdvisor.AreaOf(WifiDriverLimit));
        Assert.Equal(CheckArea.DriverSettings, ConfigurationAdvisor.AreaOf(WifiRoaming));
        Assert.Equal(CheckArea.TcpIpSettings, ConfigurationAdvisor.AreaOf(TcpTweaks));
        Assert.Equal(CheckArea.AdapterErrors, ConfigurationAdvisor.AreaOf(AdapterErrors));
        Assert.Equal(CheckArea.Mtu, ConfigurationAdvisor.AreaOf(MtuUnusual));
        Assert.Equal(CheckArea.Ipv6, ConfigurationAdvisor.AreaOf(Ipv6Disabled));
    }

    [Fact]
    public void The_drivers_own_description_picks_half_duplex_and_slow_forced_speeds()
    {
        Assert.True(DriverRules.IsHalfDuplex(Duplex("1")));
        Assert.True(DriverRules.IsHalfDuplex(Duplex("3")));
        Assert.False(DriverRules.IsHalfDuplex(Duplex("4")));
        Assert.True(DriverRules.IsHalfDuplex(Setting("*SpeedDuplex", "Speed", "9", "0", ("9", "1 Gbps Half Duplex"))));
        Assert.True(DriverRules.IsForcedSlow(Duplex("2")));
        Assert.True(DriverRules.IsForcedSlow(Duplex("4")));
        Assert.False(DriverRules.IsForcedSlow(Duplex("6")));
        Assert.True(DriverRules.IsForcedSlow(Setting("*SpeedDuplex", "Speed", "7", "0", ("7", "100Mbps Full"))));
        Assert.False(DriverRules.IsForcedSlow(Setting("*SpeedDuplex", "Speed", "8", "0", ("8", "2.5 Gbps Full Duplex"))));
    }

    [Fact]
    public void Options_are_found_by_value_and_ordered_by_number()
    {
        var roaming = Roaming("4");
        Assert.Equal("1. Lowest", roaming.LowestOptionText);
        Assert.Equal("3. Medium", roaming.OptionText("2"));
        Assert.Null(roaming.OptionText("9"));
        Assert.Equal((4, 2), (roaming.Number, roaming.DefaultNumber));
        Assert.Null(Setting("X", "Name", "text", "other").Number);
        Assert.Null(new DriverSetting("X", "Name", "1", "1").LowestOptionText);
    }
}
