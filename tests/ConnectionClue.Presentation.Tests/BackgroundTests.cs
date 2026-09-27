using System.Globalization;
using System.Threading.Channels;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Alerts;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.Scheduling;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public class AlertPolicyTests
{
    private readonly FakeTimeProvider _time = new();

    private static HealthReport Report(HealthLevel level, params IssueKind[] kinds) =>
        new(level, [.. kinds.Select(k => new HealthIssue(k, level, IssueLocation.Unknown, 1))]);

    [Fact]
    public void Alerts_on_change_reminds_hourly_and_reports_recovery()
    {
        var policy = new AlertPolicy(_time);
        Assert.Null(policy.Evaluate(Report(HealthLevel.NoIssue)));
        Assert.Equal(AlertKind.Slow, policy.Evaluate(Report(HealthLevel.Degraded, IssueKind.Delay))?.Kind);
        Assert.Null(policy.Evaluate(Report(HealthLevel.Degraded, IssueKind.Delay)));
        Assert.Null(policy.Evaluate(HealthReport.Inconclusive));

        _time.Advance(AlertPolicy.Reminder);
        var reminder = policy.Evaluate(Report(HealthLevel.Degraded, IssueKind.Delay));
        Assert.Equal(AlertKind.Slow, reminder?.Kind);
        Assert.Equal(_time.GetUtcNow() - AlertPolicy.Reminder, reminder?.FirstSeen);
        var problem = policy.Evaluate(Report(HealthLevel.Unhealthy, IssueKind.Interrupted));
        Assert.Equal(AlertKind.Problem, problem?.Kind);
        Assert.Null(problem?.FirstSeen);

        Assert.Equal(AlertKind.Recovered, policy.Evaluate(Report(HealthLevel.NoIssue))?.Kind);
        Assert.Null(policy.Evaluate(Report(HealthLevel.NoIssue)));
    }

    [Fact]
    public void Background_checks_notify_every_problem_with_its_start_and_recover_once()
    {
        var policy = new AlertPolicy(_time);
        var start = _time.GetUtcNow();
        Assert.Null(policy.Evaluate(Report(HealthLevel.NoIssue), AlertTrigger.Background));
        var first = policy.Evaluate(Report(HealthLevel.Unhealthy, IssueKind.Loss), AlertTrigger.Background);
        Assert.Equal(new AlertDecision(AlertKind.Problem, HealthLevel.Unhealthy), first);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(policy.Evaluate(HealthReport.Inconclusive, AlertTrigger.Background)); // Proves nothing either way.
        var again = policy.Evaluate(Report(HealthLevel.Unhealthy, IssueKind.Loss), AlertTrigger.Background);
        Assert.Equal(new AlertDecision(AlertKind.Problem, HealthLevel.Unhealthy, start), again);
        Assert.Null(policy.Evaluate(Report(HealthLevel.Unhealthy, IssueKind.Loss))); // A manual check keeps the hourly limit.
        Assert.Equal(AlertKind.Slow, policy.Evaluate(Report(HealthLevel.Degraded, IssueKind.Delay), AlertTrigger.Background)?.Kind);

        Assert.Equal(AlertKind.Recovered, policy.Evaluate(Report(HealthLevel.NoIssue), AlertTrigger.Background)?.Kind);
        Assert.Null(policy.Evaluate(Report(HealthLevel.NoIssue), AlertTrigger.Background));
    }

    [Theory]
    [InlineData(HealthLevel.NoIssue)]
    [InlineData(HealthLevel.Degraded)]
    [InlineData(HealthLevel.Unhealthy)]
    [InlineData(HealthLevel.Inconclusive)]
    public void The_reconnect_check_always_reports_its_result(HealthLevel level)
    {
        var policy = new AlertPolicy(_time);
        var report = level == HealthLevel.Inconclusive ? HealthReport.Inconclusive
            : level == HealthLevel.NoIssue ? Report(level) : Report(level, IssueKind.Delay);
        Assert.Equal(new AlertDecision(AlertKind.Reconnected, level), policy.Evaluate(report, AlertTrigger.Reconnect));
        // A problem found after reconnecting is the active one: the next background check says it continues.
        var next = policy.Evaluate(report, AlertTrigger.Background);
        if (level is HealthLevel.Degraded or HealthLevel.Unhealthy) Assert.NotNull(next?.FirstSeen);
        else Assert.Null(next);
    }
}

