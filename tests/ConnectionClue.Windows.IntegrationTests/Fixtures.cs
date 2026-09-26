using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionClue.Core;
using ConnectionClue.Windows;
using ConnectionClue.Windows.Probes;

namespace ConnectionClue.Windows.IntegrationTests;

internal sealed class StaticResolver(params ResolvedTarget[] targets) : ITargetResolver
{
    public ResolvedTarget? Resolve(StreamKey stream) => targets.FirstOrDefault(t => t.TargetId == stream.TargetId);
}

internal static class Fx
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static SessionClock Clock() => new(TimeProvider.System);

    public static ProbeRequest Request(SessionClock clock, string id, ProbeKind kind, RequestFamily family, int timeoutMs) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 0, new StreamKey(id, kind, family), clock.NowUs, TimeSpan.FromMilliseconds(timeoutMs));

    public static ResolvedTarget Target(string id, int port = 0, params string[] addresses) =>
        new(id, "test", null, port, "/", [.. addresses.Select(IPAddress.Parse)]);

    public static ResolvedTarget Web(string id, string host, string? token = null, int expected = 200) =>
        new(id, "test", host, 443, "/", [], expected, token);

    public static CancellationToken Cancelled() => new(canceled: true);

    public static void Valid(ProbeObservation o) => Assert.Empty(ObservationInvariants.Violations(o));

    public static void RequireInternet()
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) Assert.Skip("No network.");
        try { Dns.GetHostAddresses("example.com"); }
        catch (SocketException) { Assert.Skip("No DNS."); }
    }

    public static PathContext RequireDefaultPath()
    {
        var path = new RouteProvider().GetDefaultPath(IpFamily.IPv4);
        if (path?.NextHop is null) Assert.Skip("No IPv4 default route.");
        return path;
    }
}
