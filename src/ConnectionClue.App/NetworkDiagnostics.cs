using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using ConnectionClue.Presentation.Diagnostics;
using Windows.Devices.WiFi;

namespace ConnectionClue.App;

internal sealed class NetworkDiagnostics : INetworkDiagnostics
{
    private static readonly byte[] PingPayload = new byte[32];
    private const int TraceProbeCount = 3, MaxTraceHops = 16;
    private static readonly TimeSpan TraceTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly (string Name, IPAddress Address)[] Resolvers =
    [
        ("Cloudflare", IPAddress.Parse("1.1.1.1")),
        ("Google", IPAddress.Parse("8.8.8.8")),
        ("Quad9", IPAddress.Parse("9.9.9.9")),
    ];

    /// <summary>Primary and secondary IPv4 then IPv6 addresses each provider publishes for its public resolver.</summary>
    private static readonly Dictionary<string, string[]> ResolverAddresses = new()
    {
        ["Cloudflare"] = ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"],
        ["Google"] = ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"],
        ["Quad9"] = ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"],
    };

    private const int ErrorCancelled = 1223; // the user declined the administrator prompt

    /// <summary>The only 2.4 GHz channels that do not overlap each other.</summary>
    private static readonly int[] NonOverlapping24GhzChannels = [1, 6, 11];

    public async Task<IReadOnlyList<TraceHop>> TraceRouteAsync(CancellationToken cancellationToken)
    {
        var destination = IPAddress.Parse("1.1.1.1");
        var hops = new List<TraceHop>();
        using var ping = new Ping();
        double? previousRtt = null;

        for (int ttl = 1; ttl <= MaxTraceHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replies = new List<double>(TraceProbeCount);
            string? address = null;
            bool reached = false, failed = false;
            for (int probe = 0; probe < TraceProbeCount; probe++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Windows reports RoundtripTime 0 for TTL-expired replies, so every hop is timed here, the same way.
                    long sent = Stopwatch.GetTimestamp();
                    var reply = await ping.SendPingAsync(destination, TraceTimeout, PingPayload,
                        new PingOptions(ttl, dontFragment: true), cancellationToken).ConfigureAwait(false);
                    if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                    {
                        replies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                        address ??= reply.Address?.ToString();
                    }
                    reached |= reply.Status == IPStatus.Success;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (PingException)
                {
                    failed = true;
                    break;
                }
            }

            double? median = replies.Count == 0 ? null : replies.Order().ElementAt(replies.Count / 2);
            double? increase = median is { } current && previousRtt is { } previous
                ? Math.Max(0, current - previous) : null;
            var status = failed ? NetworkToolStatus.Failed : reached ? NetworkToolStatus.Success
                : replies.Count > 0 ? NetworkToolStatus.Responded : NetworkToolStatus.TimedOut;
            hops.Add(new(ttl, address, median, replies.Count, TraceProbeCount, increase, status));
            if (median is not null) previousRtt = median;
            if (reached || failed) break;
        }

        return hops;
    }

    public async Task<IReadOnlyList<DnsResolverMeasurement>> ComparePublicDnsAsync(CancellationToken cancellationToken)
    {
        var current = InternetAdapter()?.Dns.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var measurements = Resolvers.Select(resolver => MeasureResolverAsync(resolver.Name, resolver.Address, resolver.Address.Equals(current), cancellationToken)).ToList();
        if (current is not null && !Resolvers.Any(r => r.Address.Equals(current)))
            measurements.Insert(0, MeasureResolverAsync("", current, true, cancellationToken));
        return await Task.WhenAll(measurements).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes the DNS servers of the adapter that carries the default route, through an elevated PowerShell that Windows
    /// asks the user to approve; null restores automatic (DHCP) DNS. IPv6 addresses are added only where IPv6 is bound.
    /// </summary>
    public async Task<DnsSwitchResult> SetDnsAsync(string? provider, CancellationToken cancellationToken)
    {
        if (InternetAdapter() is not { } adapter) return DnsSwitchResult.NoAdapter;
        string[]? servers = null;
        if (provider is not null && !ResolverAddresses.TryGetValue(provider, out servers)) return DnsSwitchResult.Failed;
        string index = adapter.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string script = servers is null
            ? $"$ErrorActionPreference='Stop'; Set-DnsClientServerAddress -InterfaceIndex {index} -ResetServerAddresses; Clear-DnsClientCache"
            : $"$ErrorActionPreference='Stop'; Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses {List(servers.Take(2))}; "
              + $"try {{ Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses {List(servers)} }} catch {{ }}; Clear-DnsClientCache";
        var start = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            // The administrator prompt blocks the starting thread until answered: keep it off the UI thread.
            using var process = await Task.Run(() => Process.Start(start), cancellationToken).ConfigureAwait(false);
            if (process is null) return DnsSwitchResult.Failed;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode != 0 ? DnsSwitchResult.Failed : servers is null ? DnsSwitchResult.Restored : DnsSwitchResult.Switched;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return DnsSwitchResult.Cancelled;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DnsSwitchResult.Failed;
        }

        static string List(IEnumerable<string> addresses) => "(" + string.Join(",", addresses.Select(a => $"'{a}'")) + ")";
    }

