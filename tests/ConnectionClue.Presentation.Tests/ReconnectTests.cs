using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Alerts;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.Review;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class ReconnectTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _time = new();
    private readonly SpySpeed _speed = new();
    private readonly SpyServices _services = new();
    private readonly SpyReviewer _reviewer = new();
    private readonly List<Alert> _alerts = [];
    private readonly SettingsViewModel _settings = new(Localizer.Default, En, backgroundEnabled: false, checkSeconds: 600);
    private readonly MainViewModel _vm;
    private bool _connected = true;
    private int _starts, _active, _maxActive;
    private Exception? _startFailure;

    public ReconnectTests()
    {
        _vm = new MainViewModel(Localizer.Default, En, _time, (_, _) =>
        {
            _starts++;
            if (_startFailure is { } failure) throw failure;
            _maxActive = Math.Max(_maxActive, ++_active);
            return Task.FromResult(new PreviewProbes(new Probe(3), new Probe(20), new Probe(15), new Probe(60),
                new CheckContext(ConnectionMedium.WiFi), _speed,
                Task.FromResult(new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 400, SignalBars: 1))),
                Monitors: new Lifetime(() => _active--)));
        }, new(), _settings, new MemoryStore(), isConnected: () => _connected, reviewer: _reviewer, serviceTargetProbe: _services);
        _vm.AlertRaised += (_, alert) => _alerts.Add(alert);
    }

    public void Dispose() => _vm.Dispose();

    private void Connected(bool value, bool mobile = false)
    {
        _connected = value;
        _vm.UpdateConnectivity(value, mobile);
    }

    private async Task Tick(int seconds = 1)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        await Task.Delay(5, TestContext.Current.CancellationToken);
    }

    private async Task Finish(Task task)
    {
        for (int i = 0; i < 50 && !task.IsCompleted; i++) await Tick();
        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Starting_connected_does_not_run_a_reconnect_check()
    {
        Connected(true);
        Connected(true);
        await Tick(60);
        Assert.Equal(0, _starts);
        Assert.Empty(_vm.ConnectionStatus);
    }

    [Fact]
    public void Disconnect_notifies_once_per_episode_even_on_another_page()
    {
        _vm.Page = AppPage.Settings;
        Connected(false);
        Connected(false);
        Connected(false);
        var alert = Assert.Single(_alerts);
        Assert.Equal(AlertKind.Disconnected, alert.Kind);
        Assert.Equal("No network connection", alert.Title);
        Assert.StartsWith("No network connection.", _vm.ConnectionStatus, StringComparison.Ordinal);
        Assert.True(_vm.IsDisconnected);
        Assert.Equal(AppPage.Settings, _vm.Page);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task Reconnect_runs_one_ten_second_check_without_speed_services_or_online_review()
    {
        Connected(false);
        Connected(true);
        var once = _vm.ReconnectCheckTask;
        for (int i = 0; i < 5; i++) Connected(true);
        Assert.Same(once, _vm.ReconnectCheckTask);
        await Finish(once);
        Assert.Equal(1, _starts);
        Assert.Equal(10, _vm.ChartSeconds);
        Assert.Equal(0, _speed.Calls);
        Assert.Equal(0, _services.Calls);
        Assert.Equal(0, _reviewer.Calls);
        Assert.NotEmpty(_vm.BestPractices); // Advice exists, but no online review is requested.
        Assert.Empty(_vm.ConnectionStatus);
        Assert.Equal(600, _settings.CheckSeconds);
        Assert.True(_settings.MeasureSpeed);
        Assert.True(_settings.AiReview);
        Assert.False(_settings.BackgroundEnabled);
        Assert.Equal(15, _settings.IntervalMinutes);
        Connected(true);
        await Tick(3600);
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task Starting_offline_then_connecting_runs_the_one_time_check()
    {
        Connected(false);
        Assert.Equal(HeroState.Disconnected, _vm.Hero);
        Connected(true);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(1, _starts);
        Assert.False(_vm.IsDisconnected);
    }

    [Fact]
    public async Task A_manual_check_can_refresh_a_stale_offline_state_without_leaving_the_warning_visible()
    {
        Connected(false);
        _connected = true; // Windows has not delivered its reconnect notification yet.
        _settings.CheckSeconds = 10;
        _settings.MeasureSpeed = false;
        var manual = _vm.QuickCheckCommand.ExecuteAsync(null);
        Assert.False(_vm.IsDisconnected);
        Assert.DoesNotContain("No network connection.", _vm.ConnectionStatus, StringComparison.Ordinal);
        await Finish(manual);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(2, _starts); // One manual check, then the single queued automatic check.
        Assert.Empty(_vm.ConnectionStatus);
    }

    [Fact]
    public async Task A_flapping_connection_cancels_the_not_yet_started_check()
    {
        Connected(false);
        Connected(true);
        var first = _vm.ReconnectCheckTask;
        await Tick();
        Connected(false);
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, _starts);
        Connected(true);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task Another_disconnect_cancels_the_automatic_check_then_rearms_once()
    {
        Connected(false);
        Connected(true);
        var first = _vm.ReconnectCheckTask;
        await Tick(2);
        Assert.Equal(1, _starts);
        Assert.True(_vm.IsRunning);
        Connected(false);
        await Finish(first);
        Assert.True(_vm.IsDisconnected);
        Assert.Equal(HeroState.Disconnected, _vm.Hero);
        Connected(true);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(2, _starts);
        Assert.Equal(1, _maxActive);
        Assert.Equal(2, _alerts.Count(a => a.Kind == AlertKind.Disconnected));
    }

    [Fact]
    public async Task Reconnect_waits_for_an_existing_capture_instead_of_interrupting_it()
    {
        var capture = _vm.LongCaptureCommand.ExecuteAsync(null);
        Connected(false);
        Connected(true);
        await Tick(3);
        Assert.Equal(1, _starts);
        Assert.False(capture.IsCompleted);
        Assert.False(_vm.ReconnectCheckTask.IsCompleted);
        _vm.StopCommand.Execute(null);
        await capture.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(2, _starts);
        Assert.Equal(1, _maxActive);
        Assert.Equal(10, _vm.ChartSeconds);
    }

    [Fact]
    public async Task Reconnect_waits_for_result_processing_after_measurement_has_finished()
    {
        var review = new TaskCompletionSource<IReadOnlyDictionary<string, ReviewVerdict>>();
        _reviewer.Pending = review.Task;
        var check = _vm.RunCheckAsync(false, 10);
        for (int i = 0; i < 30 && !_vm.IsReviewing; i++) await Tick();
        Assert.True(_vm.IsReviewing);
        Assert.False(_vm.IsRunning);
        Connected(false);
        Connected(true);
        await Tick(3);
        Assert.Equal(1, _starts);
        review.SetResult(new Dictionary<string, ReviewVerdict>());
        await check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(2, _starts);
        Assert.Equal(1, _reviewer.Calls);
    }

    [Fact]
    public async Task Reconnect_waits_while_export_is_busy()
    {
        _vm.IsExportingResults = true;
        Connected(false);
        Connected(true);
        await Tick(3);
        Assert.Equal(0, _starts);
        _vm.IsExportingResults = false;
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task Ending_a_capture_offline_keeps_the_disconnected_warning()
    {
        var check = _vm.RunCheckAsync(false, 10);
        Connected(false);
        Assert.True(_vm.IsRunning); // Manual capture continues to record the drop.
        await Finish(check);
        Assert.Equal(HeroState.Disconnected, _vm.Hero);
        Assert.StartsWith("No network connection.", _vm.ConnectionStatus, StringComparison.Ordinal);
        Assert.Single(_alerts, a => a.Kind == AlertKind.Disconnected);
        Assert.DoesNotContain(_alerts, a => a.Kind == AlertKind.Recovered);
    }

    [Fact]
    public async Task Failure_is_reported_without_automatic_retries()
    {
        _startFailure = new InvalidOperationException("No probe session could be created.");
        Connected(false);
        Connected(true);
        await Finish(_vm.ReconnectCheckTask);
        Assert.StartsWith("The automatic reconnect check could not finish.", _vm.ConnectionStatus, StringComparison.Ordinal);
        Assert.Equal(HeroState.Inconclusive, _vm.Hero);
        Assert.False(_vm.IsRunning);
        Connected(true);
        await Tick(30);
        Assert.Equal(1, _starts);
        _startFailure = null;
        await Finish(_vm.RunCheckAsync(false, 10));
        Assert.Empty(_vm.ConnectionStatus);
    }

    [Fact]
    public async Task Disposing_cancels_pending_reconnection_work()
    {
        Connected(false);
        Connected(true);
        var pending = _vm.ReconnectCheckTask;
        _vm.Dispose();
        await Finish(pending);
        Connected(false);
        Connected(true);
        await Tick(30);
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task One_time_check_does_not_enable_regular_mobile_checks()
    {
        Connected(false, mobile: true);
        Connected(true, mobile: true);
        await Finish(_vm.ReconnectCheckTask);
        Assert.Equal(1, _starts);
        Assert.Equal(0, _speed.Calls);
        Assert.False(_settings.BackgroundOnMobileEnabled);
        Assert.False(_vm.IsBackgroundCheckScheduled);
    }

    private sealed class Probe(double ms) : IProbe
    {
        public ProbeKind Kind => ProbeKind.Tcp;
        public Task<ProbeObservation> ExecuteAsync(ProbeRequest r, CancellationToken ct) => Task.FromResult(new ProbeObservation(
            r, ProbeStatus.Success, Attribution.SocketObserved, IpFamily.IPv4, r.ScheduledUs, r.ScheduledUs + (long)(ms * 1000),
            (long)(ms * 1000), TimingSource.UserMode, null, new EmptyDetail()));
    }

    private sealed class Lifetime(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class SpySpeed : IThroughputProbe
    {
        public int Calls { get; private set; }
        public Task<ThroughputResult> MeasureAsync(ThroughputDirection direction, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ThroughputResult(direction, ProbeStatus.Success, 20, 1000, duration));
        }
    }

    private sealed class SpyServices : IServiceTargetProbe
    {
        public int Calls { get; private set; }
        public Task<ServiceTargetResult> ProbeAsync(ServiceTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ServiceTargetResult(target, ServiceTargetStatus.Connected, 10));
        }
    }

    private sealed class SpyReviewer : IAdviceReviewer
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, ReviewVerdict>>? Pending { get; set; }
        public Task<IReadOnlyDictionary<string, ReviewVerdict>> ReviewAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            return Pending ?? Task.FromResult<IReadOnlyDictionary<string, ReviewVerdict>>(new Dictionary<string, ReviewVerdict>());
        }
    }

    private sealed class MemoryStore : IResultStore
    {
        public SavedResult? Value { get; private set; }
        public SavedResult? Load() => Value;
        public void Save(SavedResult result) => Value = result;
        public void Clear() => Value = null;
    }
}
