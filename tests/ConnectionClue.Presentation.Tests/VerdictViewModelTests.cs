using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class VerdictViewModelTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly (CultureInfo, CultureInfo) _cultures = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 21, 4, 0, TimeSpan.Zero));
    private readonly Store _store = new();
    private readonly Exporter _exporter = new();

    public VerdictViewModelTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = En;

    public void Dispose() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = _cultures;

    private MainViewModel Vm(PreviewProbes probes) => new(Localizer.Default, En, _time, (_, _) => Task.FromResult(probes), new(),
        new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false, checkSeconds: 10, aiReview: false), _store,
        reportExporter: _exporter);

    private static PreviewProbes Probes(ProbeStatus dns = ProbeStatus.Success, ConnectionMedium medium = ConnectionMedium.WiFi,
        Func<IReadOnlyList<MonitorEvent>>? links = null) =>
        new(new Probe(ProbeStatus.Success, 3), new Probe(ProbeStatus.Success, 20), new Probe(dns, 15), new Probe(ProbeStatus.Success, 60),
            new CheckContext(medium), LinkEvents: links);

    private async Task Run(MainViewModel vm, Action<int>? onSecond = null)
    {
        var check = vm.RunCheckAsync(measureSpeed: false);
        for (int i = 0; i < 60 && !check.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
            onSecond?.Invoke(i + 1);
        }
        await check;
    }

    [Fact]
    public async Task Wifi_drop_leads_with_time_and_duration_and_counts_as_a_problem()
    {
        var events = new List<MonitorEvent>();
        using var vm = Vm(Probes(links: () => events));
        await Run(vm, second =>
        {
            if (second == 4) events.Add(new(_time.GetTimestamp(), EventSource.Wlan, ConnectionEventKind.WlanDisconnected));
            // A failed reconnect attempt is also reported as "connection complete", with a reason code.
            if (second == 5) events.Add(new(_time.GetTimestamp(), EventSource.Wlan, ConnectionEventKind.WlanConnected, ReasonCode: 0x38004));
            if (second == 6) events.Add(new(_time.GetTimestamp(), EventSource.Wlan, ConnectionEventKind.WlanConnected, ReasonCode: 0));
        });

        Assert.Equal("Wi-Fi link dropped at 9:04:04 PM (down for 2 s).", vm.Summary);
        Assert.Equal(HealthLevel.Unhealthy, vm.LastLevel);
        Assert.Equal(RuleId.R01, _store.Value!.Verdicts![0].Rule);
        Assert.StartsWith("Wi-Fi link dropped at", vm.RecommendationFindings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dns_failures_with_working_direct_connections_explain_the_cause()
    {
        using var vm = Vm(Probes(ProbeStatus.Timeout, ConnectionMedium.Ethernet));
        await Run(vm);

        Assert.StartsWith("Name lookup (DNS) failed at 9:04:00 PM (no answer) while direct connections kept working", vm.Summary, StringComparison.Ordinal);
        Assert.Equal(HealthLevel.Unhealthy, vm.LastLevel);

        await vm.ExportHtmlReportCommand.ExecuteAsync(null);
        var report = _exporter.Last!;
        Assert.StartsWith("Name lookup (DNS) failed", report.Findings[0], StringComparison.Ordinal);
        Assert.Contains(report.Findings, f => f.StartsWith("Measurements: download —, upload —, latency 20 ms", StringComparison.Ordinal));
        Assert.Equal("9:04:05 PM (5 s)", SupportReportHtml.TimeText(report, 5));
    }

    [Fact]
    public async Task Healthy_check_says_what_was_observed_without_claiming_more()
    {
        using var vm = Vm(Probes());
        await Run(vm);
        Assert.Equal("No problem observed during this check: the measurements stayed within your limits.", vm.Summary);
        Assert.Equal(HealthLevel.NoIssue, vm.LastLevel);
    }

    [Fact]
    public void Restored_result_renders_saved_verdicts_in_local_time()
    {
        var start = _time.GetUtcNow().AddMinutes(-5);
        _store.Value = new SavedResult(start.AddSeconds(30), new HealthReport(HealthLevel.Degraded, []), new CheckContext(ConnectionMedium.WiFi), [],
            StartedAtUtc: start, Verdicts: [new Verdict(RuleId.R09, 12, Value: 180, Baseline: 20, Router: 3, BeyondRouter: true)]);
        using var vm = new MainViewModel(Localizer.Default, En, _time, (_, _) => throw new NotSupportedException(), new(),
            new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false, aiReview: false), _store);
        Assert.StartsWith("Delay starts beyond your router, so it's your internet provider or farther: at 8:59:12 PM, internet delay rose to 180 ms (usually 20 ms) while your router stayed near 3 ms.",
            vm.RecommendationFindings, StringComparison.Ordinal);
    }

    private sealed class Probe(ProbeStatus status, double ms) : IProbe
    {
        public ProbeKind Kind => ProbeKind.Tcp;

        public Task<ProbeObservation> ExecuteAsync(ProbeRequest r, CancellationToken ct) => Task.FromResult(status == ProbeStatus.Success
            ? new ProbeObservation(r, status, Attribution.SocketObserved, IpFamily.IPv4, r.ScheduledUs, r.ScheduledUs + (long)(ms * 1000),
                (long)(ms * 1000), TimingSource.UserMode, null, new EmptyDetail())
            : Observations.NoDuration(r, status, Attribution.Unknown, null, r.ScheduledUs, r.ScheduledUs + 1000, null));
    }

    private sealed class Store : IResultStore
    {
        public SavedResult? Value { get; set; }
        public SavedResult? Load() => Value;
        public void Save(SavedResult result) => Value = result;
        public void Clear() => Value = null;
    }

    private sealed class Exporter : ISupportReportExporter
    {
        public SupportReportData? Last { get; private set; }

        public Task<bool> ExportHtmlAsync(SupportReportData report, CancellationToken cancellationToken)
        {
            Last = report;
            return Task.FromResult(true);
        }

        public bool PrintPdf(SupportReportData report) => (Last = report) is not null;
    }
}
