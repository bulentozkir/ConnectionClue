using System.Net;

namespace ConnectionClue.Core;

public enum EventSource { IpHelper, Wlan, Power, Clock, App }

public enum ConnectionEventKind
{
    LinkUp, LinkDown, WlanConnected, WlanDisconnected, WlanRoamed,
    RouteChanged, AddressChanged, ProxyChanged, Suspend, Resume, WallClockChanged
}

public enum CapabilityState { Available, Unavailable, Denied, Unsupported, Unknown }

public enum InterfaceMedium { Ethernet, WiFi, Tunnel, Ppp, Loopback, Other }

/// <summary>Raw OS notification; Timestamp is TimeProvider.GetTimestamp() at receipt. Capture maps it to a session offset.</summary>
public sealed record MonitorEvent(
    long Timestamp, EventSource Source, ConnectionEventKind Kind,
    ulong? InterfaceLuid = null, Guid? InterfaceGuid = null, IpFamily? Family = null, int? ReasonCode = null);

/// <summary>OS-predicted path to a destination (GetBestRoute2). NextHop is null for on-link destinations.</summary>
public sealed record PathContext(
    IpFamily Family, ulong InterfaceLuid, uint InterfaceIndex, IPAddress? NextHop, IPAddress SourceAddress,
    InterfaceMedium Medium, bool IsHardware, bool TunnelSuspected, bool MediaConnected);
