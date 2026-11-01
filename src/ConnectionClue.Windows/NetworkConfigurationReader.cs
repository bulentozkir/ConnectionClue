using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionClue.Core;
using Microsoft.Win32;

namespace ConnectionClue.Windows;

/// <summary>Inventories the readable properties of Windows networking CIM settings, not just properties with rules.
/// Queries are scoped to the measured adapter. A shared time budget prevents optional inventory delaying a check indefinitely.</summary>
public static class NetworkConfigurationReader
{
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);

    private static readonly string[] GlobalClasses =
    [
        "MSFT_NetOffloadGlobalSetting", "MSFT_NetTCPSetting", "MSFT_DNSClientGlobalSetting", "MSFT_NetFirewallProfile",
    ];

    private static readonly string[] InterfaceClasses =
    [
        "MSFT_NetIPInterface", "MSFT_NetIPAddress", "MSFT_NetRoute", "MSFT_DNSClient", "MSFT_DNSClientServerAddress",
        "MSFT_NetConnectionProfile",
    ];

    private static readonly string[] AdapterClasses =
    [
        "MSFT_NetAdapterRssSettingData", "MSFT_NetAdapterRscSettingData", "MSFT_NetAdapterChecksumOffloadSettingData",
        "MSFT_NetAdapterLsoSettingData", "MSFT_NetAdapterBindingSettingData",
    ];

    public static NetworkConfiguration WithAddressing(NetworkConfiguration inventory, NetworkInterface? nic)
    {
        bool? dhcp = null, staticDns = null, linkLocalOnly = null;
        try
        {
            if (nic is not null)
            {
                var ip = nic.GetIPProperties();
                dhcp = nic.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties().IsDhcpEnabled : null;
                linkLocalOnly = HasOnlyLinkLocalAddresses(ip.UnicastAddresses.Select(a => (a.Address, a.DuplicateAddressDetectionState)));
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}");
                staticDns = key is null ? null : key.GetValue("NameServer") switch
                {
                    null => false,
                    string value => !string.IsNullOrWhiteSpace(value),
                    _ => null,
                };
            }
        }
        catch (Exception e) when (e is NetworkInformationException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Trace.TraceWarning("Network configuration addressing read unavailable: {0}", e.GetType().Name);
            inventory = inventory with { Sources = [.. inventory.Sources, new("Addressing", [], e.GetType().Name)] };
        }
        return inventory with { DhcpEnabled = dhcp, StaticDns = staticDns, LinkLocalAddressOnly = linkLocalOnly };
    }

    public static NetworkConfiguration Read(int interfaceIndex, string? description) => ReadSources(interfaceIndex, description, Query);

    private static bool IsLinkLocalV4(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [169, 254, _, _];

    public static bool? HasOnlyLinkLocalAddresses(IEnumerable<(IPAddress Address, DuplicateAddressDetectionState State)> addresses)
    {
        var usable = addresses.Where(a => a.State is DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated)
            .Select(a => a.Address).Where(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)
                && !a.Equals(IPAddress.Broadcast) && !a.IsIPv6Multicast).ToList();
        if (usable.Count == 0) return null;
        bool linkLocalV4 = usable.Any(IsLinkLocalV4);
        bool routable = usable.Any(a =>
            a.AddressFamily == AddressFamily.InterNetwork ? !IsLinkLocalV4(a) :
            a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
        return !linkLocalV4 && !routable ? null : linkLocalV4 && !routable;
    }

    public static NetworkConfiguration ReadSources(int interfaceIndex, string? description,
        Func<string, TimeSpan, IReadOnlyList<IReadOnlyDictionary<string, string>>> query, TimeSpan? budget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interfaceIndex);
        ArgumentNullException.ThrowIfNull(query);
        var clock = Stopwatch.StartNew();
        var sources = new List<NetworkConfigurationSource>();
        if (string.IsNullOrWhiteSpace(description))
            sources.Add(new("AdapterSettings", [], "AdapterIdentityUnavailable"));
        var queries = GlobalClasses.Select(c => (Class: c, Filter: ""))
            .Concat(InterfaceClasses.Select(c => (Class: c, Filter: $" WHERE InterfaceIndex = {interfaceIndex.ToString(CultureInfo.InvariantCulture)}")))
            .Concat(string.IsNullOrWhiteSpace(description) ? [] : AdapterClasses.Select(c =>
                (Class: c, Filter: $" WHERE InterfaceDescription = '{description.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'")));
        foreach (var (name, filter) in queries)
        {
            var remaining = (budget ?? ReadBudget) - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                sources.Add(new(name, [], "TimeBudgetExceeded"));
                continue;
            }
            try
            {
                sources.Add(new(name, query($"SELECT * FROM {name}{filter}", remaining)));
            }
            catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException
                or UnauthorizedAccessException or System.Security.SecurityException or TimeoutException)
            {
                Trace.TraceWarning("Network configuration source {0} unavailable: {1}", name, e.GetType().Name);
                sources.Add(new(name, [], e.GetType().Name));
            }
        }
        if (sources.Any(s => s.Error == "TimeBudgetExceeded"))
            Trace.TraceWarning("Optional network configuration inventory exceeded its read budget.");
        return new(sources);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Query(string query, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        using var search = new ManagementObjectSearcher(@"root\StandardCimv2", query);
        search.Options.Timeout = timeout;
        search.Options.ReturnImmediately = true;
        using var results = search.Get();
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var item in results)
        {
            using (item)
            {
                if (clock.Elapsed >= timeout) throw new TimeoutException("Network configuration query exceeded its read budget.");
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties)
                    if (property.Value is { } value)
                        values[property.Name] = value is Array array
                            ? string.Join(", ", array.Cast<object>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))
                            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                rows.Add(values);
            }
        }
        return rows;
    }
}
