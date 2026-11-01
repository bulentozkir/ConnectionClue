using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ConnectionClue.Core;
using Microsoft.Win32;
using WinRt = global::Windows.Networking.Connectivity;

namespace ConnectionClue.Windows;

/// <summary>Raw facts about the active adapter and OS settings. Null or zero means unknown and never produces advice.
/// Names (adapter, network, DNS server, proxy, VPN) and exact setting values let advice say precisely what to change.</summary>
public sealed record SystemSnapshot(
    InterfaceMedium? Medium = null,
    double LinkMbps = 0,
    int? SignalBars = null,
    DateOnly? DriverDate = null,
    bool? PowerOffAllowed = null,
    bool? EnergyEfficientEthernet = null,
    bool Ipv6Disabled = false,
    bool? TcpAutoTuningLimited = null,
    bool OnBattery = false,
    bool PowerSaverActive = false,
    int? WifiPowerSavingOnBattery = null,
    bool ProxyConfigured = false,
    int? WifiPowerSavingOnAc = null,
    bool IsUsb = false,
    bool? UsbSelectiveSuspend = null,
    string? AdapterName = null,
    string? DriverVersion = null,
    string? DriverProvider = null,
    string? NetworkName = null,
    IReadOnlyList<string>? DnsServers = null,
    string? ProxyAddress = null,
    bool EnergySaverOn = false,
    bool BestEfficiencyMode = false,
    int? TcpAutoTuningLevel = null,
    int? Ipv6DisabledComponents = null,
    bool Ipv6UnboundOnAdapter = false,
    string? TunnelName = null,
    int? SignalDbm = null,
    int? WifiChannel = null,
    double? WifiRetryPercent = null,
    long WifiFrames = 0,
    string? WifiEncryption = null,
    IReadOnlyList<DriverSettingValue>? DriverSettings = null,
    int? Mtu = null,
    long PacketErrors = 0,
    long Packets = 0,
    string? InterfaceAlias = null,
    IReadOnlyList<string>? TcpTweaks = null,
    NetworkConfiguration? Configuration = null);

/// <summary>
/// Reads network-relevant driver, OS and power settings as a standard user. Strictly read-only: it never changes a
/// setting. Each read is independent and best-effort, so one blocked source does not hide the others.
/// </summary>
public static unsafe partial class SystemInspector
{
    private const string AdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string Tcpip6Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
    private const string TcpipKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    private static readonly Guid WirelessSubgroup = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid WirelessPowerSaving = new("12bbebe6-58d6-4636-95bb-3217ef867c1a");
    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid BestEfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Uri ProxyProbe = new("https://www.example.com/");
    private static readonly string[] EeeKeywords = ["*EEE", "EEE", "EEELinkAdvertisement"];

    // This setting rarely changes: scheduled checks reuse it for an hour instead of waking WMI every minute.
    private static readonly CachedValue<int?> AutoTuning = new(TimeSpan.FromHours(1));

    // The adapter's advanced settings take about 20 ms of CPU to read and change only when someone opens Device Manager; the
    // held copy belongs to one adapter, so a switch from Wi-Fi to a cable reads the new one at once.
    private static readonly CachedValue<(Guid Adapter, IReadOnlyList<DriverSettingValue>? Settings)> DriverSettingsHeld = new(TimeSpan.FromHours(1));
    private static readonly CachedValue<(Guid Adapter, NetworkConfiguration Configuration)> ConfigurationHeld = new(TimeSpan.FromHours(1));

