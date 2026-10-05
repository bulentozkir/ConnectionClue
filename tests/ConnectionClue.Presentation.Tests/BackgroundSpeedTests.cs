using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.History;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

/// <summary>Background checks measure speed with a light sample: one connection, about once an hour, and never on metered or
/// mobile connections or while other apps use the link. Quick checks keep the full speed test.</summary>
public sealed class BackgroundSpeedTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly (CultureInfo, CultureInfo) _cultures = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly HistoryStore _history = new();
    private readonly SpySpeed _speed;
    private bool _metered, _mobile, _busy;
    private ulong _received, _sent;

    public BackgroundSpeedTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = En;
        _speed = new SpySpeed(_time);
    }

    public void Dispose() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = _cultures;

    private MainViewModel Vm(SettingsViewModel? settings = null) => new(Localizer.Default, En, _time,
        (_, _) => Task.FromResult(new PreviewProbes(new Probe(3), new Probe(20), new Probe(15), new Probe(60),
            new CheckContext(ConnectionMedium.Ethernet, Metered: _metered), _speed, Counters: Counters)),
        new(), settings ?? new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false, checkSeconds: 10, aiReview: false),
        new NoStore(), historyStore: _history, isMobileNetwork: () => _mobile);

    // Each read adds what other apps moved since the previous one: 20 MB while busy, 16 Mbps over a 10-second delay phase.
    private (ulong Received, ulong Sent)? Counters()
    {
        _received += _busy ? 20_000_000UL : 1_000UL;
        _sent += 1_000UL;
        return (_received, _sent);
    }

    private async Task RunToEnd(Task check)
    {
        for (int i = 0; i < 120 && !check.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await check.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private async Task UntilHistory(int count)
    {
        for (int i = 0; i < 120 && _history.Entries.Count < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        Assert.Equal(count, _history.Entries.Count);
    }

    [Fact]
    public async Task Background_check_takes_a_light_sample_and_keeps_it_in_history()
    {
        _speed.Takes = TimeSpan.FromSeconds(2);
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));

        Assert.Equal([ThroughputDirection.Download, ThroughputDirection.Upload], _speed.Calls.Select(c => c.Direction));
        Assert.All(_speed.Calls, c => Assert.Equal(ThroughputBudget.Light(c.Direction), c.Budget));
        Assert.Equal(("48.0 Mbps", "3.2 MB · light sample"), (vm.Download.Value, vm.Download.Detail));
        Assert.Equal(("12.0 Mbps", "0.9 MB · light sample"), (vm.Upload.Value, vm.Upload.Detail));
        var entry = Assert.Single(_history.Entries);
        Assert.Equal(48.0, entry.DownloadMbps);
        Assert.Equal(12.0, entry.UploadMbps);
        // One gentle connection says nothing about latency under load, so there is no bufferbloat grade.
        Assert.Equal("", vm.Latency.Detail);
        Assert.Null(entry.BufferbloatGrade);
        Assert.InRange(vm.ChartSeconds, 14, 15); // the chart ends with the sample, not at its time limit
    }

    [Fact]
    public async Task Quick_checks_keep_the_full_speed_test_with_latency_under_load()
    {
        _speed.Takes = TimeSpan.FromSeconds(2);
        using var vm = Vm();
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));

        Assert.All(_speed.Calls, c => Assert.Equal(ThroughputBudget.Full(c.Direction), c.Budget));
        Assert.Equal(2, _speed.Calls.Count);
        Assert.Equal(("48.0 Mbps", "3.2 MB"), (vm.Download.Value, vm.Download.Detail));
        Assert.StartsWith("Under load: ", vm.Latency.Detail, StringComparison.Ordinal);
        Assert.Equal(26, vm.ChartSeconds);
    }

    [Fact]
    public async Task Samples_run_about_once_an_hour_and_say_when_the_next_is_due()
    {
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(2, _speed.Calls.Count);

        _time.Advance(TimeSpan.FromMinutes(5));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(2, _speed.Calls.Count);
        Assert.Equal("—", vm.Download.Value);
        Assert.StartsWith("Next sample after 9:58", vm.Download.Detail, StringComparison.Ordinal);
        Assert.Null(_history.Entries[0].DownloadMbps); // newest first

        _time.SetUtcNow(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero)); // the check on the hour samples again
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(4, _speed.Calls.Count);
    }

    [Fact]
    public async Task A_quick_check_speed_test_counts_as_the_hourly_sample()
    {
        using var vm = Vm();
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        Assert.Equal(2, _speed.Calls.Count);

        _time.Advance(TimeSpan.FromMinutes(5));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(2, _speed.Calls.Count);
        Assert.StartsWith("Next sample after ", vm.Upload.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_speed_measured_before_the_app_started_counts_too()
    {
        _history.Entries = [new CheckHistoryEntry(_time.GetUtcNow().AddMinutes(-10), HealthLevel.NoIssue, 90, 20, 18, 2, null, null)];
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Empty(_speed.Calls);
        Assert.StartsWith("Next sample after 9:48", vm.Download.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sample_waits_while_other_apps_use_the_connection()
    {
        _busy = true;
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Empty(_speed.Calls);
        Assert.Equal(("—", "Skipped while other apps used the connection"), (vm.Download.Value, vm.Download.Detail));

        _busy = false; // a skipped sample is not a sample: the next quiet check takes it
        _time.Advance(TimeSpan.FromMinutes(5));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(2, _speed.Calls.Count);
    }

    [Theory]
    [InlineData(true, false, "Skipped on a metered connection")]
    [InlineData(false, true, "Skipped on a mobile network")]
    public async Task Metered_and_mobile_connections_are_never_sampled(bool metered, bool mobile, string detail)
    {
        (_metered, _mobile) = (metered, mobile);
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Empty(_speed.Calls);
        Assert.Equal(detail, vm.Download.Detail);
        Assert.Equal(detail, vm.Upload.Detail);
    }

    [Fact]
    public async Task A_capped_sample_is_a_lower_bound_that_history_keeps_out_of_averages()
    {
        _speed.CappedDownload = true;
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal("≥ 380 Mbps", vm.Download.Value);
        var entry = Assert.Single(_history.Entries);
        Assert.Null(entry.DownloadMbps); // a lower bound would pull averages and plan comparisons down
        Assert.Equal(380.0, entry.DownloadAtLeastMbps);
        Assert.Equal(12.0, entry.UploadMbps);
        Assert.True(entry.LightSample);
        Assert.Contains("download ≥ 380 Mbps", vm.HistoryRows[0].Metrics, StringComparison.Ordinal);
        Assert.StartsWith("Average: download — Mbps; upload 12 Mbps", vm.DailyHistoryRows[0].Speeds, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_direction_too_fast_to_sample_lightly_is_sampled_again_only_after_six_hours()
    {
        _speed.CappedDownload = true;
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(2, _speed.Calls.Count);

        _time.SetUtcNow(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero)); // the next hourly sample: upload only
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(ThroughputDirection.Upload, _speed.Calls[^1].Direction);
        Assert.Equal(3, _speed.Calls.Count);
        Assert.Equal("—", vm.Download.Value);
        Assert.StartsWith("Next sample after 2:58", vm.Download.Detail, StringComparison.Ordinal);
        Assert.Equal("12.0 Mbps", vm.Upload.Value);

        _time.SetUtcNow(new DateTimeOffset(2026, 9, 27, 15, 0, 0, TimeSpan.Zero));
        _speed.CappedDownload = false; // a slower link now settles, so hourly samples resume
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(5, _speed.Calls.Count);
        Assert.Equal("48.0 Mbps", vm.Download.Value);
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 27, 16, 0, 0, TimeSpan.Zero));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Equal(7, _speed.Calls.Count);
    }

    [Fact]
    public async Task Restarting_the_app_keeps_the_hourly_spacing_and_the_back_off()
    {
        _history.Entries = [new CheckHistoryEntry(_time.GetUtcNow().AddMinutes(-10), HealthLevel.NoIssue, null, 12, 18, 2, null, null,
            DownloadAtLeastMbps: 380, LightSample: true)];
        using var vm = Vm();
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        Assert.Empty(_speed.Calls); // the capped sample ten minutes ago still counts as this hour's
        Assert.StartsWith("Next sample after 9:48", vm.Download.Detail, StringComparison.Ordinal);

        _time.SetUtcNow(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        var call = Assert.Single(_speed.Calls);
        Assert.Equal(ThroughputDirection.Upload, call.Direction);
        Assert.StartsWith("Next sample after 2:48", vm.Download.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quick_check_after_a_capped_sample_keeps_the_back_off_across_a_restart()
    {
        var now = _time.GetUtcNow();
        _history.Entries =
        [
            new CheckHistoryEntry(now.AddMinutes(-20), HealthLevel.NoIssue, 930, 12, 18, 2, null, null), // quick check, full test
            new CheckHistoryEntry(now.AddMinutes(-40), HealthLevel.NoIssue, null, 12, 18, 2, null, null, DownloadAtLeastMbps: 380,
                LightSample: true),
        ];
        using var vm = Vm(); // as after a restart: the full test confirms a fast link, it does not end the back-off
        _time.SetUtcNow(now.AddMinutes(40)); // hourly sample due again (an hour after the quick check)
        await RunToEnd(vm.RunCheckAsync(measureSpeed: true));
        var call = Assert.Single(_speed.Calls);
        Assert.Equal(ThroughputDirection.Upload, call.Direction);
        Assert.StartsWith("Next sample after 2:18", vm.Download.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduled_checks_sample_only_while_speed_measurement_is_on()
    {
        var settings = new SettingsViewModel(Localizer.Default, En, backgroundEnabled: true, intervalMinutes: 5, aiReview: false);
        using var vm = Vm(settings);
        _time.Advance(TimeSpan.FromMinutes(5)); // 9:05, the first scheduled check
        await UntilHistory(1);
        Assert.Equal(2, _speed.Calls.Count);
        Assert.All(_speed.Calls, c => Assert.True(c.Budget.StopWhenSettled));

        settings.MeasureSpeed = false;
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 27, 10, 5, 0, TimeSpan.Zero)); // a sample would be due again
        await UntilHistory(2);
        Assert.Equal(2, _speed.Calls.Count);
        Assert.Equal("Not measured", vm.Download.Detail);
    }

    private sealed class SpySpeed(TimeProvider time) : IThroughputProbe
    {
        public List<(ThroughputDirection Direction, ThroughputBudget Budget)> Calls { get; } = [];

        /// <summary>How long each phase takes on the fake clock; latency probes keep running meanwhile.</summary>
        public TimeSpan Takes { get; set; }

        /// <summary>The download spends its byte budget before it settles, so it is only a lower bound.</summary>
        public bool CappedDownload { get; set; }

        public async Task<ThroughputResult> MeasureAsync(ThroughputDirection direction, ThroughputBudget budget, IProgress<double>? liveMbps,
            CancellationToken ct)
        {
            Calls.Add((direction, budget));
            if (Takes > TimeSpan.Zero) await Task.Delay(Takes, time, ct);
            bool down = direction == ThroughputDirection.Download;
            return new ThroughputResult(direction, ProbeStatus.Success, down ? (CappedDownload ? 380 : 48) : 12,
                down ? 3_200_000 : 900_000, Takes, LowerBound: down && CappedDownload);
        }
    }

    private sealed class Probe(double ms) : IProbe
    {
        public ProbeKind Kind => ProbeKind.Tcp;

        public Task<ProbeObservation> ExecuteAsync(ProbeRequest r, CancellationToken ct) => Task.FromResult(new ProbeObservation(r,
            ProbeStatus.Success, Attribution.SocketObserved, IpFamily.IPv4, r.ScheduledUs, r.ScheduledUs + (long)(ms * 1000),
            (long)(ms * 1000), TimingSource.UserMode, null, new EmptyDetail()));
    }

    private sealed class NoStore : IResultStore
    {
        public SavedResult? Load() => null;
        public void Save(SavedResult result) { }
        public void Clear() { }
    }

    private sealed class HistoryStore : ICheckHistoryStore
    {
        public IReadOnlyList<CheckHistoryEntry> Entries { get; set; } = [];
        public IReadOnlyList<CheckHistoryEntry> Load() => Entries;
        public void Save(IReadOnlyList<CheckHistoryEntry> entries) => Entries = entries;
    }
}
