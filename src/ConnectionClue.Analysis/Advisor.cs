using System.Globalization;

namespace ConnectionClue.Analysis;

public enum Symptom { Gaming, Video, Calls, Disconnects }

/// <summary>Configuration best practices the user can apply; the app only reads settings and never changes them.</summary>
public enum AdvisoryCode
{
    LatencyUnderLoad, OtherTraffic, WeakWifiSignal, TcpAutoTuningLimited, EthernetLinkSlow, WifiLinkSlow, AdapterPowerSaving,
    UsbSelectiveSuspend, EnergyEfficientEthernet, PowerSaving, SlowDns, OldNetworkDriver, MeteredConnection, ProxyConfigured,
    VpnActive, Ipv6Disabled,
}

public enum AdvisoryLevel { Suggestion, Important }

/// <summary>What the advisor checks; an area is listed as "no change needed" when its setting or measurement is already good.</summary>
public enum CheckArea
{
    WifiSignal, LinkSpeed, NetworkDriver, AdapterPower, UsbPower, EnergyEfficientEthernet, TcpTuning, PowerSaving, NameLookups,
    LatencyUnderLoad, OtherTraffic, Proxy, Vpn, MeteredConnection, Ipv6,
}

/// <summary>
/// One piece of advice with its evidence. Value and Value2 are the numbers in the text (Mbps, ms, bars, or yyyymm for a
/// driver date). Args are names from this PC (adapter, network, app, DNS server, proxy); an arg starting with "#" is an
/// invariant number and one starting with "@" is a resource key, both localized when shown. Variant picks the specific
/// instructions (for example which app category, or which power setting). Stored as codes, so it re-renders in any language.
/// </summary>
public sealed record Advisory(AdvisoryCode Code, AdvisoryLevel Level, double Value = 0, double Value2 = 0,
    IReadOnlyList<string>? Args = null, string? Variant = null);

/// <summary>Active adapter facts. Null means unknown (not readable), which never produces advice.</summary>
public sealed record AdapterFacts(
    ConnectionMedium Medium, double LinkMbps, DateOnly? DriverDate = null, bool? PowerOffAllowed = null, bool? EnergyEfficientEthernet = null,
    int? SignalBars = null, bool IsUsb = false, bool? UsbSelectiveSuspend = null, string? Name = null, string? DriverVersion = null,
    string? DriverVendor = null);

/// <summary>Read-only snapshot of network-relevant OS, driver and power settings, with the names advice needs.</summary>
public sealed record SystemFacts(
    AdapterFacts? Adapter = null,
    bool Metered = false,
    bool ProxyConfigured = false,
    bool TunnelSuspected = false,
    bool Ipv6Disabled = false,
    bool? TcpAutoTuningLimited = null,
    bool OnBattery = false,
    bool PowerSaverActive = false,
    int? WifiPowerSavingOnBattery = null,
    int? WifiPowerSavingOnAc = null,
    string? NetworkName = null,
    string? DnsServer = null,
    bool DnsIsRouter = false,
    string? ProxyAddress = null,
    bool EnergySaverOn = false,
    bool BestEfficiency = false,
    int? TcpAutoTuningLevel = null,
    int? Ipv6DisabledComponents = null,
    bool Ipv6UnboundOnAdapter = false,
    string? TunnelName = null);

public enum AppKind { Windows, OneDrive, Sync, GameLauncher, Browser, Other }

/// <summary>One app's traffic around the check. FileName is the executable (empty for Windows system services).</summary>
public sealed record AppTraffic(string Name, string FileName, double SentMb, double ReceivedMb)
{
    public double TotalMb => SentMb + ReceivedMb;
}

