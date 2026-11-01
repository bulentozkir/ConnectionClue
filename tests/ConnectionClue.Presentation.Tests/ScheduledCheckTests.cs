using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.History;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.Review;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

/// <summary>What a scheduled check may cost, now that it runs every minute: it never replaces a result the user is reading,
/// never waits for the online reviewers, looks up the busy apps only when the link is busy, and rewrites the history file
/// at most every ten minutes.</summary>
public sealed class ScheduledCheckTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly (CultureInfo, CultureInfo) _cultures = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly CountingHistory _history = new();
    private readonly CountingReviewer _reviewer = new();
    private int _starts, _appLookups;
    private bool _busy, _stopStarting;
    private ulong _received;

    public ScheduledCheckTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = En;

    public void Dispose() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = _cultures;

    private static SettingsViewModel Settings(bool background = false, bool aiReview = false) =>
        new(Localizer.Default, En, backgroundEnabled: background, checkSeconds: 10, measureSpeed: false, aiReview: aiReview);

    // A Wi-Fi PC with one weak-signal best practice, so every finished check has advice for the online reviewers to look at.
    private MainViewModel Vm(SettingsViewModel settings, IResultStore? store = null) => new(Localizer.Default, En, _time, (_, _) =>
    {
        Interlocked.Increment(ref _starts);
        if (_stopStarting) throw new InvalidOperationException("stop after the start");
        return Task.FromResult(new PreviewProbes(new Probe(3), new Probe(20), new Probe(15), new Probe(60),
            new CheckContext(ConnectionMedium.WiFi),
            Facts: Task.FromResult(new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 400, SignalBars: 1))),
            Counters: Counters, AppTraffic: Apps));
    }, new(), settings, store ?? new NoStore(), historyStore: _history, reviewer: _reviewer);

    // Each read adds what other apps moved since the previous one: 20 MB (16 Mbps over the 10-second delay phase) when busy.
    private (ulong Received, ulong Sent)? Counters()
    {
        _received += _busy ? 20_000_000UL : 1_000UL;
        return (_received, 0);
    }

    private Task<IReadOnlyList<AppTraffic>> Apps(DateTimeOffset from, DateTimeOffset to)
    {
        _appLookups++;
        return Task.FromResult<IReadOnlyList<AppTraffic>>([new AppTraffic("OneDrive", "onedrive.exe", 1, 14.5)]);
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

    /// <summary>Advances a minute at a time until a scheduled check has started; returns how long that took.</summary>
    private async Task<TimeSpan> MinutesUntilStart(int startsBefore)
    {
        TimeSpan waited = default;
        while (Volatile.Read(ref _starts) == startsBefore && waited < TimeSpan.FromMinutes(15))
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            waited += TimeSpan.FromMinutes(1);
            await Task.Delay(40, TestContext.Current.CancellationToken);
        }
        return waited;
    }

    [Fact]
    public async Task Scheduled_checks_wait_while_the_window_shows_a_result_the_user_started()
    {
        using var vm = Vm(Settings(background: true));
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        Assert.Equal(1, _starts);

        _stopStarting = true; // a scheduled check that does start ends at once
        var waited = await MinutesUntilStart(startsBefore: 1);

        Assert.Equal(2, _starts); // the schedule was alive all along...
        Assert.InRange(waited, MainViewModel.ManualResultHold, MainViewModel.ManualResultHold + TimeSpan.FromMinutes(1)); // ...but held back
    }

    [Fact]
    public async Task Scheduled_checks_carry_on_when_no_window_shows_the_result()
    {
        using var vm = Vm(Settings(background: true));
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        vm.SetWindowVisible(false); // sent to the notification area

        _stopStarting = true;
        var waited = await MinutesUntilStart(startsBefore: 1);

        Assert.Equal(2, _starts);
        Assert.True(waited <= TimeSpan.FromMinutes(1), $"The next scheduled check waited {waited}.");
    }

    [Fact]
    public async Task Only_checks_the_user_starts_contact_the_online_reviewers()
    {
        using var vm = Vm(Settings(aiReview: true));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false)); // a scheduled check
        Assert.NotEmpty(vm.BestPractices); // there is advice to review...
        Assert.Equal(0, _reviewer.Calls); // ...but a scheduled check never waits for the reviewers
        Assert.False(vm.IsReviewing);

        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        Assert.Equal(1, _reviewer.Calls);
    }

    [Fact]
    public async Task A_scheduled_check_keeps_advice_the_reviewers_rejected_out_and_confirmed_advice_marked_without_asking_again()
    {
        using var vm = Vm(Settings(aiReview: true));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.NotEmpty(vm.BestPractices); // nothing is known about this advice yet, so it shows unchecked
        Assert.All(vm.BestPractices, a => Assert.False(a.Checked));

        _reviewer.Known = new ReviewVerdict(true, "Test", "fine", _time.GetUtcNow());
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.NotEmpty(vm.BestPractices);
        Assert.All(vm.BestPractices, a => Assert.True(a.Checked));

        _reviewer.Known = new ReviewVerdict(false, "Test", "wrong", _time.GetUtcNow());
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Empty(vm.BestPractices); // two reviewers called it wrong earlier: it does not come back every minute

        Assert.Equal(0, _reviewer.Calls); // and none of this contacted a reviewer
        Assert.False(vm.IsReviewing);
    }

    [Fact]
    public async Task A_scheduled_result_ends_the_review_of_the_result_it_replaces_so_reconnect_checks_are_not_blocked()
    {
        var report = new HealthReport(HealthLevel.Degraded,
            [new HealthIssue(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter, 180, 100)]);
        var lastSession = new SavedResult(_time.GetUtcNow().AddMinutes(-20), report, new CheckContext(ConnectionMedium.WiFi), [ActionCode.RestartRouter]);
        _reviewer.Pending = new TaskCompletionSource<IReadOnlyDictionary<string, ReviewVerdict>>().Task; // the reviewers never answer
        using var vm = Vm(Settings(aiReview: true), new Store(lastSession));
        Assert.True(vm.IsReviewing); // last session's advice waits for them...

        await RunToEnd(vm.RunCheckAsync(measureSpeed: false)); // ...when a scheduled check replaces it
        Assert.False(vm.IsReviewing);

        int before = Volatile.Read(ref _starts);
        vm.UpdateConnectivity(false);
        vm.UpdateConnectivity(true);
        await RunToEnd(vm.ReconnectCheckTask);
        Assert.Equal(before + 1, _starts); // the reconnect check was not left waiting for a review nobody needs
    }

    [Fact]
    public async Task The_countdown_stops_when_the_window_closes_during_a_check()
    {
        using var vm = Vm(Settings());
        var check = vm.QuickCheckCommand.ExecuteAsync(null);
        for (int i = 0; i < 50 && vm.TimeLeft.Length == 0; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.NotEmpty(vm.TimeLeft);

        vm.SetWindowVisible(false); // the tray tooltip must not keep a frozen countdown
        Assert.Empty(vm.TimeLeft);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.Empty(vm.TimeLeft);
        await RunToEnd(check);
    }

    [Fact]
    public async Task The_per_app_traffic_lookup_runs_only_when_other_traffic_is_heavy()
    {
        using var vm = Vm(Settings());
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Equal(0, _appLookups);

        _busy = true;
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Equal(1, _appLookups);
    }

    [Fact]
    public async Task Scheduled_checks_rewrite_the_history_file_at_most_every_ten_minutes_and_dispose_writes_the_rest()
    {
        var vm = Vm(Settings());
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Equal((1, 1), (_history.Saves, _history.Entries.Count)); // the first one is written at once

        for (int i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        }
        Assert.Equal((1, 1), (_history.Saves, _history.Entries.Count)); // five more checks, no write
        Assert.StartsWith("Saved check summaries: 6.", vm.HistoryCountText, StringComparison.Ordinal); // none is lost

        _time.Advance(MainViewModel.HistorySaveEvery);
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Equal((2, 7), (_history.Saves, _history.Entries.Count));

        _time.Advance(TimeSpan.FromMinutes(1));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Equal((2, 7), (_history.Saves, _history.Entries.Count));
        vm.Dispose();
        Assert.Equal((3, 8), (_history.Saves, _history.Entries.Count)); // closing the app writes what is pending
        vm.Dispose();
        Assert.Equal(3, _history.Saves);
    }

    [Fact]
    public async Task A_check_the_user_starts_writes_the_history_at_once()
    {
        using var vm = Vm(Settings());
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        _time.Advance(TimeSpan.FromMinutes(1));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.Equal((1, 1), (_history.Saves, _history.Entries.Count));

        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        Assert.Equal((2, 3), (_history.Saves, _history.Entries.Count));
    }

    private sealed class Probe(double ms) : IProbe
    {
        public ProbeKind Kind => ProbeKind.Tcp;

        public Task<ProbeObservation> ExecuteAsync(ProbeRequest r, CancellationToken ct) => Task.FromResult(new ProbeObservation(r,
            ProbeStatus.Success, Attribution.SocketObserved, IpFamily.IPv4, r.ScheduledUs, r.ScheduledUs + (long)(ms * 1000),
            (long)(ms * 1000), TimingSource.UserMode, null, new EmptyDetail()));
    }

    private sealed class CountingHistory : ICheckHistoryStore
    {
        public int Saves { get; private set; }
        public IReadOnlyList<CheckHistoryEntry> Entries { get; private set; } = [];
        public IReadOnlyList<CheckHistoryEntry> Load() => Entries;

        public void Save(IReadOnlyList<CheckHistoryEntry> entries)
        {
            Saves++;
            Entries = entries;
        }
    }

    private sealed class CountingReviewer : IAdviceReviewer
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, ReviewVerdict>>? Pending { get; set; }
        public ReviewVerdict? Known { get; set; }

        public Task<IReadOnlyDictionary<string, ReviewVerdict>> ReviewAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            return Pending ?? Task.FromResult<IReadOnlyDictionary<string, ReviewVerdict>>(new Dictionary<string, ReviewVerdict>());
        }

        public IReadOnlyDictionary<string, ReviewVerdict> Remembered(IReadOnlyList<string> texts) =>
            Known is null ? new Dictionary<string, ReviewVerdict>() : texts.Distinct().ToDictionary(t => t, _ => Known);
    }

    private sealed class NoStore : IResultStore
    {
        public SavedResult? Load() => null;
        public void Save(SavedResult result) { }
        public void Clear() { }
    }

    private sealed class Store(SavedResult? value) : IResultStore
    {
        public SavedResult? Load() => value;
        public void Save(SavedResult result) => value = result;
        public void Clear() => value = null;
    }
}