public class RecurringChecksTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Channel<int> _calls = Channel.CreateUnbounded<int>();

    private RecurringChecks Create() => new(_time, () =>
    {
        _calls.Writer.TryWrite(1);
        return Task.CompletedTask;
    });

    private Task Next() => _calls.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Runs_every_interval()
    {
        using var checks = Create();
        checks.Configure(true, TimeSpan.FromMinutes(15));
        Assert.False(_calls.Reader.TryRead(out _));
        _time.Advance(TimeSpan.FromMinutes(15));
        await Next();
        _time.Advance(TimeSpan.FromMinutes(15));
        await Next();
    }

    [Fact]
    public async Task Run_now_starts_immediately() 
    {
        using var checks = Create();
        checks.Configure(true, TimeSpan.FromMinutes(30), runNow: true);
        await Next();
    }

    [Fact]
    public async Task Disabling_stops_the_schedule()
    {
        using var checks = Create();
        checks.Configure(true, TimeSpan.FromMinutes(10));
        checks.Configure(false, TimeSpan.FromMinutes(10));
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(_calls.Reader.TryRead(out _));
        Assert.False(checks.IsEnabled);
    }

    [Fact]
    public void Rejects_intervals_below_the_minimum()
    {
        using var checks = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => checks.Configure(true, TimeSpan.FromMinutes(2)));
    }
}

public class SettingsViewModelTests
{
    private sealed class NoResults : IResultStore
    {
        public SavedResult? Load() => null;
        public void Save(SavedResult result) { }
        public void Clear() { }
    }

    [Fact]
    public void Snaps_unknown_values_and_localizes_labels()
    {
        var tr = CultureInfo.GetCultureInfo("tr");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = tr;
        try
        {
            var s = new SettingsViewModel(Localizer.Default, tr, intervalMinutes: 7, lossLimitPercent: 5);
            Assert.True(s.BackgroundEnabled);
            Assert.False(s.BackgroundOnMobileEnabled);
            Assert.Equal(5, s.IntervalMinutes);
            Assert.Equal(new HealthThresholds(100, 5, 30), s.Thresholds);
            Assert.Equal([3, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360, 480], s.Intervals.Select(c => c.Value));
            Assert.Equal("15 dakika", s.Intervals.Single(c => c.Value == 15).Label);
            Assert.Equal("1 saat", s.Intervals.Single(c => c.Value == 60).Label);
            Assert.Equal("90 dakika", s.Intervals.Single(c => c.Value == 90).Label);
            Assert.Equal("8 saat", s.Intervals.Single(c => c.Value == 480).Label);
            Assert.Equal("%5", s.LossLimits.Single(c => c.Value == 5).Label);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Mobile_background_schedule_requires_opt_in_and_returns_on_wifi()
    {
        var time = new FakeTimeProvider();
        bool connected = true;
        bool mobile = true;
        var settings = new SettingsViewModel(Localizer.Default, CultureInfo.GetCultureInfo("en-US"),
            backgroundEnabled: true, intervalMinutes: 10);
        using var vm = new MainViewModel(Localizer.Default, CultureInfo.GetCultureInfo("en-US"), time,
            (_, _) => throw new InvalidOperationException("unexpected check"), new(), settings, new NoResults(),
            isConnected: () => connected, isMobileNetwork: () => mobile);

        Assert.False(vm.IsBackgroundCheckScheduled);
        Assert.True(vm.QuickCheckCommand.CanExecute(null));
        settings.BackgroundOnMobileEnabled = true;
        Assert.True(vm.IsBackgroundCheckScheduled);
        settings.BackgroundOnMobileEnabled = false;
        Assert.False(vm.IsBackgroundCheckScheduled);

        vm.UpdateConnectivity(connected: true, mobileNetwork: false);
        Assert.True(vm.IsBackgroundCheckScheduled);
        vm.UpdateConnectivity(connected: false, mobileNetwork: true);
        Assert.False(vm.IsBackgroundCheckScheduled);
    }
}

public class CheckLengthTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");

    [Fact]
    public void Quick_check_defaults_to_30_seconds_and_accepts_only_10_to_60()
    {
        var s = new SettingsViewModel(Localizer.Default, En);
        Assert.Equal(30, s.CheckSeconds);
        Assert.Equal("Between 10 and 60 seconds.", s.CheckLengthHint);
        s.CheckSecondsText = "5";
        Assert.True(s.IsCheckSecondsInvalid);
        Assert.Equal(30, s.CheckSeconds);
        s.CheckSecondsText = "61";
        Assert.True(s.IsCheckSecondsInvalid);
        s.CheckSecondsText = "60";
        Assert.False(s.IsCheckSecondsInvalid);
        Assert.Equal(60, s.CheckSeconds);
        s.CheckSecondsText = "abc";
        s.CommitCheckSecondsText();
        Assert.Equal("60", s.CheckSecondsText);
        Assert.False(s.IsCheckSecondsInvalid);
        Assert.Equal(10, new SettingsViewModel(Localizer.Default, En, checkSeconds: 3).CheckSeconds);
        Assert.Equal(60, new SettingsViewModel(Localizer.Default, En, checkSeconds: 600).CheckSeconds); // Saved by 1.0.6 or earlier.
    }

    [Fact]
    public void Background_check_length_defaults_to_10_seconds_independently_of_quick_checks()
    {
        var s = new SettingsViewModel(Localizer.Default, En, checkSeconds: 45);
        Assert.Equal(10, s.BackgroundCheckSeconds);
        Assert.Equal("10", s.BackgroundCheckSecondsText);
        s.BackgroundCheckSecondsText = "9";
        Assert.True(s.IsBackgroundCheckSecondsInvalid);
        Assert.False(s.IsCheckSecondsInvalid);
        Assert.Equal(10, s.BackgroundCheckSeconds);
        s.BackgroundCheckSecondsText = "25";
        Assert.False(s.IsBackgroundCheckSecondsInvalid);
        Assert.Equal((25, 45), (s.BackgroundCheckSeconds, s.CheckSeconds));
        s.BackgroundCheckSecondsText = "";
        s.CommitBackgroundCheckSecondsText();
        Assert.Equal("25", s.BackgroundCheckSecondsText);
        Assert.False(s.IsBackgroundCheckSecondsInvalid);
        Assert.Equal(60, new SettingsViewModel(Localizer.Default, En, backgroundCheckSeconds: 90).BackgroundCheckSeconds);
    }

    [Fact]
    public void Upgrading_old_settings_moves_only_old_defaults()
    {
        var old = new SettingsViewModel(Localizer.Default, En, intervalMinutes: 15, longCaptureMinutes: 60);
        old.Upgrade(savedVersion: 2);
        Assert.Equal((5, 60), (old.IntervalMinutes, old.LongCaptureMinutes));
        Assert.Equal("", old.GamingTarget); // Version 2 already had default targets: a cleared one stays cleared.

        var older = new SettingsViewModel(Localizer.Default, En, intervalMinutes: 15, longCaptureMinutes: 60);
        older.Upgrade(savedVersion: 0);
        Assert.Equal((5, 15), (older.IntervalMinutes, older.LongCaptureMinutes));
        Assert.NotEqual("", older.GamingTarget);

        var chosen = new SettingsViewModel(Localizer.Default, En, intervalMinutes: 30);
        chosen.Upgrade(savedVersion: 2);
        Assert.Equal(30, chosen.IntervalMinutes);
        var current = new SettingsViewModel(Localizer.Default, En, intervalMinutes: 15);
        current.Upgrade(SettingsViewModel.CurrentVersion);
        Assert.Equal(15, current.IntervalMinutes); // Chosen after upgrading.
    }
}

public class RecommendationsTests
{
    private sealed class MemoryStore(SavedResult? value) : IResultStore
    {
        public SavedResult? Value { get; private set; } = value;
        public SavedResult? Load() => Value;
        public void Save(SavedResult result) => Value = result;
        public void Clear() => Value = null;
    }

