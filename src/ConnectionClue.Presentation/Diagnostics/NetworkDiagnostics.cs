namespace ConnectionClue.Presentation.Diagnostics;

public enum NetworkToolStatus { Success, Responded, NoReply, TimedOut, Failed, PermissionDenied, Unsupported }
public enum WifiScanStatus { Success, NoAdapter, NoNetworks, PermissionDenied, Failed }

/// <summary>Outcome of switching DNS: Cancelled means the Windows administrator prompt was declined.</summary>
public enum DnsSwitchResult { Switched, Restored, Cancelled, Failed, NoAdapter }

public sealed record TraceHop(
    int Hop, string? Address, double? MedianRoundTripMilliseconds, int Replies, int Probes,
    double? IncreaseFromPreviousHopMilliseconds, NetworkToolStatus Status);

/// <summary>IsCurrent marks the resolver this PC uses now; Provider is then empty unless it is one of the public services.</summary>
public sealed record DnsResolverMeasurement(
    string Provider, string Server, double? MedianMilliseconds, int SuccessfulQueries, NetworkToolStatus Status, bool IsCurrent = false);

/// <summary>Connected is the band and channel this PC uses now, when Windows reports it.</summary>
public sealed record WifiChannelSummary(string Band, int Channel, int NearbyNetworks, bool Recommended, bool Connected = false);

public sealed record WifiScanResult(WifiScanStatus Status, IReadOnlyList<WifiChannelSummary> Channels);

public interface INetworkDiagnostics
{
    Task<IReadOnlyList<TraceHop>> TraceRouteAsync(CancellationToken cancellationToken);

    /// <summary>This PC's current resolver (when it answers plain DNS) and Cloudflare, Google and Quad9.</summary>
    Task<IReadOnlyList<DnsResolverMeasurement>> ComparePublicDnsAsync(CancellationToken cancellationToken);

    /// <summary>Points the active adapter's DNS at a public provider (null: back to automatic). Needs administrator approval.</summary>
    Task<DnsSwitchResult> SetDnsAsync(string? provider, CancellationToken cancellationToken);

    Task<WifiScanResult> ScanWifiAsync(CancellationToken cancellationToken);
    void OpenNetworkSettings();
}