    /// <summary>reuseSlowFacts lets a scheduled check reuse the facts that are slow to read and rarely change; a check the
    /// user starts always reads them fresh.</summary>
    public static SystemSnapshot Inspect(PathContext? path, bool reuseSlowFacts = false)
    {
        var components = Try(Ipv6DisabledComponents);
        var level = AutoTuning.Get(() => Try(AutoTuningLevel), reuseSlowFacts);
        var proxy = Try(ProxyAddressInUse);
        var snapshot = new SystemSnapshot(Ipv6Disabled: components is not null, Ipv6DisabledComponents: components,
            TcpAutoTuningLevel: level, TcpAutoTuningLimited: level is null ? null : level <= 2, ProxyConfigured: proxy is not null, ProxyAddress: proxy,
            TcpTweaks: Try(ReadTcpTweaks));
        snapshot = Try(() => WithPower(snapshot), snapshot);
        if (path is null) return snapshot;

        var row = new MIB_IF_ROW2 { InterfaceLuid = new NET_LUID_LH { Value = path.InterfaceLuid } };
        if (PInvoke.GetIfEntry2(&row) != WIN32_ERROR.NO_ERROR) return snapshot;
        var id = row.InterfaceGuid;
        var nic = Try(() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == id));
        var profile = Try(() => ConnectedProfile(id));
        snapshot = snapshot with
        {
            NetworkName = Try(() => profile?.ProfileName),
            DnsServers = Try(() => nic?.GetIPProperties().DnsAddresses.Where(a => !a.IsIPv6LinkLocal).Select(a => a.ToString()).ToList()),
            TunnelName = path.TunnelSuspected ? nic?.Description : null,
        };
        int interfaceIndex = (int)row.InterfaceIndex;
        (Guid Adapter, NetworkConfiguration Configuration) ReadConfiguration() =>
            (id, NetworkConfigurationReader.Read(interfaceIndex, nic?.Description));
        var configuration = ConfigurationHeld.Get(ReadConfiguration, reuseSlowFacts);
        if (configuration.Adapter != id) configuration = ConfigurationHeld.Get(ReadConfiguration, reuse: false);
        snapshot = snapshot with { Configuration = NetworkConfigurationReader.WithAddressing(configuration.Configuration, nic) };
        if (path is not { IsHardware: true, Medium: InterfaceMedium.Ethernet or InterfaceMedium.WiFi }) return snapshot;

