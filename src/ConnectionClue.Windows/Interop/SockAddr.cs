using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ConnectionClue.Core;

namespace ConnectionClue.Windows.Interop;

internal static class SockAddr
{
    private const ushort AfInet = 2, AfInet6 = 23;

    // SOCKADDR_INET ABI: family@0, IPv4 address@4; IPv6 address@8, scope id@24.
    public static SOCKADDR_INET From(IPAddress address)
    {
        SOCKADDR_INET s = default;
        var b = MemoryMarshal.AsBytes(new Span<SOCKADDR_INET>(ref s));
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            BitConverter.TryWriteBytes(b, AfInet);
            address.TryWriteBytes(b[4..8], out _);
        }
        else
        {
            BitConverter.TryWriteBytes(b, AfInet6);
            address.TryWriteBytes(b[8..24], out _);
            BitConverter.TryWriteBytes(b[24..28], (uint)address.ScopeId);
        }
        return s;
    }

    public static IPAddress? ToAddress(in SOCKADDR_INET s)
    {
        var b = MemoryMarshal.AsBytes(new ReadOnlySpan<SOCKADDR_INET>(in s));
        return BitConverter.ToUInt16(b) switch
        {
            AfInet => new IPAddress(b[4..8]),
            AfInet6 => new IPAddress(b[8..24], BitConverter.ToUInt32(b[24..28])),
            _ => null,
        };
    }

    public static IpFamily? FamilyOf(ADDRESS_FAMILY family) => (ushort)family switch
    {
        AfInet => IpFamily.IPv4,
        AfInet6 => IpFamily.IPv6,
        _ => null,
    };
}

/// <summary>Non-blocking sink for native callbacks. Overflow is counted and surfaced, never silent.</summary>
internal sealed class MonitorChannel
{
    private readonly Channel<MonitorEvent> _channel = Channel.CreateBounded<MonitorEvent>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _dropped;

    public ChannelReader<MonitorEvent> Reader => _channel.Reader;
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Post(MonitorEvent e)
    {
        if (!_channel.Writer.TryWrite(e)) Interlocked.Increment(ref _dropped);
    }

    public void Complete() => _channel.Writer.TryComplete();
}
