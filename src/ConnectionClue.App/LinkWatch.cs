using System.ComponentModel;
using System.Threading.Channels;
using ConnectionClue.Core;
using ConnectionClue.Windows;

namespace ConnectionClue.App;

/// <summary>
/// Collects link up/down (cable or adapter) and Wi-Fi connect/disconnect notifications for the probed interface during
/// one check: the evidence for "Wi-Fi link dropped at …" (R01). Both sources need no consent; an unavailable one is skipped.
/// </summary>
internal sealed class LinkWatch : IDisposable
{
    private readonly InterfaceMonitor? _links;
    private readonly WlanMonitor? _wlan;
    private readonly List<MonitorEvent> _events = [];
    private readonly Lock _gate = new();

    public LinkWatch(ulong interfaceLuid, bool wifi)
    {
        try
        {
            _links = new InterfaceMonitor(TimeProvider.System);
            _links.Start();
        }
        catch (Win32Exception)
        {
            _links?.Dispose();
            _links = null;
        }
        if (wifi)
        {
            _wlan = new WlanMonitor(TimeProvider.System);
            if (_wlan.Start() != CapabilityState.Available)
            {
                _wlan.Dispose();
                _wlan = null;
            }
        }
        _ = PumpAsync(_links?.Events, e => e.InterfaceLuid == interfaceLuid && e.Kind is ConnectionEventKind.LinkUp or ConnectionEventKind.LinkDown);
        _ = PumpAsync(_wlan?.Events, e => e.Kind is ConnectionEventKind.WlanConnected or ConnectionEventKind.WlanDisconnected);
    }

    public IReadOnlyList<MonitorEvent> Snapshot()
    {
        lock (_gate) return [.. _events];
    }

    public void Dispose()
    {
        _links?.Dispose();
        _wlan?.Dispose();
    }

    // Ends when Dispose completes the monitor's channel.
    private async Task PumpAsync(ChannelReader<MonitorEvent>? reader, Func<MonitorEvent, bool> keep)
    {
        if (reader is null) return;
        await foreach (var e in reader.ReadAllAsync().ConfigureAwait(false))
            if (keep(e))
                lock (_gate) _events.Add(e);
    }
}
