using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.Review;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class BestPracticeTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly (CultureInfo, CultureInfo) _cultures = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    public BestPracticeTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = En;

    public void Dispose() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = _cultures;

    private MainViewModel Vm(Store store, PreviewProbes? probes = null, IShell? shell = null, IAdviceReviewer? reviewer = null,
        bool aiReview = true, Func<bool>? isConnected = null) => new(Localizer.Default, En, _time,
        (_, _) => probes is null ? throw new NotSupportedException() : Task.FromResult(probes),
        new(), new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false, checkSeconds: 10, aiReview: aiReview),
        store, shell, isConnected: isConnected, reviewer: reviewer);

    private static PreviewProbes Probes(SystemFacts facts, double dnsMs = 20, CheckContext? context = null,
        Func<(ulong Received, ulong Sent)?>? counters = null) =>
        new(new Fake(ProbeKind.Icmp, 3), new Fake(ProbeKind.Tcp, 20), new Fake(ProbeKind.SystemDns, dnsMs), new Fake(ProbeKind.Https, 60),
            context ?? new CheckContext(facts.Adapter?.Medium ?? ConnectionMedium.Unknown), Facts: Task.FromResult(facts), Counters: counters);

    /// <summary>Runs a check on fake time; onSecond runs once per simulated second.</summary>
    private async Task Run(MainViewModel vm, Action<int>? onSecond = null)
    {
        var check = vm.RunCheckAsync(measureSpeed: false);
        for (int i = 0; i < 300 && !check.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
            onSecond?.Invoke(i + 1);
        }
        await check;
    }

    [Fact]
    public async Task Marker_records_the_moment_and_lists_it_in_the_legend()
    {
        using var vm = Vm(new Store(null), Probes(new SystemFacts()));
        await Run(vm, second => { if (second == 3) vm.MarkerCommand.Execute(null); });
        Assert.Equal(3, Assert.Single(vm.Markers), 0.5);
        Assert.Equal("Your lag marks: 3 s", vm.MarkersText);
        Assert.Equal("Marker recorded.", vm.Message);
    }

    [Fact]
    public async Task Traffic_from_other_apps_during_the_idle_phase_becomes_advice()
    {
        var t0 = _time.GetUtcNow();
        (ulong, ulong)? Counters() { double s = (_time.GetUtcNow() - t0).TotalSeconds; return ((ulong)(s * 2_000_000), (ulong)(s * 50_000)); }
        using var vm = Vm(new Store(null), Probes(new SystemFacts(new AdapterFacts(ConnectionMedium.Ethernet, 1000)), counters: Counters));
        await Run(vm);
        var other = Assert.Single(vm.BestPractices);
        Assert.Equal("Other apps used the connection during the check (↓ 16 · ↑ 0.4 Mbps)", other.Title);
        Assert.True(other.Important); // gaming is the default symptom
        Assert.Equal("taskmgr", other.Action!.Target);
    }

    [Fact]
    public void Card_helpers_open_the_right_place_or_copy()
    {
        var shell = new FakeShell();
        var report = new HealthReport(HealthLevel.Unhealthy, [new HealthIssue(IssueKind.Loss, HealthLevel.Unhealthy, IssueLocation.BeyondRouter, 8, 2)]);
        var saved = new SavedResult(_time.GetUtcNow(), report, new CheckContext(ConnectionMedium.WiFi, Gateway: "192.168.1.1"),
            [ActionCode.RestartRouter, ActionCode.ContactIsp],
            [new Advisory(AdvisoryCode.TcpAutoTuningLimited, AdvisoryLevel.Important), new Advisory(AdvisoryCode.WeakWifiSignal, AdvisoryLevel.Important, 2),
             new Advisory(AdvisoryCode.ProxyConfigured, AdvisoryLevel.Suggestion)]);
        using var vm = Vm(new Store(saved), shell: shell);

        Assert.Equal("http://192.168.1.1/", vm.Recommendations[0].Action!.Target);
        Assert.Null(vm.BestPractices.Single(b => b.Title.StartsWith("Weak", StringComparison.Ordinal)).Action); // physical fix only
        var proxy = vm.BestPractices.Single(b => b.Title == "A proxy server is in use").Action!;
        Assert.Equal(("Open Settings", "ms-settings:network-proxy", "Open Settings: A proxy server is in use"), (proxy.Label, proxy.Target, proxy.AccessibleName));

        vm.RunActionCommand.Execute(proxy);
        vm.RunActionCommand.Execute(vm.BestPractices[0].Action);
        vm.RunActionCommand.Execute(vm.Recommendations[1].Action);
        Assert.Equal(["ms-settings:network-proxy"], shell.Opened);
        Assert.Equal("netsh int tcp set global autotuninglevel=normal", shell.Copied[0]);
        Assert.StartsWith("ConnectionClue", shell.Copied[1]);
        Assert.Contains("8%", shell.Copied[1]);
        Assert.Equal("Copied to the clipboard.", vm.RecommendationStatus);
    }

    [Fact]
    public void Every_advisory_has_text() =>
        Assert.All(Enum.GetValues<AdvisoryCode>(), c =>
        {
            Assert.NotEmpty(Localizer.Default.Get($"Advice_{c}_Title", En));
            Assert.NotEmpty(Localizer.Default.Get($"Advice_{c}_Detail", En));
        });

    /// <summary>Every situation-specific wording the advisor can pick, with example names from a PC.</summary>
    public static TheoryData<AdvisoryCode, string?, string[]?> Variants => new()
    {
        { AdvisoryCode.LatencyUnderLoad, "Download", null }, { AdvisoryCode.LatencyUnderLoad, "Upload", null },
        { AdvisoryCode.OtherTraffic, "OneDrive", ["Microsoft OneDrive", "#12.5", "Visual Studio Code", "#3.1"] },
        { AdvisoryCode.OtherTraffic, "Sync", ["Dropbox", "#8"] }, { AdvisoryCode.OtherTraffic, "Game", ["Steam", "#420"] },
        { AdvisoryCode.OtherTraffic, "Browser", ["Microsoft Edge", "#6.2"] }, { AdvisoryCode.OtherTraffic, "Windows", ["@App_Windows", "#20"] },
        { AdvisoryCode.OtherTraffic, "App", ["SmartTicker", "#9.3"] },
        { AdvisoryCode.WeakWifiSignal, null, ["HomeNet", "Intel(R) Wi-Fi 6E AX210 160MHz"] },
        { AdvisoryCode.WifiLinkSlow, "Capable", ["HomeNet", "Intel(R) Wi-Fi 6E AX210 160MHz", "Wi-Fi 6E"] },
        { AdvisoryCode.WifiLinkSlow, "Legacy", ["HomeNet", "Realtek RTL8188EU", "Wi-Fi 4"] }, { AdvisoryCode.WifiLinkSlow, null, ["HomeNet", "Adapter", ""] },
        { AdvisoryCode.EthernetLinkSlow, null, ["Realtek PCIe GbE"] }, { AdvisoryCode.AdapterPowerSaving, null, ["Intel(R) Wi-Fi 6E AX210"] },
        { AdvisoryCode.UsbSelectiveSuspend, "Plugged", ["Lenovo USB Ethernet"] }, { AdvisoryCode.UsbSelectiveSuspend, "Battery", ["Lenovo USB Ethernet"] },
        { AdvisoryCode.EnergyEfficientEthernet, null, ["Realtek PCIe GbE"] },
        { AdvisoryCode.PowerSaving, "EnergySaver", null }, { AdvisoryCode.PowerSaving, "Efficiency", null },
        { AdvisoryCode.PowerSaving, "WifiBattery", ["@WifiPower_2"] }, { AdvisoryCode.PowerSaving, "WifiPlugged", ["@WifiPower_3"] },
        { AdvisoryCode.SlowDns, "Router", ["192.168.1.1"] }, { AdvisoryCode.SlowDns, "Other", ["203.0.113.53"] },
        { AdvisoryCode.OldNetworkDriver, "Intel", ["Intel(R) Wi-Fi 6 AX201", "22.1.0.3"] }, { AdvisoryCode.OldNetworkDriver, null, ["Realtek PCIe GbE", "10.50.0"] },
        { AdvisoryCode.MeteredConnection, null, ["HomeNet"] },
        { AdvisoryCode.ProxyConfigured, "Local", ["127.0.0.1:8888"] }, { AdvisoryCode.ProxyConfigured, "Remote", ["proxy.example:8080"] },
        { AdvisoryCode.VpnActive, null, ["WireGuard Tunnel"] }, { AdvisoryCode.VpnActive, null, null },
        { AdvisoryCode.TcpAutoTuningLimited, null, ["disabled"] },
        { AdvisoryCode.Ipv6Disabled, "Registry", ["0xFF"] }, { AdvisoryCode.Ipv6Disabled, "Adapter", ["Intel(R) Wi-Fi 6E AX210"] },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_specific_wording_renders_in_every_language(AdvisoryCode code, string? variant, string[]? args)
    {
        var saved = new SavedResult(_time.GetUtcNow(), new HealthReport(HealthLevel.NoIssue, []), new CheckContext(Gateway: "192.168.1.1"), [],
            [new Advisory(code, AdvisoryLevel.Suggestion, code == AdvisoryCode.OldNetworkDriver ? 202106 : 12, 3, args, variant)]);
        foreach (var culture in SupportedLanguages.All)
        {
            using var vm = new MainViewModel(Localizer.Default, culture, _time, (_, _) => throw new NotSupportedException(), new(),
                new SettingsViewModel(Localizer.Default, culture, backgroundEnabled: false), new Store(saved));
            var item = Assert.Single(vm.BestPractices);
            Assert.False(string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Detail), culture.Name);
            Assert.DoesNotContain("{", item.Title + item.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("@", item.Title + item.Detail, StringComparison.Ordinal);
            if (variant is not null)
                Assert.True(Localizer.Default.Find($"Advice_{code}_Detail{variant}", culture) is not null
                    || Localizer.Default.Find($"Advice_{code}_Title{variant}", culture) is not null, $"{culture.Name}: {code}/{variant}");
            if (args is { Length: > 0 } && args[0] is ['@', ..]) continue;
            if (args is { Length: > 0 } && args[0].Length > 0 && code is not (AdvisoryCode.PowerSaving or AdvisoryCode.OtherTraffic))
                Assert.Contains(args[0], item.Title + item.Detail, StringComparison.Ordinal); // the name from this PC is shown
        }
    }

    [Fact]
    public void Fixes_use_the_evidence_this_pc_or_other_devices()
    {
        var report = new HealthReport(HealthLevel.Degraded, [new HealthIssue(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter, 180, 100)]);
        var busy = new SavedResult(_time.GetUtcNow(), report, new CheckContext(Gateway: "192.168.1.1"), [ActionCode.PauseHouseholdUploads, ActionCode.RestartRouter],
            [new Advisory(AdvisoryCode.OtherTraffic, AdvisoryLevel.Important, 0.3, 2.2, ["Microsoft OneDrive", "#14.5"], "OneDrive")]);
        using var vm = Vm(new Store(busy));
        Assert.Equal("On this PC, Microsoft OneDrive used 14.5 MB during the check. Pause it first, then check again.", vm.Recommendations[0].Detail);
        Assert.Contains("192.168.1.1", vm.Recommendations[1].Detail, StringComparison.Ordinal);
        Assert.Equal("OneDrive was syncing during the check (14.5 MB)", vm.BestPractices[0].Title);

        var quiet = busy with { Advisories = [], Passed = [CheckArea.OtherTraffic] };
        using var vm2 = Vm(new Store(quiet));
        Assert.StartsWith("This PC was quiet during the check", vm2.Recommendations[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Healthy_check_saves_best_practices_and_previews_the_important_one()
    {
        var store = new Store(null);
        var facts = new SystemFacts(new AdapterFacts(ConnectionMedium.WiFi, 400, SignalBars: 1));
        using var vm = Vm(store, new PreviewProbes(new Fake(ProbeKind.Icmp, 3), new Fake(ProbeKind.Tcp, 20),
            new Fake(ProbeKind.SystemDns, 400), new Fake(ProbeKind.Https, 60), new CheckContext(ConnectionMedium.WiFi),
            Facts: Task.FromResult(facts)));

        var check = vm.RunCheckAsync(measureSpeed: false);
        for (int i = 0; i < 300 && !check.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await check;

        Assert.Equal(HealthLevel.NoIssue, vm.LastLevel);
        Assert.Empty(store.Value!.Actions);
        Assert.Equal([AdvisoryCode.WeakWifiSignal, AdvisoryCode.SlowDns], store.Value.Advisories!.Select(a => a.Code));
        Assert.True(vm is { HasRecommendations: true, HasBestPractices: true, HasFixes: false, RecommendationCount: 2 });
        Assert.Equal("Worth checking: Weak Wi-Fi signal (1 of 5 bars)", vm.TryPreview);
        Assert.Equal((true, "Important"), (vm.BestPractices[0].Important, vm.BestPractices[0].Level));
        Assert.Equal("Name lookups are slow (400 ms)", vm.BestPractices[1].Title);
        Assert.StartsWith("No connection issues were found.", vm.RecommendationFindings);
        Assert.Equal([typeof(FindingsNote), typeof(SectionHeading), typeof(AdviceItem), typeof(AdviceItem), typeof(CheckedNote)],
            vm.RecommendationBlocks.Select(b => b.GetType()));
        Assert.Equal("Checked, no change needed: link speed, power saving, proxy, VPN, metered connection, IPv6", vm.CheckedOkText);
        Assert.False(((FindingsNote)vm.RecommendationBlocks[0]).Warning);
    }

    [Fact]
    public async Task Check_without_speed_phase_keeps_latency_under_load_advice()
    {
        var store = new Store(new SavedResult(_time.GetUtcNow(), new HealthReport(HealthLevel.NoIssue, []), new CheckContext(), [],
            [new Advisory(AdvisoryCode.LatencyUnderLoad, AdvisoryLevel.Important, 220)]));
        using var vm = Vm(store, new PreviewProbes(new Fake(ProbeKind.Icmp, 3), new Fake(ProbeKind.Tcp, 20),
            new Fake(ProbeKind.SystemDns, 20), new Fake(ProbeKind.Https, 60), new CheckContext(ConnectionMedium.Ethernet),
            Facts: Task.FromResult(new SystemFacts(new AdapterFacts(ConnectionMedium.Ethernet, 1000)))));

        var check = vm.RunCheckAsync(measureSpeed: false);
        for (int i = 0; i < 300 && !check.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await check;

        Assert.Equal(HealthLevel.NoIssue, vm.LastLevel);
        Assert.Equal(AdvisoryCode.LatencyUnderLoad, Assert.Single(store.Value!.Advisories!).Code);
        Assert.Equal("Worth checking: Latency rises by 220 ms under load", vm.TryPreview);
    }

    [Fact]
    public void Restored_best_practices_render_levels_driver_month_and_vpn()
    {
        var saved = new SavedResult(_time.GetUtcNow(), new HealthReport(HealthLevel.NoIssue, []), new CheckContext(), [],
            [new Advisory(AdvisoryCode.VpnActive, AdvisoryLevel.Suggestion), new Advisory(AdvisoryCode.OldNetworkDriver, AdvisoryLevel.Suggestion, 202106)]);
        using var vm = Vm(new Store(saved));

        Assert.True(vm.IsRecommendationsPage);
        Assert.Equal("A VPN is routing your traffic", vm.BestPractices[0].Title); // unnamed VPN: generic wording
        Assert.Equal("Network driver is from June 2021", vm.BestPractices[1].Title);
        Assert.StartsWith("Suggestion: Network driver is from June 2021. ", vm.BestPractices[1].AccessibleText);
        Assert.Empty(vm.Recommendations);
    }

    [Fact]
    public void Results_saved_before_best_practices_still_load()
    {
        using var vm = Vm(new Store(new SavedResult(_time.GetUtcNow(), new HealthReport(HealthLevel.Degraded,
            [new HealthIssue(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter, 180, 100)]),
            new CheckContext(), [ActionCode.RestartRouter])));
        Assert.Empty(vm.BestPractices);
        Assert.Equal([typeof(SectionHeading), typeof(FindingsNote), typeof(RecommendationItem)], vm.RecommendationBlocks.Select(b => b.GetType()));
        Assert.Equal(1, vm.RecommendationCount);
    }

    [Fact]
    public void Offline_saved_recommendations_skip_online_review_and_are_marked_unchecked()
    {
        var reviewer = new FakeReviewer(_ => true);
        using var vm = Vm(new Store(Specific()), reviewer: reviewer, isConnected: () => false);

        Assert.Empty(reviewer.Seen);
        Assert.Equal("The online AI check was not available, so these recommendations are shown unchecked.", vm.ReviewStatus);
        Assert.All(vm.BestPractices, advice => Assert.False(advice.Checked));
    }

    private sealed class Fake(ProbeKind kind, double ms) : IProbe
    {
        public ProbeKind Kind => kind;

        public Task<ProbeObservation> ExecuteAsync(ProbeRequest r, CancellationToken ct) => Task.FromResult(new ProbeObservation(
            r, ProbeStatus.Success, Attribution.SocketObserved, IpFamily.IPv4, r.ScheduledUs, r.ScheduledUs + (long)(ms * 1000),
            (long)(ms * 1000), TimingSource.UserMode, null, new EmptyDetail()));
    }

    private sealed class FakeShell : IShell
    {
        public List<string> Opened { get; } = [];
        public List<string> Copied { get; } = [];
        public void Open(string target) => Opened.Add(target);
        public void Copy(string text) => Copied.Add(text);
    }

    private sealed class FakeReviewer(Func<string, bool?> decide) : IAdviceReviewer
    {
        public List<string> Seen { get; } = [];

        public Task<IReadOnlyDictionary<string, ReviewVerdict>> ReviewAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Seen.AddRange(texts);
            var result = new Dictionary<string, ReviewVerdict>();
            foreach (var t in texts)
                if (decide(t) is { } ok) result[t] = new(ok, "Fake", "", DateTimeOffset.UnixEpoch);
            return Task.FromResult<IReadOnlyDictionary<string, ReviewVerdict>>(result);
        }
    }

    private SavedResult Specific() => new(_time.GetUtcNow(),
        new HealthReport(HealthLevel.Degraded, [new HealthIssue(IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter, 180, 100)]),
        new CheckContext(ConnectionMedium.WiFi, Gateway: "192.168.1.1"), [ActionCode.PauseHouseholdUploads, ActionCode.RestartRouter, ActionCode.ContactIsp],
        [new Advisory(AdvisoryCode.OtherTraffic, AdvisoryLevel.Important, 0.3, 2.2, ["Microsoft OneDrive", "#13.8", "Visual Studio Code", "#3.4"], "OneDrive"),
         new Advisory(AdvisoryCode.WeakWifiSignal, AdvisoryLevel.Important, 2, 108, ["yavas", "Intel(R) Wi-Fi 6E AX210 160MHz"]),
         new Advisory(AdvisoryCode.SlowDns, AdvisoryLevel.Suggestion, 320, 0, ["192.168.1.1"], "Router")]);

    [Fact]
    public void Online_review_hides_rejected_advice_marks_confirmed_and_sends_nothing_personal()
    {
        var reviewer = new FakeReviewer(text => !text.Contains("Name lookups", StringComparison.Ordinal));
        using var vm = Vm(new Store(Specific()), reviewer: reviewer);

        Assert.Equal(6, reviewer.Seen.Count);
        foreach (var personal in new[] { "yavas", "192.168.1.1", "Visual Studio Code", "Microsoft OneDrive", "Intel(R)", "13.8", "108", "180 ms" })
            Assert.DoesNotContain(reviewer.Seen, t => t.Contains(personal, StringComparison.Ordinal));
        Assert.Contains(reviewer.Seen, t => t.Contains("Weak Wi-Fi signal on “[name]” (N of 5 bars)", StringComparison.Ordinal));
        Assert.Contains(reviewer.Seen, t => t.Contains("[address]", StringComparison.Ordinal));

        Assert.Equal(["OneDrive was syncing during the check (13.8 MB)", "Weak Wi-Fi signal on “yavas” (2 of 5 bars)"], vm.BestPractices.Select(b => b.Title));
        Assert.All(vm.BestPractices, b => Assert.True(b.Checked));
        Assert.All(vm.Recommendations, r => Assert.True(r.Checked));
        Assert.Equal(5, vm.RecommendationCount);
        Assert.Equal("Checked by an online AI (Fake). Some advice was held back because two online AI reviewers flagged it as incorrect.", vm.ReviewStatus);
        Assert.False(vm.IsReviewing);
    }

    [Fact]
    public void Without_a_reviewer_answer_the_advice_is_shown_unchecked()
    {
        using var vm = Vm(new Store(Specific()), reviewer: new FakeReviewer(_ => null));
        Assert.Equal(3, vm.BestPractices.Count);
        Assert.All(vm.BestPractices, b => Assert.False(b.Checked));
        Assert.Equal("The online AI check was not available, so these recommendations are shown unchecked.", vm.ReviewStatus);
    }

    [Fact]
    public void With_the_review_off_nothing_is_sent()
    {
        var reviewer = new FakeReviewer(_ => true);
        using var vm = Vm(new Store(Specific()), reviewer: reviewer, aiReview: false);
        Assert.Empty(reviewer.Seen);
        Assert.Equal("", vm.ReviewStatus);
        Assert.Equal(3, vm.BestPractices.Count);
    }

    private sealed class Store(SavedResult? value) : IResultStore
    {
        public SavedResult? Value { get; private set; } = value;
        public SavedResult? Load() => Value;
        public void Save(SavedResult result) => Value = result;
        public void Clear() => Value = null;
    }
}
