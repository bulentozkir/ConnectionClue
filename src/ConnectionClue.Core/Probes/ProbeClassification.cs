using System.Net.NetworkInformation;
using System.Net.Sockets;
using static ConnectionClue.Core.ProbeStatus;

namespace ConnectionClue.Core;

/// <summary>Pure mapping of OS/protocol outcomes to persisted statuses and ErrorCode vocabulary.</summary>
public static class ProbeClassification
{
    public const int DnsServerFailure = 9002, DnsNameError = 9003, DnsRefused = 9005, DnsNoRecords = 9501,
        DnsNoServers = 9852, Win32Cancelled = 1223, Win32Timeout = 1460;

    public const int MaxBodyBytes = 4096;

    public static (ProbeStatus Status, string? ErrorCode) FromIpStatus(IPStatus s) => s switch
    {
        IPStatus.Success => (Success, null),
        IPStatus.TimedOut => (ProbeStatus.Timeout, null),
        IPStatus.DestinationNetworkUnreachable => (NetworkUnreachable, null),
        IPStatus.DestinationHostUnreachable or IPStatus.DestinationUnreachable => (HostUnreachable, null),
        IPStatus.DestinationProhibited => (HostUnreachable, "Prohibited"),
        IPStatus.DestinationPortUnreachable => (HostUnreachable, "PortUnreachable"),
        IPStatus.HardwareError or IPStatus.NoResources or IPStatus.BadDestination or IPStatus.Unknown
            => (InternalError, s.ToString()),
        _ => (NetworkUnreachable, s.ToString()),
    };

    public static (ProbeStatus Status, string? ErrorCode) FromSocketError(SocketError e) => e switch
    {
        SocketError.ConnectionRefused => (Refused, null),
        SocketError.ConnectionReset => (Refused, "Reset"),
        SocketError.TimedOut => (ProbeStatus.Timeout, null),
        SocketError.NetworkUnreachable => (NetworkUnreachable, null),
        SocketError.NetworkDown => (NetworkUnreachable, "NetworkDown"),
        SocketError.HostUnreachable => (HostUnreachable, null),
        SocketError.HostDown => (HostUnreachable, "HostDown"),
        SocketError.AddressNotAvailable => (NetworkUnreachable, "AddressNotAvailable"),
        SocketError.AccessDenied => (PermissionDenied, "AccessDenied"),
        SocketError.HostNotFound => (DnsFailure, "NxDomain"),
        SocketError.NoData => (DnsFailure, "NoData"),
        SocketError.TryAgain => (DnsFailure, "ServFail"),
        SocketError.OperationAborted => (Cancelled, null),
        _ => (InternalError, e.ToString()),
    };

    public static (ProbeStatus Status, string? ErrorCode) FromDns(int win32Status, int matchingRecords) => win32Status switch
    {
        0 when matchingRecords > 0 => (Success, null),
        0 or DnsNoRecords => (DnsFailure, "NoData"),
        DnsNameError => (DnsFailure, "NxDomain"),
        DnsServerFailure => (DnsFailure, "ServFail"),
        DnsRefused => (DnsFailure, "DnsRefused"),
        DnsNoServers => (DnsFailure, "NoServers"),
        Win32Timeout => (ProbeStatus.Timeout, null),
        Win32Cancelled => (Cancelled, null),
        _ => (DnsFailure, $"Win32_{win32Status}"),
    };

    public static (ProbeStatus Status, string? ErrorCode) FromHttp(int status, int expected, bool oversize, bool tokenMatched) =>
        status switch
        {
            407 => (ProxyAuthRequired, null),
            429 => (RateLimited, null),
            >= 300 and < 400 => (HttpUnexpected, "Redirect"),
            _ when status != expected => (HttpUnexpected, "Status"),
            _ when oversize => (HttpUnexpected, "Oversize"),
            _ when !tokenMatched => (HttpUnexpected, "Body"),
            _ => (Success, null),
        };
}
