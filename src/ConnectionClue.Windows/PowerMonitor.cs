using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ConnectionClue.Core;
using ConnectionClue.Windows.Interop;

namespace ConnectionClue.Windows;

/// <summary>
/// Window-less suspend/resume notifications. Best-effort cause attribution only:
/// the scheduler heartbeat is authoritative for gaps (Modern Standby may deliver late or not at all).
/// </summary>
public sealed unsafe class PowerMonitor(TimeProvider time) : IDisposable
{
    private const uint PbtApmSuspend = 4, PbtApmResumeAutomatic = 18;
    private const REGISTER_NOTIFICATION_FLAGS DeviceNotifyCallback = (REGISTER_NOTIFICATION_FLAGS)2;

    private readonly MonitorChannel _out = new();
    private GCHandle _self;
    private DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS* _parameters;
    private void* _registration;

    public ChannelReader<MonitorEvent> Events => _out.Reader;
    public long DroppedEvents => _out.Dropped;

    public void Start()
    {
        _self = GCHandle.Alloc(this);
        _parameters = (DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS*)NativeMemory.AllocZeroed((nuint)sizeof(DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS));
        _parameters->Callback = &OnPowerEvent;
        _parameters->Context = (void*)GCHandle.ToIntPtr(_self);
        void* registration;
        var rc = PInvoke.PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, new HANDLE(_parameters), &registration);
        if (rc != WIN32_ERROR.NO_ERROR)
        {
            Release();
            throw new Win32Exception((int)rc);
        }
        _registration = registration;
    }

    public void Dispose()
    {
        if (_registration is not null)
        {
            PInvoke.PowerUnregisterSuspendResumeNotification((HPOWERNOTIFY)(nint)_registration);
            _registration = null;
        }
        Release();
        _out.Complete();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerEvent(void* context, uint type, void* setting)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is PowerMonitor m)
        {
            ConnectionEventKind? kind = type switch
            {
                PbtApmSuspend => ConnectionEventKind.Suspend,
                PbtApmResumeAutomatic => ConnectionEventKind.Resume,
                _ => null,
            };
            if (kind is { } k) m._out.Post(new MonitorEvent(m.Now(), EventSource.Power, k));
        }
        return 0;
    }

    private long Now() => time.GetTimestamp();

    private void Release()
    {
        if (_parameters is not null)
        {
            NativeMemory.Free(_parameters);
            _parameters = null;
        }
        if (_self.IsAllocated) _self.Free();
    }
}