        bool unbound = nic is not null && Try(() => !nic.Supports(NetworkInterfaceComponent.IPv6));
        snapshot = snapshot with
        {
            Medium = path.Medium,
            AdapterName = nic?.Description,
            InterfaceAlias = nic?.Name,
            LinkMbps = Math.Min(row.ReceiveLinkSpeed, row.TransmitLinkSpeed) / 1e6,
            SignalBars = path.Medium == InterfaceMedium.WiFi ? Try(() => SignalBars(profile)) : null,
            Mtu = row.Mtu is > 0 and < 100_000 ? (int)row.Mtu : null,
            PacketErrors = Clamp(row.InErrors + row.OutErrors),
            Packets = Clamp(row.InUcastPkts + row.InNUcastPkts + row.OutUcastPkts + row.OutNUcastPkts),
            Ipv6UnboundOnAdapter = unbound,
            Ipv6Disabled = snapshot.Ipv6Disabled || unbound,
        };
        if (path.Medium == InterfaceMedium.WiFi) snapshot = Try(() => WithWifi(snapshot, id, profile), snapshot);
        return Try(() => WithDriver(snapshot, id, reuseSlowFacts), snapshot);
    }

    private static long Clamp(ulong count) => (long)Math.Min(count, long.MaxValue);

    private static T Try<T>(Func<T> read, T fallback = default!)
    {
        try { return read(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return fallback; }
    }

    /// <summary>The DisabledComponents value when it turns IPv6 off (0x10: all non-tunnel interfaces). 0x20 (prefer IPv4)
    /// is a supported setting, not a problem.</summary>
    private static int? Ipv6DisabledComponents()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Tcpip6Key);
        return key?.GetValue("DisabledComponents") is int flags && (flags & 0x10) != 0 ? flags : null;
    }

    /// <summary>Lowest MSFT_NetTCPSetting.AutoTuningLevelLocal of the internet templates: 0 Disabled, 1 HighlyRestricted,
    /// 2 Restricted, 3 Normal, 4 Experimental.</summary>
    private static int? AutoTuningLevel()
    {
        using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
            "SELECT SettingName, AutoTuningLevelLocal FROM MSFT_NetTCPSetting");
        searcher.Options.Timeout = TimeSpan.FromSeconds(5);
        int? lowest = null;
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                if (item["SettingName"] is not ("Internet" or "InternetCustom") || item["AutoTuningLevelLocal"] is not { } level) continue;
                lowest = Math.Min(lowest ?? int.MaxValue, Convert.ToInt32(level, CultureInfo.InvariantCulture));
            }
        }
        return lowest;
    }

    // The Windows proxy's IsBypassed is always false; GetProxy returns null (or the target itself) when no proxy applies.
    private static string? ProxyAddressInUse() =>
        HttpClient.DefaultProxy.GetProxy(ProxyProbe) is { } proxy && proxy != ProxyProbe ? proxy.Authority : null;

    // Saved but disconnected profiles share the adapter (0 bars, stale names); only the connected one counts. The profile Windows
    // uses for the internet is one cheap call (about a tenth of the CPU of listing every profile) and is the one on the default
    // route in practice; any other case falls back to the full list.
    private static WinRt.ConnectionProfile? ConnectedProfile(Guid id) =>
        WinRt.NetworkInformation.GetInternetConnectionProfile() is { NetworkAdapter: { } adapter } internet && adapter.NetworkAdapterId == id
            ? internet
            : WinRt.NetworkInformation.GetConnectionProfiles().FirstOrDefault(p => p.NetworkAdapter?.NetworkAdapterId == id
                && p.GetNetworkConnectivityLevel() != WinRt.NetworkConnectivityLevel.None);

    private static int? SignalBars(WinRt.ConnectionProfile? profile) => profile is { IsWlanConnectionProfile: true } ? profile.GetSignalBars() : null;

    /// <summary>The cipher Windows negotiated (from the connection profile, which needs no Location permission) and what the radio
    /// reports about the link: channel, signal in dBm and how many frames needed another attempt.</summary>
    private static SystemSnapshot WithWifi(SystemSnapshot snapshot, Guid id, WinRt.ConnectionProfile? profile)
    {
        var link = Try(() => WlanInfo.ReadLink(id));
        return snapshot with
        {
            WifiEncryption = Try(() => profile?.NetworkSecuritySettings?.NetworkEncryptionType.ToString()),
            WifiChannel = link?.Channel,
            SignalDbm = link?.SignalDbm,
            WifiFrames = link?.Frames?.Sent ?? 0,
            WifiRetryPercent = link?.Frames is { Sent: > 0 } counts ? 100.0 * counts.Retried / counts.Sent : null,
        };
    }

    /// <summary>Values that old "speed up your internet" guides leave behind and that make TCP slower or less reliable. Null when
    /// the registry key cannot be read; empty when nothing is set to such a value.</summary>
    private static IReadOnlyList<string>? ReadTcpTweaks()
    {
        using var key = Registry.LocalMachine.OpenSubKey(TcpipKey);
        return key is null ? null : FindTcpTweaks(key.GetValue);
    }

    /// <summary>"Name=value" for each limiting value: path-MTU discovery off (every remote connection falls back to 576-byte
    /// packets), RFC 1323 window scaling off (bit 0 of Tcp1323Opts; speed is capped near 64 KB per round trip), selective
    /// acknowledgements off, and task offload off. Windows' own defaults, and values merely present, are not reported.</summary>
    public static IReadOnlyList<string> FindTcpTweaks(Func<string, object?> read)
    {
        var found = new List<string>();
        void Check(string name, Func<long, bool> limiting)
        {
            if (read(name) is { } value && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && limiting(n))
                found.Add(string.Create(CultureInfo.InvariantCulture, $"{name}={n}"));
        }
        Check("EnablePMTUDiscovery", n => n == 0);
        Check("Tcp1323Opts", n => (n & 1) == 0);
        Check("SackOpts", n => n == 0);
        Check("DisableTaskOffload", n => n == 1);
        return found;
    }

    private static SystemSnapshot WithPower(SystemSnapshot snapshot)
    {
        // ACLineStatus 0 = on battery; SystemStatusFlag 1 = energy saver on.
        if (PInvoke.GetSystemPowerStatus(out var status))
            snapshot = snapshot with { OnBattery = status.ACLineStatus == 0, EnergySaverOn = status.SystemStatusFlag == 1 };
        if (PowerGetEffectiveOverlayScheme(out var overlay) == 0 && overlay == BestEfficiencyOverlay)
            snapshot = snapshot with { BestEfficiencyMode = true };
        snapshot = snapshot with { PowerSaverActive = snapshot.EnergySaverOn || snapshot.BestEfficiencyMode };

        Guid* scheme = null;
        if (PInvoke.PowerGetActiveScheme(default, &scheme) != WIN32_ERROR.NO_ERROR || scheme is null) return snapshot;
        try
        {
            // Wi-Fi: 0 Maximum performance, 1 Low, 2 Medium, 3 Maximum power saving. USB selective suspend: 1 = enabled.
            var usb = ReadPower(scheme, UsbSubgroup, UsbSelectiveSuspend, ac: !snapshot.OnBattery);
            return snapshot with
            {
                WifiPowerSavingOnBattery = ReadPower(scheme, WirelessSubgroup, WirelessPowerSaving, ac: false),
                WifiPowerSavingOnAc = ReadPower(scheme, WirelessSubgroup, WirelessPowerSaving, ac: true),
                UsbSelectiveSuspend = usb is null ? null : usb == 1,
            };
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)(nint)scheme);
        }
    }

    private static int? ReadPower(Guid* scheme, Guid subgroup, Guid setting, bool ac)
    {
        uint index;
        uint error = ac ? (uint)PInvoke.PowerReadACValueIndex(default, scheme, &subgroup, &setting, &index)
            : (uint)PInvoke.PowerReadDCValueIndex(default, scheme, &subgroup, &setting, &index);
        return error == 0 ? (int)index : null;
    }

    /// <summary>Bytes received and sent on an interface since boot; the difference over a window gives this PC's own traffic.</summary>
    public static (ulong Received, ulong Sent)? ReadCounters(ulong interfaceLuid)
    {
        var row = new MIB_IF_ROW2 { InterfaceLuid = new NET_LUID_LH { Value = interfaceLuid } };
        return PInvoke.GetIfEntry2(&row) == WIN32_ERROR.NO_ERROR ? (row.InOctets, row.OutOctets) : null;
    }

    private static SystemSnapshot WithDriver(SystemSnapshot snapshot, Guid id, bool reuseSlowFacts)
    {
        using var adapters = Registry.LocalMachine.OpenSubKey(AdapterClassKey);
        if (adapters is null) return snapshot;
        foreach (var name in adapters.GetSubKeyNames())
        {
            // Non-adapter subkeys such as "Properties" deny standard users; skip them.
            using var key = Try(() => adapters.OpenSubKey(name));
            if (key?.GetValue("NetCfgInstanceId") is not string cfg || !Guid.TryParse(cfg, out var g) || g != id) continue;

            var provider = key.GetValue("ProviderName") as string;
            // Windows-provided drivers update through Windows Update and often carry a fixed 2006 placeholder date.
            var inbox = provider?.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
            // PnPCapabilities bit 0x08 clears "Allow the computer to turn off this device to save power"; absent means allowed.
            var pnp = key.GetValue("PnPCapabilities") is int caps ? caps : 0;
            return snapshot with
            {
                DriverDate = inbox ? null : DriverDate(key),
                DriverVersion = key.GetValue("DriverVersion") as string,
                DriverProvider = provider,
                AdapterName = snapshot.AdapterName ?? key.GetValue("DriverDesc") as string,
                PowerOffAllowed = (pnp & 0x08) == 0,
                EnergyEfficientEthernet = snapshot.Medium == InterfaceMedium.Ethernet ? Eee(key) : null,
                IsUsb = key.GetValue("DeviceInstanceID") is string device && device.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase),
                DriverSettings = HeldDriverSettings(id, key, reuseSlowFacts),
            };
        }
        return snapshot;
    }

    private static IReadOnlyList<DriverSettingValue>? HeldDriverSettings(Guid adapter, RegistryKey key, bool reuse)
    {
        (Guid Adapter, IReadOnlyList<DriverSettingValue>? Settings) Read() => (adapter, Try(() => DriverSettingsReader.Read(new RegistryNode(key))));
        var held = DriverSettingsHeld.Get(Read, reuse);
        // Another adapter's copy, or a read that failed, is not worth holding for an hour.
        if (held.Adapter != adapter || held.Settings is null && reuse) held = DriverSettingsHeld.Get(Read, reuse: false);
        return held.Settings;
    }

    private static DateOnly? DriverDate(RegistryKey key)
    {
        if (key.GetValue("DriverDateData") is byte[] { Length: 8 } data)
            return DateOnly.FromDateTime(DateTime.FromFileTimeUtc(BitConverter.ToInt64(data)));
        return key.GetValue("DriverDate") is string text
            && DateTime.TryParseExact(text, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateOnly.FromDateTime(date)
            : null;
    }

    private static bool? Eee(RegistryKey key)
    {
        foreach (var keyword in EeeKeywords)
            if (key.GetValue(keyword) is { } value && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var on))
                return on != 0;
        return null;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);
}