/// <summary>Known apps that commonly use the network in the background, so advice can say how to pause them.</summary>
public static class AppCatalog
{
    private static readonly Dictionary<string, AppKind> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = AppKind.Windows, ["svchost.exe"] = AppKind.Windows, ["backgroundtransferhost.exe"] = AppKind.Windows,
        ["onedrive.exe"] = AppKind.OneDrive,
        ["dropbox.exe"] = AppKind.Sync, ["googledrivefs.exe"] = AppKind.Sync, ["icloudservices.exe"] = AppKind.Sync, ["iclouddrive.exe"] = AppKind.Sync,
        ["box.exe"] = AppKind.Sync, ["megasync.exe"] = AppKind.Sync, ["nextcloud.exe"] = AppKind.Sync, ["syncthing.exe"] = AppKind.Sync,
        ["bztransmit64.exe"] = AppKind.Sync, ["pcloud.exe"] = AppKind.Sync,
        ["steam.exe"] = AppKind.GameLauncher, ["steamservice.exe"] = AppKind.GameLauncher, ["epicgameslauncher.exe"] = AppKind.GameLauncher,
        ["battle.net.exe"] = AppKind.GameLauncher, ["eadesktop.exe"] = AppKind.GameLauncher, ["upc.exe"] = AppKind.GameLauncher,
        ["ubisoftconnect.exe"] = AppKind.GameLauncher, ["gamingservices.exe"] = AppKind.GameLauncher, ["xboxpcapp.exe"] = AppKind.GameLauncher,
        ["galaxyclient.exe"] = AppKind.GameLauncher, ["riotclientservices.exe"] = AppKind.GameLauncher,
        ["msedge.exe"] = AppKind.Browser, ["chrome.exe"] = AppKind.Browser, ["firefox.exe"] = AppKind.Browser, ["opera.exe"] = AppKind.Browser,
        ["brave.exe"] = AppKind.Browser, ["vivaldi.exe"] = AppKind.Browser, ["arc.exe"] = AppKind.Browser,
    };

    public static AppKind Classify(string fileName) => Known.GetValueOrDefault(fileName, AppKind.Other);
}

/// <summary>
/// Inputs for one review. OtherTrafficMbps is this PC's own traffic during the idle phase (interface counters; the check's
/// probes add well under 0.1 Mbps). Apps attributes it. Download/UploadMbps and the loaded latencies come from the speed test.
/// </summary>
public sealed record AdvisorInput(
    SystemFacts Facts, Symptom? Symptom, HealthReport Report, double? DnsMedianMs, double? IdleLatencyMs, double? LoadedLatencyMs,
    DateOnly Today, (double Down, double Up)? OtherTrafficMbps = null, IReadOnlyList<AppTraffic>? Apps = null,
    double? DownloadMbps = null, double? UploadMbps = null, double? LoadedDownMs = null, double? LoadedUpMs = null);

public sealed record AdvisorResult(IReadOnlyList<Advisory> Advice, IReadOnlyList<CheckArea> Passed);

/// <summary>
/// Turns facts and measurements into specific advice: each item names what was found on this PC (app, adapter, network,
/// server, setting value) and picks instructions for that exact situation. Symptom-specific advice appears only when the
/// user's symptom or a detected issue makes it relevant, so healthy PCs are not flooded. Important items come first.
/// Thresholds are proposed defaults pending lab calibration.
/// </summary>
public static class ConfigurationAdvisor
{
    // 150 Mbps is just above the 2.4 GHz ceiling (144 Mbps, 802.11n 2x2 at 20 MHz): a slower link usually means 2.4 GHz or a weak 5 GHz signal.
    public const double SlowWifiMbps = 150, SlowWifiRealtimeMbps = 100, VerySlowWifiMbps = 50, SlowEthernetMbps = 100;
    public const double SlowDnsMs = 150, LoadedRiseMs = 100, OtherDownMbps = 5, OtherUpMbps = 1, MinAppMb = 0.5;
    public const int WeakSignalBars = 2, OldDriverDays = 730; // bars of 5

    public static AdvisorResult Review(AdvisorInput input)
    {
        var advice = Evaluate(input);
        return new(advice, Passed(input, advice));
    }