    [Fact]
    public void Restores_last_recommendations_with_time_and_staleness_note_then_dismisses()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = en;
        try
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
            var report = new HealthReport(HealthLevel.Degraded,
                [new HealthIssue(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter, 180, 100)]);
            var store = new MemoryStore(new SavedResult(time.GetUtcNow().AddMinutes(-20), report, new CheckContext(ConnectionMedium.WiFi),
                [ActionCode.PauseHouseholdUploads, ActionCode.RestartRouter, ActionCode.ContactIsp]));
            using var vm = new MainViewModel(Localizer.Default, en, time, (_, _) => throw new NotSupportedException(),
                new(), new SettingsViewModel(Localizer.Default, en), store);

            Assert.True(vm.ShowRecommendationsButton);
            Assert.True(vm.IsRecommendationsPage);
            Assert.True(vm.RecommendationsStale);
            Assert.Equal("Based on the check at 9/25/2026 11:40 AM", vm.RecommendationTime);
            Assert.StartsWith("Internet delay was 180 ms (limit 100 ms).", vm.RecommendationFindings);
            Assert.Equal(["1", "2", "3"], vm.Recommendations.Select(r => r.Number));
            Assert.Equal("Pause large uploads and downloads", vm.Recommendations[0].Title);

            vm.DismissRecommendationsCommand.Execute(null);
            Assert.False(vm.ShowRecommendationsButton);
            Assert.True(vm.IsCheckPage);
            Assert.Null(store.Value);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }
}