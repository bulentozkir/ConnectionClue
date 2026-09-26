using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ConnectionClue.Core;

namespace ConnectionClue.Windows.Probes;

/// <summary>TCP connect to an approved literal IP:port. Duration = kernel TCP_INFO RTT when available.</summary>
public sealed class TcpProbe(SessionClock clock, ITargetResolver targets) : IProbe
{
    private const int SioTcpInfo = unchecked((int)0xD8000027); // _WSAIORW(IOC_VENDOR, 39)

    public ProbeKind Kind => ProbeKind.Tcp;

    public async Task<ProbeObservation> ExecuteAsync(ProbeRequest request, CancellationToken ct)
    {
        const Attribution Predicted = Attribution.RoutePredicted;
        var target = targets.Resolve(request.Stream);
        if (target?.AddressFor(request.Stream.Family) is not { } address)
            return Observations.Skipped(request, "NoAddress", Predicted);

        var family = IpFamilies.Of(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        long started = clock.NowUs;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, target.Port), timeout.Token).ConfigureAwait(false);
            long ended = clock.NowUs;
            var (rtt, minRtt) = ReadTcpInfo(socket);
            var detail = new TcpDetail(socket.LocalEndPoint?.ToString(), socket.RemoteEndPoint?.ToString(), minRtt);
            return rtt is > 0
                ? new ProbeObservation(request, ProbeStatus.Success, Attribution.SocketObserved, family, started, ended,
                    rtt.Value, TimingSource.OsReported, null, detail)
                : new ProbeObservation(request, ProbeStatus.Success, Attribution.SocketObserved, family, started, ended,
                    ended - started, TimingSource.UserMode, null, detail);
        }
        catch (OperationCanceledException)
        {
            var status = ct.IsCancellationRequested ? ProbeStatus.Cancelled : ProbeStatus.Timeout;
            return Observations.NoDuration(request, status, Predicted, family, started, clock.NowUs, null);
        }
        catch (SocketException ex)
        {
            long ended = clock.NowUs;
            var (status, code) = ProbeClassification.FromSocketError(ex.SocketErrorCode);
            return status == ProbeStatus.Refused
                ? new ProbeObservation(request, status, Predicted, family, started, ended, ended - started,
                    TimingSource.UserMode, code, new EmptyDetail())
                : Observations.NoDuration(request, status, Predicted, family, started, ended, code);
        }
    }

    private static unsafe (uint? Rtt, uint? MinRtt) ReadTcpInfo(Socket socket)
    {
        var output = new byte[sizeof(TCP_INFO_v0)];
        try
        {
            if (socket.IOControl(SioTcpInfo, BitConverter.GetBytes(0), output) < output.Length) return (null, null);
            var info = MemoryMarshal.Read<TCP_INFO_v0>(output);
            return (info.RttUs, info.MinRttUs);
        }
        catch (SocketException)
        {
            return (null, null);
        }
    }
}
