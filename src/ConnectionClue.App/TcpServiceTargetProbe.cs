using System.Diagnostics;
using System.Net.Sockets;
using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.App;

internal sealed class TcpServiceTargetProbe : IServiceTargetProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<ServiceTargetResult> ProbeAsync(ServiceTarget target, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(target.Host, target.Port, linked.Token);
            return new(target, ServiceTargetStatus.Connected, timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(target, ServiceTargetStatus.TimedOut, null);
        }
        catch (SocketException e)
        {
            var status = e.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => ServiceTargetStatus.NameLookupFailed,
                SocketError.ConnectionRefused => ServiceTargetStatus.Refused,
                _ => ServiceTargetStatus.Unreachable,
            };
            return new(target, status, null);
        }
    }
}
