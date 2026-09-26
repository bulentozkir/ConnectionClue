using System.Net;

namespace ConnectionClue.Core;

public enum ProbeKind { Icmp, Tcp, SystemDns, Https }
public enum RequestFamily { IPv4, IPv6, Any }
public enum IpFamily { IPv4, IPv6 }
public enum Attribution { SocketObserved, RoutePredicted, Proxied, Unknown }
public enum TimingSource { OsReported, UserMode }

public enum ProbeStatus
{
    Success, Timeout, NetworkUnreachable, HostUnreachable, Refused, DnsFailure,
    TlsFailure, HttpUnexpected, ProxyAuthRequired, RateLimited,
    PermissionDenied, Unsupported, Cancelled, Skipped, InternalError
}

public sealed record StreamKey(string TargetId, ProbeKind Kind, RequestFamily Family)
{
    public override string ToString() => $"{TargetId}|{Kind}|{Family}";
}

public sealed record ProbeRequest(
    Guid SessionId, Guid SegmentId, long Sequence, StreamKey Stream, long ScheduledUs, TimeSpan Timeout);

public abstract record CaptureRecord(Guid SessionId);

public abstract record ProbeDetail(int Version);
public sealed record EmptyDetail() : ProbeDetail(1);
public sealed record IcmpDetail(string IpStatus, int? Ttl) : ProbeDetail(1);
public sealed record TcpDetail(string? LocalEndpoint, string? RemoteEndpoint, uint? MinRttUs) : ProbeDetail(1);
public sealed record DnsDetail(int Win32Status, int RecordCount) : ProbeDetail(1);
public sealed record HttpsDetail(
    int? StatusCode, int BodyBytes, string? RedirectHost, string? TlsIssuer, bool? TlsIssuerExpected,
    TimeSpan? RetryAfter) : ProbeDetail(1);

public sealed record ProbeObservation(
    ProbeRequest Request, ProbeStatus Status, Attribution Attribution, IpFamily? ObservedFamily,
    long? StartedUs, long? EndedUs, long? DurationUs, TimingSource? TimingSource,
    string? ErrorCode, ProbeDetail Detail) : CaptureRecord(Request.SessionId);

public static class IpFamilies
{
    public static IpFamily Of(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6
            ? IpFamily.IPv6 : IpFamily.IPv4;

    public static bool Matches(this RequestFamily requested, IpFamily actual) =>
        requested == RequestFamily.Any || (requested == RequestFamily.IPv4) == (actual == IpFamily.IPv4);
}
