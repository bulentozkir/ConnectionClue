using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.Alerts;

namespace ConnectionClue.Presentation.ViewModels;

public sealed partial class MainViewModel
{
    public const int ReconnectCheckSeconds = 10;
    public static readonly TimeSpan ReconnectSettleTime = TimeSpan.FromSeconds(2);

    private Task _activeCheck = Task.CompletedTask;
    private CancellationTokenSource? _reconnectCancellation;
    private TaskCompletionSource? _operationChanged;
    private IAsyncRelayCommand[] _reconnectBlockers = [];
    private bool _disconnectNotified, _disposed;

    /// <summary>A connection notice shown on every page; empty once the reconnect check has finished.</summary>
    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "";

    /// <summary>The latest one-shot reconnect operation, including waiting for the current operation to finish.</summary>
    public Task ReconnectCheckTask { get; private set; } = Task.CompletedTask;

    private void InitializeReconnectChecks()
    {
        _reconnectBlockers =
        [
            QuickCheckCommand, LongCaptureCommand, TestServicesCommand,
            TraceRouteCommand, ComparePublicDnsCommand, ScanWifiCommand,
            SwitchToFastestDnsCommand, RestoreAutomaticDnsCommand,
            ExportResultsCommand, ExportMhtmlCommand, ExportHtmlReportCommand,
        ];
        PropertyChanged += OnReconnectOperationChanged;
        foreach (var command in _reconnectBlockers) command.PropertyChanged += OnReconnectOperationChanged;
    }

    private void OnReconnectOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsRunning) or nameof(IsReviewing) or nameof(IsExportingResults)
            or nameof(IsTestingTarget) or nameof(IsRunningNetworkTool))
            SignalOperationChanged();
    }

    private void SignalOperationChanged() =>
        Interlocked.Exchange(ref _operationChanged, null)?.TrySetResult();

    private bool ReadyForReconnectCheck => CanStart() && !IsReviewing && !IsTestingTarget && !IsRunningNetworkTool
        && _reconnectBlockers.All(command => !command.IsRunning);

    private async Task WaitForReconnectSlotAsync(CancellationToken cancellationToken)
    {
        while (!ReadyForReconnectCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signal = _operationChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ReadyForReconnectCheck) break;
            await signal.Task.WaitAsync(cancellationToken);
        }
    }

    private void QueueReconnectCheck()
    {
        var cancellation = new CancellationTokenSource();
        _reconnectCancellation = cancellation;
        ConnectionStatus = _l.Get("Connectivity_ReconnectPending", _ui);
        Emit(ConnectionStatus, AnnouncementKind.SessionState);
        ReconnectCheckTask = RunReconnectCheckAsync(cancellation);
    }

    private void CancelReconnectCheck()
    {
        var cancellation = _reconnectCancellation;
        _reconnectCancellation = null;
        cancellation?.Cancel(); // The asynchronous owner disposes it after its wait/check has stopped.
    }

    private async Task RunReconnectCheckAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool failed = false;
        try
        {
            // Windows can publish several notifications while routes and DNS are still being configured.
            await Task.Delay(ReconnectSettleTime, _time, token);
            await WaitForReconnectSlotAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || IsDisconnected) return;
            if (_isConnected?.Invoke() == false)
            {
                UpdateConnectivity(false, IsMobileNetwork);
                return;
            }
            ConnectionStatus = _l.Get("Connectivity_ReconnectChecking", _ui);
            Emit(ConnectionStatus, AnnouncementKind.SessionState);
            await RunCheckAsync(false, ReconnectCheckSeconds, CheckKind.Reconnect, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException
            or NotSupportedException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            failed = true;
            if (!_disposed && ReferenceEquals(_reconnectCancellation, cancellation) && !IsDisconnected)
            {
                ConnectionStatus = _l.Get("Connectivity_ReconnectFailed", _ui);
                (Hero, HeroTitle, Summary) = (HeroState.Inconclusive, _l.Get("Health_Inconclusive", _ui), ConnectionStatus);
                Emit(ConnectionStatus, AnnouncementKind.Error);
                AlertRaised?.Invoke(this, new Alert(AlertKind.Problem, HeroTitle, ConnectionStatus));
            }
        }
        finally
        {
            if (ReferenceEquals(_reconnectCancellation, cancellation))
            {
                _reconnectCancellation = null;
                if (!failed && !IsDisconnected) ConnectionStatus = "";
            }
            cancellation.Dispose();
        }
    }

    private void DisposeReconnectChecks()
    {
        _disposed = true;
        CancelReconnectCheck();
        PropertyChanged -= OnReconnectOperationChanged;
        foreach (var command in _reconnectBlockers) command.PropertyChanged -= OnReconnectOperationChanged;
        SignalOperationChanged();
    }
}