    /// <summary>The adapter that carries the IPv4 default route, with its configured DNS servers.</summary>
    private static (uint Index, IPAddress[] Dns)? InternetAdapter()
    {
        var path = new ConnectionClue.Windows.RouteProvider().GetDefaultPath(ConnectionClue.Core.IpFamily.IPv4);
        if (path is null) return null;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = adapter.GetIPProperties();
                if (properties.GetIPv4Properties()?.Index == path.InterfaceIndex) return (path.InterfaceIndex, [.. properties.DnsAddresses]);
            }
            catch (NetworkInformationException)
            {
            }
        }
        return (path.InterfaceIndex, []);
    }

    public async Task<WifiScanResult> ScanWifiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed) return new(WifiScanStatus.PermissionDenied, []);
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count == 0) return new(WifiScanStatus.NoAdapter, []);

            var internetProfile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            var adapter = adapters.FirstOrDefault(a =>
                a.NetworkAdapter.NetworkAdapterId == internetProfile?.NetworkAdapter?.NetworkAdapterId) ?? adapters[0];
            await adapter.ScanAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var networks = adapter.NetworkReport.AvailableNetworks;
            var observations = networks
                .Select(network => ChannelFor(network.ChannelCenterFrequencyInKilohertz))
                .Where(channel => channel is not null)
                .Select(channel => channel!.Value)
                .ToArray();
            if (observations.Length == 0) return new(WifiScanStatus.NoNetworks, []);
            string? bssid = ConnectionClue.Windows.WlanInfo.ConnectedBssid(adapter.NetworkAdapter.NetworkAdapterId);
            var mine = bssid is null ? null : networks.FirstOrDefault(n => string.Equals(n.Bssid, bssid, StringComparison.OrdinalIgnoreCase));
            var connected = mine is null ? null : ChannelFor(mine.ChannelCenterFrequencyInKilohertz);

            var summaries = new List<WifiChannelSummary>();
            foreach (var band in observations.GroupBy(c => c.Band))
            {
                var counts = band.GroupBy(c => c.Channel).ToDictionary(g => g.Key, g => g.Count());
                int recommended = band.Key switch
                {
                    "2.4" => NonOverlapping24GhzChannels.OrderBy(candidate => OverlapScore(observations, candidate)).ThenBy(c => c).First(),
                    "5" => QuietestFiveGhzBlock(counts),
                    _ => counts.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key).First().Key,
                };
                counts.TryAdd(recommended, 0); // the best channel is often one nobody uses yet
                summaries.AddRange(counts.OrderBy(pair => pair.Key).Select(pair => new WifiChannelSummary(band.Key, pair.Key, pair.Value,
                    pair.Key == recommended, connected is { } c && c.Band == band.Key && c.Channel == pair.Key)));
            }
            return new(WifiScanStatus.Success, summaries);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return new(WifiScanStatus.Failed, []);
        }
    }

    public void OpenNetworkSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });

    private static async Task<DnsResolverMeasurement> MeasureResolverAsync(
        string provider, IPAddress server, bool isCurrent, CancellationToken cancellationToken)
    {
        var measurements = new List<double>(3);
        NetworkToolStatus status = NetworkToolStatus.TimedOut;
        for (int i = 0; i < 3; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
                using var client = new UdpClient(AddressFamily.InterNetwork);
                client.Connect(server, 53);
                byte[] query = BuildDnsQuery();
                var timer = Stopwatch.StartNew();
                await client.SendAsync(query, timeout.Token).ConfigureAwait(false);
                var response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (ValidDnsResponse(query, response.Buffer))
                {
                    measurements.Add(timer.Elapsed.TotalMilliseconds);
                    status = NetworkToolStatus.Success;
                }
                else
                {
                    status = NetworkToolStatus.Failed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                status = measurements.Count > 0 ? NetworkToolStatus.Success : NetworkToolStatus.TimedOut;
            }
            catch (SocketException)
            {
                status = NetworkToolStatus.Failed;
            }
        }

        double? median = measurements.Count switch
        {
            0 => null,
            1 => measurements[0],
            2 => measurements.Average(),
            _ => measurements.Order().ElementAt(measurements.Count / 2),
        };
        return new(provider, server.ToString(), median, measurements.Count, status, isCurrent);
    }

    private static byte[] BuildDnsQuery()
    {
        ushort id = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        byte[] query =
        [
            (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0,
            0, 1, 0, 1,
        ];
        return query;
    }

    private static bool ValidDnsResponse(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response) =>
        response.Length >= 12 && response[0] == query[0] && response[1] == query[1]
        && (response[2] & 0x80) != 0 && (response[3] & 0x0f) == 0
        && BinaryPrimitives.ReadUInt16BigEndian(response[6..]) > 0;

    private static (string Band, int Channel)? ChannelFor(int frequencyKHz)
    {
        int mhz = frequencyKHz / 1000;
        if (mhz is >= 2412 and <= 2472 && (mhz - 2407) % 5 == 0) return ("2.4", (mhz - 2407) / 5);
        if (mhz == 2484) return ("2.4", 14);
        if (mhz is >= 5000 and < 5925 && (mhz - 5000) % 5 == 0) return ("5", (mhz - 5000) / 5);
        if (mhz == 5935) return ("6", 2);
        if (mhz is >= 5955 and <= 7115 && (mhz - 5950) % 5 == 0) return ("6", (mhz - 5950) / 5);
        return null;
    }

    private static int OverlapScore((string Band, int Channel)[] observations, int candidate) =>
        observations.Where(o => o.Band == "2.4").Sum(o => Math.Max(0, 5 - Math.Abs(o.Channel - candidate)));

    /// <summary>36 or 149: the start of the non-DFS 80 MHz blocks (36–48, 149–161) most routers support, whichever is quieter.</summary>
    private static int QuietestFiveGhzBlock(Dictionary<int, int> counts)
    {
        int Networks(int first) => counts.Where(pair => pair.Key >= first && pair.Key <= first + 12).Sum(pair => pair.Value);
        return Networks(149) < Networks(36) ? 149 : 36;
    }
}