    public static IReadOnlyList<Advisory> Evaluate(AdvisorInput input)
    {
        var f = input.Facts;
        var a = f.Adapter;
        var list = new List<Advisory>();
        bool realtime = input.Symptom is Symptom.Gaming or Symptom.Calls;
        bool localIssue = input.Report.Issues.Any(i => i.Location == IssueLocation.LocalNetwork);
        bool Has(params IssueKind[] kinds) => input.Report.Issues.Any(i => kinds.Contains(i.Kind));
        void Add(AdvisoryCode code, bool important, double value = 0, double value2 = 0, IReadOnlyList<string>? args = null, string? variant = null) =>
            list.Add(new(code, important ? AdvisoryLevel.Important : AdvisoryLevel.Suggestion, value, value2, args, variant));

        if (input.IdleLatencyMs is { } idle && input.LoadedLatencyMs is { } loaded && loaded - idle >= LoadedRiseMs)
        {
            // Name the direction that hurts and give the router a concrete limit: 90% of what was measured.
            bool upload = input.LoadedUpMs is { } up && (input.LoadedDownMs is not { } down || up > down);
            double? speed = upload ? input.UploadMbps : input.DownloadMbps;
            Add(AdvisoryCode.LatencyUnderLoad, true, Math.Round(loaded - idle), speed is { } s ? Math.Round(s * 0.9, s < 10 ? 1 : 0) : 0,
                variant: speed is null ? null : upload ? "Upload" : "Download");
        }
        if (input.OtherTrafficMbps is { } t && (t.Down >= OtherDownMbps || t.Up >= OtherUpMbps))
        {
            // Name the app that caused the direction that triggered: an upload problem is blamed on the biggest sender.
            bool up = t.Up >= OtherUpMbps, down = t.Down >= OtherDownMbps;
            Func<AppTraffic, double> share = up && !down ? x => x.SentMb : down && !up ? x => x.ReceivedMb : x => x.TotalMb;
            var apps = (input.Apps ?? []).Where(x => share(x) >= MinAppMb).OrderByDescending(share).Take(2).ToList();
            IReadOnlyList<string>? args = apps.Count == 0 ? null : [.. apps.SelectMany(x => new[] { AppName(x), Number(share(x)) })];
            string? variant = apps.Count == 0 ? null : AppCatalog.Classify(apps[0].FileName) switch
            {
                AppKind.Windows => "Windows",
                AppKind.OneDrive => "OneDrive",
                AppKind.Sync => "Sync",
                AppKind.GameLauncher => "Game",
                AppKind.Browser => "Browser",
                _ => "App",
            };
            Add(AdvisoryCode.OtherTraffic, realtime || Has(IssueKind.Delay, IssueKind.Variation, IssueKind.Loss),
                Math.Round(t.Down, 1), Math.Round(t.Up, 1), args, variant);
        }
        if (f.TcpAutoTuningLimited == true)
            Add(AdvisoryCode.TcpAutoTuningLimited, true, args: Names(f.TcpAutoTuningLevel switch { 0 => "disabled", 1 => "highlyrestricted", 2 => "restricted", _ => null }));
        if (a is { Medium: ConnectionMedium.Ethernet, LinkMbps: > 0 and <= SlowEthernetMbps })
            Add(AdvisoryCode.EthernetLinkSlow, true, a.LinkMbps, args: Names(a.Name));
        bool weak = a is { Medium: ConnectionMedium.WiFi, SignalBars: <= WeakSignalBars };
        if (weak)
            Add(AdvisoryCode.WeakWifiSignal, realtime || localIssue || input.Symptom == Symptom.Disconnects, a!.SignalBars!.Value,
                Math.Round(a.LinkMbps), Names(f.NetworkName, a.Name));
        // A weak signal already explains a slow link; one piece of advice is enough.
        if (!weak && a is { Medium: ConnectionMedium.WiFi, LinkMbps: > 0 and < SlowWifiMbps })
        {
            var standard = WifiStandard(a.Name);
            var names = Names(f.NetworkName, a.Name);
            Add(AdvisoryCode.WifiLinkSlow, localIssue || a.LinkMbps < VerySlowWifiMbps
                    || (realtime || input.Symptom == Symptom.Video) && a.LinkMbps < SlowWifiRealtimeMbps, Math.Round(a.LinkMbps),
                args: names is null ? null : [.. names, standard?.Label ?? ""],
                variant: names is null || standard is null ? null : standard.Value.FiveGhz ? "Capable" : "Legacy");
        }
        if (a?.PowerOffAllowed == true && (input.Symptom == Symptom.Disconnects || Has(IssueKind.Interrupted)))
            Add(AdvisoryCode.AdapterPowerSaving, true, args: Names(a.Name));
        if (a is { IsUsb: true, UsbSelectiveSuspend: true } && (input.Symptom == Symptom.Disconnects || Has(IssueKind.Interrupted)))
            Add(AdvisoryCode.UsbSelectiveSuspend, true, args: Names(a.Name), variant: f.OnBattery ? "Battery" : "Plugged");
        if (a is { Medium: ConnectionMedium.Ethernet, EnergyEfficientEthernet: true } && (realtime || Has(IssueKind.Delay, IssueKind.Variation)))
            Add(AdvisoryCode.EnergyEfficientEthernet, true, args: Names(a.Name));
        if (PowerLimited(f) is { } power)
            Add(AdvisoryCode.PowerSaving, realtime || input.Report.Issues.Count > 0, power.Level, args: power.Args, variant: power.Variant);
        if (input.DnsMedianMs is { } dns && dns > SlowDnsMs)
            Add(AdvisoryCode.SlowDns, input.Symptom == Symptom.Video || Has(IssueKind.WebUnreachable), Math.Round(dns),
                args: Names(f.DnsServer), variant: f.DnsServer is null ? null : f.DnsIsRouter ? "Router" : "Other");
        if (a?.DriverDate is { } date && input.Today.DayNumber - date.DayNumber > OldDriverDays)
            Add(AdvisoryCode.OldNetworkDriver, false, date.Year * 100 + date.Month, args: Names(a.Name, a.DriverVersion),
                variant: a.DriverVendor?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true ? "Intel" : null);
        if (f.Metered && a?.Medium is ConnectionMedium.WiFi or ConnectionMedium.Ethernet)
            Add(AdvisoryCode.MeteredConnection, false, args: Names(f.NetworkName));
        if (f.ProxyConfigured)
            Add(AdvisoryCode.ProxyConfigured, false, args: Names(f.ProxyAddress), variant: f.ProxyAddress is null ? null : IsLocal(f.ProxyAddress) ? "Local" : "Remote");
        // With issues, the planner already suggests trying without the VPN.
        if (f.TunnelSuspected && realtime && input.Report.Issues.Count == 0) Add(AdvisoryCode.VpnActive, false, args: Names(f.TunnelName));
        if (f.Ipv6Disabled)
            Add(AdvisoryCode.Ipv6Disabled, false,
                args: f.Ipv6DisabledComponents is { } flags ? [string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", flags)] : f.Ipv6UnboundOnAdapter ? Names(a?.Name) : null,
                variant: f.Ipv6DisabledComponents is not null ? "Registry" : f.Ipv6UnboundOnAdapter && a?.Name is not null ? "Adapter" : null);

        return [.. list.OrderByDescending(x => x.Level).ThenBy(x => x.Code)];
    }

    /// <summary>The area each advisory belongs to, so a flagged area is never also listed as fine.</summary>
    public static CheckArea AreaOf(AdvisoryCode code) => code switch
    {
        AdvisoryCode.LatencyUnderLoad => CheckArea.LatencyUnderLoad,
        AdvisoryCode.OtherTraffic => CheckArea.OtherTraffic,
        AdvisoryCode.WeakWifiSignal => CheckArea.WifiSignal,
        AdvisoryCode.TcpAutoTuningLimited => CheckArea.TcpTuning,
        AdvisoryCode.EthernetLinkSlow or AdvisoryCode.WifiLinkSlow => CheckArea.LinkSpeed,
        AdvisoryCode.AdapterPowerSaving => CheckArea.AdapterPower,
        AdvisoryCode.UsbSelectiveSuspend => CheckArea.UsbPower,
        AdvisoryCode.EnergyEfficientEthernet => CheckArea.EnergyEfficientEthernet,
        AdvisoryCode.PowerSaving => CheckArea.PowerSaving,
        AdvisoryCode.SlowDns => CheckArea.NameLookups,
        AdvisoryCode.OldNetworkDriver => CheckArea.NetworkDriver,
        AdvisoryCode.MeteredConnection => CheckArea.MeteredConnection,
        AdvisoryCode.ProxyConfigured => CheckArea.Proxy,
        AdvisoryCode.VpnActive => CheckArea.Vpn,
        _ => CheckArea.Ipv6,
    };

    /// <summary>Wi-Fi standard from the adapter name, and whether it can use 5 GHz or 6 GHz; null when the name does not say.</summary>
    public static (string Label, bool FiveGhz)? WifiStandard(string? adapter)
    {
        if (adapter is null) return null;
        string n = adapter.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (n.Contains("wifi7") || n.Contains("802.11be") || n.Contains("be200")) return ("Wi-Fi 7", true);
        if (n.Contains("wifi6e") || n.Contains("ax210") || n.Contains("ax211") || n.Contains("ax411")) return ("Wi-Fi 6E", true);
        if (n.Contains("wifi6") || n.Contains("802.11ax") || n.Contains("ax200") || n.Contains("ax201") || n.Contains("ax101")) return ("Wi-Fi 6", true);
        if (n.Contains("wifi5") || n.Contains("802.11ac") || n.Contains("wirelessac")) return ("Wi-Fi 5", true);
        if (n.Contains("802.11n") || n.Contains("802.11g") || System.Text.RegularExpressions.Regex.IsMatch(adapter, @"\bWireless-N\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return ("Wi-Fi 4", false);
        return null;
    }

    private static string AppName(AppTraffic app) => AppCatalog.Classify(app.FileName) == AppKind.Windows
        ? "@App_Windows" : app.Name.Length > 0 ? app.Name : app.FileName;

    private static string Number(double value) => "#" + Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>All names, or null when any is unknown (the text then falls back to the generic wording).</summary>
    private static IReadOnlyList<string>? Names(params string?[] values) =>
        values.All(v => !string.IsNullOrWhiteSpace(v)) ? [.. values.Select(v => v!.Trim())] : null;

    private static bool IsLocal(string address) =>
        address.StartsWith("127.", StringComparison.Ordinal) || address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
        || address.StartsWith("[::1]", StringComparison.Ordinal);

    /// <summary>Which power setting limits the connection, most specific first; Level is the Wi-Fi power-saving level.</summary>
    private static (string? Variant, int Level, IReadOnlyList<string>? Args)? PowerLimited(SystemFacts f)
    {
        if (f.EnergySaverOn) return ("EnergySaver", 0, null);
        if (f.BestEfficiency) return ("Efficiency", 0, null);
        if (f.Adapter?.Medium == ConnectionMedium.WiFi && (f.OnBattery ? f.WifiPowerSavingOnBattery : f.WifiPowerSavingOnAc) is >= 2 and var level)
            return (f.OnBattery ? "WifiBattery" : "WifiPlugged", level, [$"@WifiPower_{level}"]);
        return f.PowerSaverActive ? (null, 0, null) : null;
    }

    /// <summary>Areas whose value is already the recommended one. Unknown values and values that are merely not relevant
    /// to the symptom (for example adapter power saving without disconnects) are left out rather than called fine.</summary>
    private static List<CheckArea> Passed(AdvisorInput input, IReadOnlyList<Advisory> advice)
    {
        var f = input.Facts;
        var a = f.Adapter;
        var flagged = advice.Select(x => AreaOf(x.Code)).ToHashSet();
        var passed = new List<CheckArea>();
        void Ok(CheckArea area, bool good)
        {
            if (good && !flagged.Contains(area)) passed.Add(area);
        }

        if (a is { Medium: ConnectionMedium.WiFi, SignalBars: { } bars }) Ok(CheckArea.WifiSignal, bars > WeakSignalBars);
        if (a is { LinkMbps: > 0 }) Ok(CheckArea.LinkSpeed, a.Medium == ConnectionMedium.WiFi ? a.LinkMbps >= SlowWifiMbps : a.LinkMbps > SlowEthernetMbps);
        if (a?.DriverDate is { } date) Ok(CheckArea.NetworkDriver, input.Today.DayNumber - date.DayNumber <= OldDriverDays);
        if (a?.PowerOffAllowed is { } off) Ok(CheckArea.AdapterPower, !off);
        if (a is { IsUsb: true, UsbSelectiveSuspend: { } usb }) Ok(CheckArea.UsbPower, !usb);
        if (a is { Medium: ConnectionMedium.Ethernet, EnergyEfficientEthernet: { } eee }) Ok(CheckArea.EnergyEfficientEthernet, !eee);
        if (f.TcpAutoTuningLimited is { } tcp) Ok(CheckArea.TcpTuning, !tcp);
        Ok(CheckArea.PowerSaving, PowerLimited(f) is null);
        if (input.DnsMedianMs is { } dns) Ok(CheckArea.NameLookups, dns <= SlowDnsMs);
        if (input.IdleLatencyMs is { } idle && input.LoadedLatencyMs is { } loaded) Ok(CheckArea.LatencyUnderLoad, loaded - idle < LoadedRiseMs);
        if (input.OtherTrafficMbps is { } t) Ok(CheckArea.OtherTraffic, t.Down < OtherDownMbps && t.Up < OtherUpMbps);
        Ok(CheckArea.Proxy, !f.ProxyConfigured);
        Ok(CheckArea.Vpn, !f.TunnelSuspected);
        Ok(CheckArea.MeteredConnection, !f.Metered);
        Ok(CheckArea.Ipv6, !f.Ipv6Disabled);
        return passed;
    }
}
