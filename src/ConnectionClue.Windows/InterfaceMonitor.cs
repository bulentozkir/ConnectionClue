using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ConnectionClue.Core;
using ConnectionClue.Windows.Interop;

namespace ConnectionClue.Windows;

/// <summary>
/// Link up/down (NotifyIpInterfaceChange + GetIfEntry2) and route-table changes (NotifyRouteChange2). No consent needed.
/// Callbacks only enqueue; interface state is queried and diffed on a background pump.
/// </summary>
public sealed class InterfaceMonitor(TimeProvider time) : IDisposable
{
    private readonly MonitorChannel _out = new();
    private readonly Channel<(long Timestamp, ulong Luid, IpFamily? Family)> _raw =
        Channel.CreateBounded<(long, ulong, IpFamily?)>(new BoundedChannelOptions(1024) { SingleReader = true });
    private readonly Dictionary<ulong, bool> _connected = [];
    private GCHandle _self;
    private HANDLE _interfaceHandle, _routeHandle;
    private Task? _pump;
    private long _rawDropped;

    public ChannelReader<MonitorEvent> Events => _out.Reader;
    public long DroppedEvents => _out.Dropped + Interlocked.Read(ref _rawDropped);

    public unsafe void Start()
    {
        Snapshot();
        _self = GCHandle.Alloc(this);
        var context = (void*)GCHandle.ToIntPtr(_self);
        HANDLE h;
        Check(PInvoke.NotifyIpInterfaceChange(default, &OnInterfaceChange, context, default, &h));
        _interfaceHandle = h;
        Check(PInvoke.NotifyRouteChange2(default, &OnRouteChange, context, default, &h));
        _routeHandle = h;
        _pump = Task.Run(PumpAsync);
    }

    public void Dispose()
    {
        // Never call from a notification callback: CancelMibChangeNotify2 waits for in-flight callbacks.
        if (_interfaceHandle != default) PInvoke.CancelMibChangeNotify2(_interfaceHandle);
        if (_routeHandle != default) PInvoke.CancelMibChangeNotify2(_routeHandle);
        _interfaceHandle = _routeHandle = default;
        _raw.Writer.TryComplete();
        _pump?.Wait(TimeSpan.FromSeconds(5));
        _out.Complete();
        if (_self.IsAllocated) _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnInterfaceChange(void* context, MIB_IPINTERFACE_ROW* row, MIB_NOTIFICATION_TYPE type)
    {
        if (row is null || GCHandle.FromIntPtr((nint)context).Target is not InterfaceMonitor m) return;
        if (!m._raw.Writer.TryWrite((m.Now(), row->InterfaceLuid.Value, SockAddr.FamilyOf(row->Family))))
            Interlocked.Increment(ref m._rawDropped);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnRouteChange(void* context, MIB_IPFORWARD_ROW2* row, MIB_NOTIFICATION_TYPE type)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is not InterfaceMonitor m) return;
        m._out.Post(new MonitorEvent(m.Now(), EventSource.IpHelper, ConnectionEventKind.RouteChanged,
            row is null ? null : row->InterfaceLuid.Value,
            Family: row is null ? null : SockAddr.FamilyOf(row->DestinationPrefix.Prefix.si_family)));
    }

    private long Now() => time.GetTimestamp();

    private async Task PumpAsync()
    {
        await foreach (var (timestamp, luid, family) in _raw.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool up = IsConnected(luid) ?? false; // a deleted interface counts as down
            bool known = _connected.TryGetValue(luid, out bool was);
            _connected[luid] = up;
            if (known ? was != up : up)
                _out.Post(new MonitorEvent(timestamp, EventSource.IpHelper,
                    up ? ConnectionEventKind.LinkUp : ConnectionEventKind.LinkDown, luid, Family: family));
        }
    }

    private unsafe void Snapshot()
    {
        MIB_IF_TABLE2* table;
        if (PInvoke.GetIfTable2(&table) != WIN32_ERROR.NO_ERROR) return;
        try
        {
            var rows = new ReadOnlySpan<MIB_IF_ROW2>((MIB_IF_ROW2*)&table->Table, (int)table->NumEntries);
            foreach (ref readonly var r in rows) _connected[r.InterfaceLuid.Value] = RouteProvider.Describe(in r).Connected;
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }
    }

    private static unsafe bool? IsConnected(ulong luid)
    {
        var row = new MIB_IF_ROW2 { InterfaceLuid = new NET_LUID_LH { Value = luid } };
        return PInvoke.GetIfEntry2(&row) == WIN32_ERROR.NO_ERROR ? RouteProvider.Describe(in row).Connected : null;
    }

    private static void Check(WIN32_ERROR error)
    {
        if (error != WIN32_ERROR.NO_ERROR) throw new Win32Exception((int)error);
    }
}
