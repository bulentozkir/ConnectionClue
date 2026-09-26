using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionClue.Core;

namespace ConnectionClue.Windows.Probes;

/// <summary>ICMP echo with OS-reported RTT (immune to user-mode CPU contention). One Ping per stream.</summary>
public sealed class IcmpProbe(SessionClock clock, ITargetResolver targets) : IProbe, IDisposable
{
    private static readonly byte[] Payload = new byte[32];
    private readonly ConcurrentDictionary<StreamKey, Lane> _lanes = new();

    public ProbeKind Kind => ProbeKind.Icmp;

    public async Task<ProbeObservation> ExecuteAsync(ProbeRequest request, CancellationToken ct)
    {
        const Attribution Predicted = Attribution.RoutePredicted;
        var address = targets.Resolve(request.Stream)?.AddressFor(request.Stream.Family);
        if (address is null) return Observations.Skipped(request, "NoAddress", Predicted);
        var lane = _lanes.GetOrAdd(request.Stream, static _ => new Lane());
        if (Interlocked.Exchange(ref lane.Busy, 1) == 1) return Observations.Skipped(request, "StreamBusy", Predicted);

        var family = IpFamilies.Of(address);
        long started = clock.NowUs;
        try
        {
            var reply = await lane.Ping.SendPingAsync(address, request.Timeout, Payload, null, ct).ConfigureAwait(false);
            long ended = clock.NowUs;
            var (status, code) = ProbeClassification.FromIpStatus(reply.Status);
            var detail = new IcmpDetail(reply.Status.ToString(), reply.Options?.Ttl);
            return status == ProbeStatus.Success
                ? new ProbeObservation(request, status, Predicted, family, started, ended,
                    reply.RoundtripTime * 1000, TimingSource.OsReported, null, detail)
                : Observations.NoDuration(request, status, Predicted, family, started, ended, code, detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Observations.NoDuration(request, ProbeStatus.Cancelled, Predicted, family, started, clock.NowUs, null);
        }
        catch (PingException ex)
        {
            var (status, code) = ex.InnerException switch
            {
                Win32Exception { NativeErrorCode: 1231 } => (ProbeStatus.NetworkUnreachable, (string?)null),
                Win32Exception { NativeErrorCode: 1232 } => (ProbeStatus.HostUnreachable, null),
                SocketException se => ProbeClassification.FromSocketError(se.SocketErrorCode),
                _ => (ProbeStatus.InternalError, ex.InnerException?.GetType().Name ?? nameof(PingException)),
            };
            return Observations.NoDuration(request, status, Predicted, family, started, clock.NowUs, code);
        }
        finally
        {
            Volatile.Write(ref lane.Busy, 0);
        }
    }

    public void Dispose()
    {
        foreach (var lane in _lanes.Values) lane.Ping.Dispose();
        _lanes.Clear();
    }

    private sealed class Lane
    {
        public readonly Ping Ping = new();
        public int Busy;
    }
}
