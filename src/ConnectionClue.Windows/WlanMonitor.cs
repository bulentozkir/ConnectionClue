using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ConnectionClue.Core;
using ConnectionClue.Windows.Interop;

namespace ConnectionClue.Windows;

/// <summary>
/// Consent-free Wi-Fi baseline: ACM connect/disconnect with reason codes only.
/// MSM (roam, signal) needs wiFiControl + location consent and is deliberately not registered here.
/// </summary>
public sealed unsafe class WlanMonitor(TimeProvider time) : IDisposable
{
    private const uint ErrorAccessDenied = 5, ErrorNotSupported = 50, ErrorServiceDoesNotExist = 1060,
        ErrorServiceNotActive = 1062, AcmConnectionComplete = 10, AcmDisconnected = 21;

    private readonly MonitorChannel _out = new();
    private GCHandle _self;
    private HANDLE _client;

    public CapabilityState State { get; private set; } = CapabilityState.Unknown;
    public uint LastError { get; private set; }
    public ChannelReader<MonitorEvent> Events => _out.Reader;
    public long DroppedEvents => _out.Dropped;

    public CapabilityState Start()
    {
        uint negotiated;
        HANDLE h;
        uint rc = PInvoke.WlanOpenHandle(2, null, &negotiated, &h);
        if (rc == 0)
        {
            _client = h;
            _self = GCHandle.Alloc(this);
            rc = PInvoke.WlanRegisterNotification(h, WLAN_NOTIFICATION_SOURCES.WLAN_NOTIFICATION_SOURCE_ACM, (BOOL)1,
                &OnNotification, (void*)GCHandle.ToIntPtr(_self), null, null);
        }

        LastError = rc;
        State = rc switch
        {
            0 => CapabilityState.Available,
            ErrorServiceNotActive or ErrorServiceDoesNotExist or ErrorNotSupported => CapabilityState.Unsupported,
            ErrorAccessDenied => CapabilityState.Denied,
            _ => CapabilityState.Unavailable,
        };
        if (rc != 0) Close();
        return State;
    }

    public void Dispose()
    {
        Close();
        _out.Complete();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnNotification(L2_NOTIFICATION_DATA* data, void* context)
    {
        if (data is null || GCHandle.FromIntPtr((nint)context).Target is not WlanMonitor m) return;
        if (data->NotificationSource != WLAN_NOTIFICATION_SOURCES.WLAN_NOTIFICATION_SOURCE_ACM) return;
        ConnectionEventKind? kind = data->NotificationCode switch
        {
            AcmConnectionComplete => ConnectionEventKind.WlanConnected,
            AcmDisconnected => ConnectionEventKind.WlanDisconnected,
            _ => null,
        };
        if (kind is null) return;
        int? reason = data->pData is not null && data->dwDataSize >= (uint)sizeof(WLAN_CONNECTION_NOTIFICATION_DATA)
            ? (int)((WLAN_CONNECTION_NOTIFICATION_DATA*)data->pData)->wlanReasonCode
            : null;
        m._out.Post(new MonitorEvent(m.Now(), EventSource.Wlan, kind.Value,
            InterfaceGuid: data->InterfaceGuid, ReasonCode: reason));
    }

    private long Now() => time.GetTimestamp();

    // Never call from the notification callback: WlanCloseHandle waits for in-flight callbacks.
    private void Close()
    {
        if (_client != default)
        {
            _ = PInvoke.WlanRegisterNotification(_client, WLAN_NOTIFICATION_SOURCES.WLAN_NOTIFICATION_SOURCE_NONE, (BOOL)1,
                null, null, null, null);
            _ = PInvoke.WlanCloseHandle(_client, null);
            _client = default;
        }
        if (_self.IsAllocated) _self.Free();
    }
}
