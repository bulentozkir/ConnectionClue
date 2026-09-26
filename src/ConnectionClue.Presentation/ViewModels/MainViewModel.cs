using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.Alerts;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.History;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.Review;
using ConnectionClue.Presentation.Scheduling;
using ConnectionClue.Presentation.Updates;

namespace ConnectionClue.Presentation.ViewModels;

public enum LaneState { Waiting, Responding, Slow, NotResponding }

public enum AppPage { Check, Recommendations, Insights, Settings }

/// <summary>What the status panel shows; the view maps each state to an icon and colour (never colour alone).
/// Disconnected: no network at all, so no check runs.</summary>
public enum HeroState { Idle, Running, NoIssue, Inconclusive, Degraded, Unhealthy, Disconnected }

/// <summary>A "What's happening?" choice. Help explains it (tooltip and UIA help text); Key picks its theme colour.</summary>
public sealed record SymptomOption(string Key, string Label, string Glyph, string Help = "");
public sealed record LanguageOption(string Name, string NativeName);
public sealed record Announcement(string Text, AnnouncementUrgency Urgency);
public sealed record Alert(AlertKind Kind, string Title, string Body);

public enum RecommendationActionKind { Open, CopyText, CopySummary }

/// <summary>A one-click helper on a recommendation: open the right Settings page or tool, or copy a command or a summary.
/// AccessibleName adds the card title, because several cards can offer the same label.</summary>
public sealed record RecommendationAction(RecommendationActionKind Kind, string Label, string Target, string AccessibleName);

/// <summary>Opens Settings pages and tools, and copies text. Implemented by the app, which accepts only its own fixed targets.</summary>
public interface IShell
{
    void Open(string target);
    void Copy(string text);
}

public sealed record RecommendationItem(string Number, string Title, string Detail, RecommendationAction? Action = null, bool Checked = false)
{
    public string AccessibleText => $"{Number}. {Title}. {Detail}";
}

/// <summary>A best-practice card. Level is spelled out, so importance never relies on colour or icon alone.
/// Checked: an online AI reviewer confirmed the advice.</summary>
public sealed record AdviceItem(bool Important, string Level, string Title, string Detail, RecommendationAction? Action = null, bool Checked = false)
{
    public string AccessibleText => $"{Level}: {Title}. {Detail}";
}

/// <summary>Section heading in the recommendations flow.</summary>
public sealed record SectionHeading(string Text);

/// <summary>What the check found (Warning), or the note that no issues were found above best practices.</summary>
public sealed record FindingsNote(string Text, bool Warning);

/// <summary>"Checked, no change needed: …" closing the recommendations.</summary>
public sealed record CheckedNote(string Text);
public sealed record HistoryListItem(string Time, string Status, string Metrics, string AccessibleText);
public sealed record HistoryDayItem(string Day, string Checks, string Quality, string Speeds, string AccessibleText);

/// <summary>One headline metric (download, upload, latency, variation) with a live value and a detail line. Key picks the
/// tile's theme colours; Help explains the metric (tooltip and UIA help text).</summary>
public sealed partial class MetricTile : ObservableObject
{
    public MetricTile(string key, string glyph, string label, string help = "")
    {
        (Key, Glyph, Label, Help) = (key, glyph, label, help);
        (Value, Detail) = ("—", "");
    }

    public string Key { get; }
    public string Glyph { get; }
    public string Label { get; }
    public string Help { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AccessibleText))]
    public partial string Value { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AccessibleText))]
    public partial string Detail { get; set; }

    public string AccessibleText => Detail.Length == 0 ? $"{Label}: {Value}" : $"{Label}: {Value}. {Detail}";
}

/// <summary>Probe set and path facts for one session; disposed when the session ends. Facts (read-only OS and driver
/// settings) are gathered in parallel with the check and awaited only when it completes. Counters reads this PC's
/// interface byte counters, which show traffic from other apps during the idle phase; AppTraffic names those apps.
/// LinkEvents returns link and Wi-Fi connect/disconnect notifications for the probed interface (R01 evidence); Monitors
/// owns whatever produces them.</summary>
public sealed record PreviewProbes(IProbe Icmp, IProbe Tcp, IProbe Dns, IProbe Https, CheckContext Context,
    IThroughputProbe? Throughput = null, Task<SystemFacts>? Facts = null, Func<(ulong Received, ulong Sent)?>? Counters = null,
    Func<DateTimeOffset, DateTimeOffset, Task<IReadOnlyList<AppTraffic>>>? AppTraffic = null,
    Func<IReadOnlyList<MonitorEvent>>? LinkEvents = null, IDisposable? Monitors = null) : IDisposable
{
    public void Dispose()
    {
        foreach (var p in new object?[] { Icmp, Tcp, Dns, Https, Throughput, Monitors }) (p as IDisposable)?.Dispose();
    }
}

public sealed partial class LaneViewModel : ObservableObject
{
    private readonly Localizer _l;
    private readonly CultureInfo _ui;
    private readonly ProbeKind _chartKind;
    private readonly List<Sample> _samples = [], _chart = [];

    public LaneViewModel(string nameKey, string deviceGlyph, ProbeKind chartKind, Localizer l, CultureInfo ui)
    {
        (_l, _ui, _chartKind, DeviceGlyph) = (l, ui, chartKind, deviceGlyph);
        Name = l.Get(nameKey, ui);
        Help = l.Get(nameKey + "_Help", ui);
        Reset();
    }

    public string Name { get; }

    /// <summary>What this step and its chart row measure (tooltip and UIA help text).</summary>
    public string Help { get; }

    /// <summary>Segoe Fluent Icons glyph for the path step.</summary>
    public string DeviceGlyph { get; }

    /// <summary>Every answered or unanswered check, for health evaluation.</summary>
    public IReadOnlyList<Sample> Samples => _samples;

    /// <summary>One measurement kind plus every failure, so a chart row never mixes quantities.</summary>
    public IReadOnlyList<Sample> ChartSamples => _chart;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusGlyph))]
    public partial LaneState State { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AccessibleText))]
    public partial string StatusText { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AccessibleText))]
    public partial string LatencyText { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AccessibleText))]
    public partial string CountText { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ChartText))]
    public partial string StatsText { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScaleText))]
    public partial double ScaleMaxMs { get; set; }

    /// <summary>User delay limit drawn on this chart row; 0 = none.</summary>
    [ObservableProperty]
    public partial double LimitMs { get; set; }

    /// <summary>Bumped on every data change so the chart row redraws.</summary>
    [ObservableProperty]
    public partial int Version { get; set; }

    /// <summary>Status badge glyph; always shown with StatusText, never colour alone.</summary>
    public string StatusGlyph => State switch
    {
        LaneState.Responding => "\uE73E",
        LaneState.Slow => "\uE7BA",
        LaneState.NotResponding => "\uE711",
        _ => "\uE916",
    };

    public string AccessibleText => $"{Name}: {StatusText}. {LatencyText} {CountText}".TrimEnd();
    public string ChartText => $"{Name}. {StatsText}";
    public string ScaleText => Ms(ScaleMaxMs);

    partial void OnLimitMsChanged(double value) => Refresh();

    public void Reset()
    {
        _samples.Clear();
        _chart.Clear();
        State = LaneState.Waiting;
        StatusText = _l.Get("Status_Waiting", _ui);
        LatencyText = "—";
        CountText = "";
        Refresh();
    }

    public void Record(ProbeObservation o, double seconds)
    {
        if (o.Status is ProbeStatus.Skipped or ProbeStatus.Cancelled) return;
        bool ok = o.Status == ProbeStatus.Success;
        var sample = new Sample(seconds, ok && o.DurationUs is { } us ? us / 1000.0 : null);
        _samples.Add(sample);
        if (o.Request.Stream.Kind == _chartKind || !ok) _chart.Add(sample);
        State = ok ? LaneState.Responding : LaneState.NotResponding;
        StatusText = _l.Get(ok ? "Status_Responding" : "Status_NotResponding", _ui);
        LatencyText = Ms(sample.Milliseconds);
        CountText = string.Format(CultureInfo.CurrentCulture, "{0}/{1}", _samples.Count(x => x.Answered), _samples.Count);
        Refresh();
    }

    /// <summary>After a check: the step shows the evaluated verdict and its median, not the last sample.</summary>
    public void Finish(LaneState verdict, double idleUntilSeconds = double.MaxValue)
    {
        if (_samples.Count == 0) return;
        State = verdict;
        StatusText = _l.Get(verdict switch
        {
            LaneState.Responding => "Status_Responding",
            LaneState.Slow => "Status_Slow",
            LaneState.NotResponding => "Status_NotResponding",
            _ => "Status_Waiting",
        }, _ui);
        LatencyText = Ms(StepStatistics.From([.. _chart.Where(s => s.Seconds < idleUntilSeconds)]).MedianMs);
    }

    private void Refresh()
    {
        var s = StepStatistics.From(_chart);
        StatsText = string.Format(_ui, _l.Get("Stats_Summary", _ui), Ms(s.MedianMs), Ms(s.P95Ms), Ms(s.VariationMs),
            string.Format(CultureInfo.CurrentCulture, "{0}/{1}", s.Failed, s.Sent));
        var values = _chart.Where(x => x.Answered).Select(x => x.Milliseconds!.Value).Order().ToArray();
        double typicalHigh = values.Length == 0 ? 0 : StepStatistics.NearestRank(values, 0.95);
        ScaleMaxMs = NiceCeiling(Math.Max(10, Math.Max(typicalHigh * 1.3, LimitMs * 1.15)));
        Version++;
    }

    private static string Ms(double? v) => v is { } x ? string.Format(CultureInfo.CurrentCulture, "{0:0} ms", x) : "—";

    private static readonly double[] NiceSteps = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];

    /// <summary>Scale top split into 4 grid steps, each a round whole number (e.g. 240 → 60/120/180, never 62.5).</summary>
    public static double NiceCeiling(double v)
    {
        double raw = Math.Max(v, 1) / 4, magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = NiceSteps.Select(n => n * magnitude).First(s => s >= raw - 1e-9 && (magnitude < 1 || Math.Abs(s % 1) < 1e-9));
        return step * 4;
    }
}

/// <summary>
/// Home, capture, recommendations and settings. The 1 s loop stands in for Capture.SessionController until WP4 lands.
/// Manual and background checks share one engine and never overlap.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Recommendations older than this (or from an earlier app session) carry a "may have changed" note.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private readonly Localizer _l;
    private readonly CultureInfo _ui;
    private readonly TimeProvider _time;
    private readonly Func<SessionClock, CancellationToken, Task<PreviewProbes>> _startSession;
    private readonly AnnouncementPolicy _announcements;
    private readonly AlertPolicy _alerts;
    private readonly RecurringChecks _recurring;
    private readonly IResultStore _results;
    private CancellationTokenSource? _run;
    private CheckContext _context = new();
    private SavedResult? _saved;
    private bool _savedFromPreviousSession;
    private long _start;

    /// <summary>Length of each speed phase (download, then upload).</summary>
    public static readonly TimeSpan SpeedPhase = TimeSpan.FromSeconds(8);
    private double _idleSeconds = double.MaxValue;
    private (double From, double To)? _downloadWindow, _uploadWindow;
    private readonly List<double> _dnsMs = [];
    private (double Down, double Up)? _otherTraffic;
    private double? _downloadMbps, _uploadMbps, _loadedDownMs, _loadedUpMs;
    private DateTimeOffset _checkStartedUtc;
    private readonly IShell? _shell;
    private readonly Func<bool>? _isConnected;
    private readonly IAdviceReviewer? _reviewer;
    private readonly IUpdateChecker? _updateChecker;
    private readonly bool _storeManagedUpdates;
    private readonly IServiceTargetProbe? _serviceTargetProbe;
    private readonly ICheckHistoryStore? _historyStore;
    private readonly Func<bool>? _isMobileNetwork;
    private readonly INetworkDiagnostics? _networkDiagnostics;
    private readonly ISupportReportExporter? _reportExporter;
    private IReadOnlyList<DnsResolverMeasurement> _dnsMeasurements = [];
    private IReadOnlyList<CheckHistoryEntry> _history = [];
    private int _reviewVersion;
    private DateTimeOffset? _lastCheckedAtUtc;
    private IReadOnlyList<ActionCode> _shownActions = [];
    private IReadOnlyList<Advisory> _shownAdvice = [];
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Intel's driver update tool; the one vendor page a recommendation may open.</summary>
    public const string IntelDriverPage = "https://www.intel.com/content/www/us/en/support/detect.html";

    public MainViewModel(Localizer l, CultureInfo ui, TimeProvider time,
        Func<SessionClock, CancellationToken, Task<PreviewProbes>> startSession, AccessibilityPreferences preferences,
        SettingsViewModel settings, IResultStore results, IShell? shell = null, Func<bool>? isConnected = null,
        IAdviceReviewer? reviewer = null, ICheckHistoryStore? historyStore = null, IUpdateChecker? updateChecker = null,
        bool storeManagedUpdates = false, IServiceTargetProbe? serviceTargetProbe = null, Func<bool>? isMobileNetwork = null,
        INetworkDiagnostics? networkDiagnostics = null, ISupportReportExporter? reportExporter = null)
    {
        (_l, _ui, _time, _startSession, _results, _shell, _isConnected, _reviewer) = (l, ui, time, startSession, results, shell, isConnected, reviewer);
        (_updateChecker, _storeManagedUpdates, _serviceTargetProbe) = (updateChecker, storeManagedUpdates, serviceTargetProbe);
        (_isMobileNetwork, _networkDiagnostics, _reportExporter) = (isMobileNetwork, networkDiagnostics, reportExporter);
        _historyStore = historyStore;
        Settings = settings;
        IsMobileNetwork = _isMobileNetwork?.Invoke() ?? false;
        _announcements = new AnnouncementPolicy(time, preferences.Verbosity);
        _alerts = new AlertPolicy(time);
        Lanes =
        [
            new("Lane_Local", "\uE80F", ProbeKind.Icmp, l, ui),
            new("Lane_Internet", "\uE774", ProbeKind.Tcp, l, ui),
            new("Lane_Services", "\uE753", ProbeKind.Https, l, ui),
        ];
        Symptoms =
        [
            new("Symptom_Gaming", l.Get("Symptom_Gaming", ui), "\uE7FC", l.Get("Symptom_Gaming_Help", ui)),
            new("Symptom_Video", l.Get("Symptom_Video", ui), "\uE714", l.Get("Symptom_Video_Help", ui)),
            new("Symptom_Calls", l.Get("Symptom_Calls", ui), "\uE717", l.Get("Symptom_Calls_Help", ui)),
            new("Symptom_Disconnects", l.Get("Symptom_Disconnects", ui), "\uE703", l.Get("Symptom_Disconnects_Help", ui)),
        ];
        Languages = [.. SupportedLanguages.All.Select(c => new LanguageOption(c.Name, c.NativeName))];
        SelectedSymptom = Symptoms[0];
        CurrentLanguage = ui.Name;
        TimeLeft = Message = LastResult = LimitLegend = RecommendationTime = RecommendationFindings = "";
        MarkersText = CheckedOkText = RecommendationStatus = ReviewStatus = "";
        Markers = [];
        Recommendations = [];
        BestPractices = [];
        RecommendationBlocks = [];
        ChartSeconds = settings.CheckSeconds;
        Summary = l.Get("Home_Subtitle", ui);
        Download = new("Download", "\uE896", l.Get("Metric_Download", ui), l.Get("Metric_Download_Help", ui));
        Upload = new("Upload", "\uE898", l.Get("Metric_Upload", ui), l.Get("Metric_Upload_Help", ui));
        Latency = new("Latency", "\uE916", l.Get("Metric_Latency", ui), l.Get("Metric_Latency_Help", ui));
        Variation = new("Variation", "\uE8B1", l.Get("Metric_Variation", ui), l.Get("Metric_Variation_Help", ui));
        Metrics = [Download, Upload, Latency, Variation];
        HeroTitle = l.Get("Home_Title", ui);
        TryPreview = "";
        BufferbloatText = l.Get("Bufferbloat_NotMeasured", ui);
        _history = CheckHistoryAnalytics.Retain(historyStore?.Load() ?? [], _time.GetUtcNow());
        RefreshHistory();
        ApplyLimit();
        if (results.Load() is { } saved) _ = ShowSavedAsync(saved, previousSession: true, reveal: true);
        settings.PropertyChanged += OnSettingsChanged;
        _recurring = new RecurringChecks(time, RunBackgroundCheckAsync); // never load the link unattended
        ConfigureRecurringChecks();
    }

    public event EventHandler<Announcement>? Announce;
    public event EventHandler<Alert>? AlertRaised;
    public event EventHandler<string>? LanguageChangeRequested;

    /// <summary>XAML binds localized labels as {Binding [Key]}.</summary>
    public string this[string key] => _l.Get(key, _ui);

    public SettingsViewModel Settings { get; }
    public IReadOnlyList<LaneViewModel> Lanes { get; }
    public IReadOnlyList<SymptomOption> Symptoms { get; }
    public IReadOnlyList<LanguageOption> Languages { get; }

    public MetricTile Download { get; }
    public MetricTile Upload { get; }
    public MetricTile Latency { get; }
    public MetricTile Variation { get; }
    public IReadOnlyList<MetricTile> Metrics { get; }

    /// <summary>Length of the current (or last) check; the chart's time axis.</summary>
    [ObservableProperty]
    public partial double ChartSeconds { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FocusedTarget), nameof(ServiceNames))]
    public partial SymptomOption? SelectedSymptom { get; set; }

    public string FocusedTarget
    {
        get => Settings.TargetFor(Enum.TryParse<Symptom>(SelectedSymptom?.Key.Replace("Symptom_", "", StringComparison.Ordinal), out var symptom) ? symptom : null);
        set
        {
            var symptom = Enum.TryParse<Symptom>(SelectedSymptom?.Key.Replace("Symptom_", "", StringComparison.Ordinal), out var parsed)
                ? parsed : (Symptom?)null;
            Settings.SetTarget(symptom, value);
            OnPropertyChanged();
        }
    }

    partial void OnSelectedSymptomChanged(SymptomOption? value) => (ServiceResults, ServiceSummary) = ([], "");

    [ObservableProperty]
    public partial string CurrentLanguage { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeLanguage), nameof(ShowRecommendationsButton), nameof(CanExportSupportReport))]
    [NotifyCanExecuteChangedFor(nameof(QuickCheckCommand), nameof(LongCaptureCommand), nameof(StopCommand), nameof(TestServicesCommand),
        nameof(TraceRouteCommand), nameof(ComparePublicDnsCommand), nameof(ScanWifiCommand),
        nameof(SwitchToFastestDnsCommand), nameof(RestoreAutomaticDnsCommand),
        nameof(ExportHtmlReportCommand), nameof(PrintPdfReportCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string TimeLeft { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial HeroState Hero { get; set; }

    [ObservableProperty]
    public partial string HeroTitle { get; set; }

    /// <summary>"What to try: …" after a check that found issues; empty otherwise.</summary>
    [ObservableProperty]
    public partial string TryPreview { get; set; }

    /// <summary>Windows reports no network at all: checks are not started, and the status and tray show a warning.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestServicesCommand), nameof(TraceRouteCommand),
        nameof(ComparePublicDnsCommand), nameof(ScanWifiCommand), nameof(SwitchToFastestDnsCommand))]
    public partial bool IsDisconnected { get; set; }

    /// <summary>Windows currently identifies the active Internet connection as cellular/WWAN.</summary>
    [ObservableProperty]
    public partial bool IsMobileNetwork { get; set; }

    /// <summary>Whether the current settings and network permit the recurring schedule.</summary>
    public bool IsBackgroundCheckScheduled => _recurring.IsEnabled;

    /// <summary>Seconds into the current check at which the user pressed the lag marker; the chart draws a flag at each.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> Markers { get; set; }

    [ObservableProperty]
    public partial string MarkersText { get; set; }

    /// <summary>"Checked, no change needed: …" for the latest check, so a clean result still shows what was verified.</summary>
    [ObservableProperty]
    public partial string CheckedOkText { get; set; }

    /// <summary>Feedback after a recommendation helper ran (for example "Copied").</summary>
    [ObservableProperty]
    public partial string RecommendationStatus { get; set; }

    /// <summary>The online AI review of the recommendations is running; the cards appear when it finishes.</summary>
    [ObservableProperty]
    public partial bool IsReviewing { get; set; }

    /// <summary>What the online AI review concluded ("Checked by …", unavailable, or advice held back); empty when off.</summary>
    [ObservableProperty]
    public partial string ReviewStatus { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCheckPage), nameof(IsRecommendationsPage), nameof(IsInsightsPage), nameof(IsSettingsPage), nameof(PageTitle))]
    public partial AppPage Page { get; set; }

    public bool IsCheckPage => Page == AppPage.Check;
    public bool IsRecommendationsPage => Page == AppPage.Recommendations;
    public bool IsSettingsPage => Page == AppPage.Settings;
    public bool IsInsightsPage => Page == AppPage.Insights;

    public string PageTitle => _l.Get(Page switch
    {
        AppPage.Recommendations => "Action_Recommendations",
        AppPage.Insights => "Nav_Insights",
        AppPage.Settings => "Action_Settings",
        _ => "Nav_Check",
    }, _ui);

    [ObservableProperty]
    public partial HealthLevel? LastLevel { get; set; }

    /// <summary>Local time and headline of the last finished check, for the tray tooltip and settings.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LastCheckText))]
    public partial string LastResult { get; set; }

    [ObservableProperty]
    public partial string LimitLegend { get; set; }

    [ObservableProperty]
    public partial string BufferbloatText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<HistoryListItem> HistoryRows { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<HistoryDayItem> DailyHistoryRows { get; set; } = [];

    [ObservableProperty]
    public partial string HistorySummary { get; set; } = "";

    [ObservableProperty]
    public partial string WorstHoursText { get; set; } = "";

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestServicesCommand))]
    public partial bool IsTestingTarget { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TraceRouteCommand), nameof(ComparePublicDnsCommand), nameof(ScanWifiCommand),
        nameof(SwitchToFastestDnsCommand), nameof(RestoreAutomaticDnsCommand))]
    public partial bool IsRunningNetworkTool { get; set; }

    [ObservableProperty]
    public partial string TraceRouteText { get; set; } = "";

    [ObservableProperty]
    public partial string DnsComparisonText { get; set; } = "";

    [ObservableProperty]
    public partial string WifiAnalysisText { get; set; } = "";

    [ObservableProperty]
    public partial string ReportStatus { get; set; } = "";

    public string LastCheckText => LastResult.Length == 0 ? "" : string.Format(_ui, _l.Get("Last_Check", _ui), LastResult);

    public bool CanChangeLanguage => !IsRunning;
    public bool CanExportSupportReport => _reportExporter is not null && _lastCheckedAtUtc is not null && !IsRunning;

    /// <summary>True when the latest saved check found issues to fix or best practices worth applying.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowRecommendationsButton))]
    public partial bool HasRecommendations { get; set; }

    [ObservableProperty]
    public partial bool HasFixes { get; set; }

    [ObservableProperty]
    public partial bool HasBestPractices { get; set; }

    /// <summary>Fixes plus best practices; the navigation badge.</summary>
    [ObservableProperty]
    public partial int RecommendationCount { get; set; }

    [ObservableProperty]
    public partial bool RecommendationsStale { get; set; }

    /// <summary>"Based on the check at …", always the first line of the recommendations.</summary>
    [ObservableProperty]
    public partial string RecommendationTime { get; set; }

    [ObservableProperty]
    public partial string RecommendationFindings { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<RecommendationItem> Recommendations { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AdviceItem> BestPractices { get; set; }

    /// <summary>The recommendations page in reading order: headings, findings, fixes, then best practices.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> RecommendationBlocks { get; set; }

    public bool ShowRecommendationsButton => HasRecommendations && !IsRunning;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateChecker is null || IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateStatus = _l.Get("Update_Checking", _ui);
        try
        {
            var result = await _updateChecker.CheckAsync();
            if (!result.IsUpdateAvailable)
            {
                UpdateStatus = string.Format(_ui, _l.Get("Update_Current", _ui), result.CurrentVersion);
                return;
            }
            if (_storeManagedUpdates)
            {
                UpdateStatus = string.Format(_ui, _l.Get("Update_StoreManaged", _ui), result.LatestVersion);
                return;
            }
            UpdateStatus = string.Format(_ui, _l.Get("Update_Available", _ui), result.LatestVersion);
            if (result.ReleaseUri is not null) _shell?.Open(result.ReleaseUri.AbsoluteUri);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            UpdateStatus = _l.Get("Update_Failed", _ui);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>Called when the user brings the window back (tray, notification): shows the latest recommendations.</summary>
    public void RevealRecommendations()
    {
        if (HasRecommendations) Page = AppPage.Recommendations;
    }

    partial void OnPageChanged(AppPage value)
    {
        if (value == AppPage.Recommendations) UpdateStale();
    }

    public void Dispose()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        _recurring.Dispose();
        _run?.Cancel();
    }

    partial void OnCurrentLanguageChanged(string value)
    {
        if (!value.Equals(_ui.Name, StringComparison.OrdinalIgnoreCase)) LanguageChangeRequested?.Invoke(this, value);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.BackgroundEnabled) or nameof(SettingsViewModel.IntervalMinutes):
                bool justEnabled = e.PropertyName == nameof(SettingsViewModel.BackgroundEnabled) && Settings.BackgroundEnabled;
                ConfigureRecurringChecks(runNow: justEnabled);
                break;
            case nameof(SettingsViewModel.BackgroundOnMobileEnabled):
                ConfigureRecurringChecks(runNow: IsMobileNetwork && Settings.BackgroundOnMobileEnabled);
                break;
            case nameof(SettingsViewModel.PlanDownloadMbps) or nameof(SettingsViewModel.PlanUploadMbps):
                RefreshHistory();
                break;
            case nameof(SettingsViewModel.AiReview) when _saved is { } current:
                ApplyLimit();
                break;
        }
    }

    private void ApplyLimit()
    {
        Lanes[1].LimitMs = Settings.DelayLimitMs;
        LimitLegend = string.Format(CultureInfo.CurrentCulture, _l.Get("Chart_AboveLimit", _ui), Settings.DelayLimitMs);
    }

    private bool CanScheduleBackgroundChecks =>
        Settings.BackgroundEnabled && !IsDisconnected && (!IsMobileNetwork || Settings.BackgroundOnMobileEnabled);

    private void ConfigureRecurringChecks(bool runNow = false)
    {
        bool enabled = CanScheduleBackgroundChecks;
        _recurring.Configure(enabled, TimeSpan.FromMinutes(Settings.IntervalMinutes), runNow: runNow && enabled);
    }

    private Task RunBackgroundCheckAsync() =>
        CanScheduleBackgroundChecks ? RunCheckAsync(measureSpeed: false) : Task.CompletedTask;

    [RelayCommand]
    private void GoToRecommendations() => RevealRecommendations();

    [RelayCommand]
    private void DismissRecommendations() => ClearSaved();

    private bool CanStart() => !IsRunning;

    private bool CanRunNetworkTool() =>
        _networkDiagnostics is not null && !IsDisconnected && !IsRunning && !IsRunningNetworkTool;

    private bool NetworkAvailableForTool()
    {
        if (_isConnected?.Invoke() != false) return true;
        UpdateConnectivity(false, IsMobileNetwork);
        return false;
    }


    [RelayCommand(CanExecute = nameof(CanExportSupportReport))]
    private async Task ExportHtmlReportAsync()
    {
        if (_reportExporter is null || _lastCheckedAtUtc is null) return;
        try
        {
            bool saved = await _reportExporter.ExportHtmlAsync(BuildSupportReport(), CancellationToken.None);
            ReportStatus = saved ? _l.Get("SupportReport_HtmlSaved", _ui) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ReportStatus = _l.Get("SupportReport_ExportFailed", _ui);
        }
        if (ReportStatus.Length > 0) Emit(ReportStatus, AnnouncementKind.SessionState);
    }

    [RelayCommand(CanExecute = nameof(CanExportSupportReport))]
    private void PrintPdfReport()
    {
        if (_reportExporter is null || _lastCheckedAtUtc is null) return;
        try
        {
            ReportStatus = _reportExporter.PrintPdf(BuildSupportReport())
                ? _l.Get("SupportReport_PrintSent", _ui) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ReportStatus = _l.Get("SupportReport_PrintFailed", _ui);
        }
        if (ReportStatus.Length > 0) Emit(ReportStatus, AnnouncementKind.SessionState);
    }

    private SupportReportData BuildSupportReport()
    {
        var checkedAt = _lastCheckedAtUtc ?? throw new InvalidOperationException("No completed check is available.");
        var report = _saved is { } saved && saved.CheckedAtUtc == checkedAt ? saved.Report : null;
        string summary = string.IsNullOrWhiteSpace(Summary) ? _l.Get("SupportReport_NoSummary", _ui) : Summary;
        string gap = _ui.TwoLetterISOLanguageName is "zh" or "ja" ? "" : " ";
        var verdictText = VerdictSentences(_lastVerdicts, _lastVerdictsStartUtc, report is null ? _context.Medium : _saved!.Context.Medium);
        string[] findings = [.. verdictText, .. report?.Issues.Select(issue => Sentence(issue, gap)) ?? [], .. MeasurementsLine()];
        const int maxPoints = 1000;
        var series = Lanes.Select(lane =>
        {
            var samples = lane.Samples;
            int stride = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)maxPoints));
            var points = new List<SupportReportPoint>(Math.Min(samples.Count, maxPoints));
            for (int start = 0; start < samples.Count; start += stride)
            {
                int end = Math.Min(samples.Count, start + stride);
                var selected = samples[end - 1];
                for (int i = start; i < end; i++)
                {
                    if (samples[i].Answered) continue;
                    selected = samples[i];
                    break;
                }
                points.Add(new(selected.Seconds, selected.Milliseconds));
            }
            return new SupportReportSeries(lane.Name, points, samples.Count - points.Count);
        }).ToArray();

        return new SupportReportData(_ui.Name, _l.Get("SupportReport_Title", _ui),
            TimeZoneInfo.ConvertTime(checkedAt, _time.LocalTimeZone).ToString("f", _ui),
            Headline(LastLevel ?? report?.Level ?? HealthLevel.Inconclusive), summary,
            _l.Get("SupportReport_FindingsHeading", _ui), _l.Get("SupportReport_TimelineHeading", _ui),
            _l.Get("SupportReport_StepHeader", _ui), _l.Get("SupportReport_TimeHeader", _ui),
            _l.Get("SupportReport_ObservationHeader", _ui), _l.Get("SupportReport_NoResponse", _ui),
            _l.Get("SupportReport_OmittedSamples", _ui), _l.Get("SupportReport_PrivacyNote", _ui),
            findings, series, _lastVerdictsStartUtc is { } started ? TimeZoneInfo.ConvertTime(started, _time.LocalTimeZone) : null);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task QuickCheckAsync()
    {
        if (IsRunning) return; // a forwarded request (jump list, tray) can arrive while a check runs
        Page = AppPage.Check; // watch the check where it runs
        if (_isConnected?.Invoke() == false)
        {
            ShowDisconnected(announce: true); // the user asked, so always say why nothing starts
            return;
        }
        (ServiceResults, ServiceSummary) = ([], "");
        await RunCheckAsync(Settings.MeasureSpeed);
        // Then the real services behind the chosen symptom, after the check so they never share the link with its measurements.
        if (!_checkStopped && !IsDisconnected && _serviceTargetProbe is not null) await RunServiceTestAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task LongCaptureAsync()
    {
        Page = AppPage.Check;
        return RunCheckAsync(measureSpeed: false, checkSeconds: Settings.LongCaptureMinutes * 60);
    }

    /// <summary>Called on every network change. Without any network the status shows a warning and checks stay off;
    /// a check that is already running continues, because a drop is evidence the user needs (Disconnections).</summary>
    public void UpdateConnectivity(bool connected, bool mobileNetwork = false)
    {
        bool connectionChanged = IsDisconnected != !connected;
        bool mobileChanged = IsMobileNetwork != mobileNetwork;
        if (!connectionChanged && !mobileChanged) return;
        IsDisconnected = !connected;
        IsMobileNetwork = mobileNetwork;
        ConfigureRecurringChecks();
        if (!connectionChanged) return;
        if (IsRunning) return;
        if (!connected) ShowDisconnected(announce: true);
        else if (Hero == HeroState.Disconnected) (Hero, HeroTitle, Summary) = (HeroState.Idle, _l.Get("Home_Title", _ui), _l.Get("Home_Subtitle", _ui));
    }

    private void ShowDisconnected(bool announce)
    {
        IsDisconnected = true;
        (Hero, HeroTitle, Summary, TryPreview, CheckedOkText) =
            (HeroState.Disconnected, _l.Get("Hero_Disconnected", _ui), _l.Get("Summary_Disconnected", _ui), "", "");
        if (announce) Emit(HeroTitle + ". " + Summary, AnnouncementKind.Error);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _run?.Cancel();

    [RelayCommand]
    private void Marker()
    {
        bool running = IsRunning;
        if (running)
        {
            Markers = [.. Markers, Math.Max(0, Math.Round(_time.GetElapsedTime(_start).TotalSeconds, 1))];
            MarkersText = string.Format(_ui, _l.Get("Chart_Markers", _ui),
                string.Join(", ", Markers.Select(s => string.Format(CultureInfo.CurrentCulture, "{0:0} s", s))));
        }
        Message = _l.Get(running ? "Announce_MarkerRecorded" : "Error_StartCaptureFirst", _ui);
        Emit(Message, running ? AnnouncementKind.MarkerAcknowledged : AnnouncementKind.Error);
    }

    [RelayCommand]
    private void RunAction(RecommendationAction? action)
    {
        if (action is null || _shell is null) return;
        if (action.Kind == RecommendationActionKind.Open)
        {
            _shell.Open(action.Target);
            return;
        }
        _shell.Copy(action.Kind == RecommendationActionKind.CopySummary ? SummaryText() : action.Target);
        RecommendationStatus = _l.Get("Status_Copied", _ui);
        Emit(RecommendationStatus, AnnouncementKind.SessionState);
    }

    /// <summary>
    /// Runs one check: a latency phase, then (manual checks only) download and upload phases while latency probes
    /// keep running, which yields latency under load. Returns at once when another check is already running.
    /// </summary>
    public async Task RunCheckAsync(bool measureSpeed, int? checkSeconds = null)
    {
        if (IsRunning) return;
        // No network at all: nothing to measure. Background ticks skip quietly (the warning is already visible).
        if (_isConnected?.Invoke() == false)
        {
            ShowDisconnected(announce: Hero != HeroState.Disconnected);
            return;
        }
        IsDisconnected = false;
        IsRunning = true;
        Message = "";
        Summary = _l.Get("Announce_CaptureStarted", _ui);
        (Hero, HeroTitle, TryPreview) = (HeroState.Running, _l.Get("Hero_Checking", _ui), "");
        foreach (var lane in Lanes) lane.Reset();
        foreach (var tile in Metrics) (tile.Value, tile.Detail) = ("—", "");
        (_downloadWindow, _uploadWindow) = (null, null);
        _dnsMs.Clear();
        ResetEvidence();
        _checkStopped = false;
        (_downloadMbps, _uploadMbps, _loadedDownMs, _loadedUpMs, _checkStartedUtc) = (null, null, null, null, _time.GetUtcNow());
        (Markers, MarkersText, CheckedOkText, _otherTraffic) = ([], "", "", null);
        _start = _time.GetTimestamp(); // markers pressed while the session starts land at 0 s
        int seconds = Math.Clamp(checkSeconds ?? Settings.CheckSeconds, SettingsViewModel.MinCheckSeconds,
            SettingsViewModel.MaxLongCaptureMinutes * 60);
        var duration = TimeSpan.FromSeconds(seconds);
        _idleSeconds = duration.TotalSeconds;
        ChartSeconds = duration.TotalSeconds;
        // Denser sampling for short checks so every check has enough samples to judge.
        int homeEvery = seconds <= SettingsViewModel.MaxCheckSeconds ? 1 : Math.Clamp(seconds / 60, 1, 5);
        int internetEvery = Math.Clamp(seconds / 30, 1, 5), webEvery = Math.Clamp(seconds / 5, 5, 15);
        var run = _run = new CancellationTokenSource();
        var clock = new SessionClock(_time);
        var pending = new List<Task>();
        Guid session = Guid.NewGuid(), segment = Guid.NewGuid();
        long sequence = 0;
        PreviewProbes? probes = null;
        Task? speed = null;
        _context = new CheckContext();
        try
        {
            probes = await _startSession(clock, run.Token);
            _context = probes.Context;
            bool withSpeed = measureSpeed && probes.Throughput is not null && !probes.Context.Metered;
            string speedNote = !measureSpeed ? _l.Get("Speed_NotMeasured", _ui)
                : probes.Context.Metered ? _l.Get("Speed_Metered", _ui) : "";
            (Download.Detail, Upload.Detail) = (speedNote, speedNote);
            var total = withSpeed ? duration + 2 * SpeedPhase : duration;
            ChartSeconds = total.TotalSeconds;
            Say("Announce_CaptureStarted", AnnouncementKind.SessionState);
            _start = _time.GetTimestamp();
            _samplesStartUtc = _time.GetUtcNow();
            var counters = probes.Counters;
            var countersAtStart = counters?.Invoke();
            bool trafficRead = false;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            for (int tick = 0; ; tick++)
            {
                var elapsed = _time.GetElapsedTime(_start);
                // Other apps' traffic over the idle phase, read once before the speed test adds its own.
                if (elapsed >= duration && !trafficRead)
                {
                    trafficRead = true;
                    if (countersAtStart is { } from && counters?.Invoke() is { } to) _otherTraffic = TrafficMbps(from, to, elapsed.TotalSeconds);
                }
                if (withSpeed && speed is null && elapsed >= duration) speed = MeasureSpeedAsync(probes.Throughput!, run.Token);
                bool latencyDone = elapsed >= duration;
                if (latencyDone && (speed is null || speed.IsCompleted)) break;
                var left = total - elapsed;
                Progress = Math.Min(1, elapsed / total);
                TimeLeft = left <= TimeSpan.Zero ? "" : left.TotalSeconds > 60
                    ? _l.Plural("Capture_MinutesLeft", (long)Math.Ceiling(left.TotalMinutes), _ui)
                    : _l.Plural("Capture_SecondsLeft", (long)Math.Ceiling(left.TotalSeconds), _ui);
                if (tick % homeEvery == 0) Probe(Lanes[0], probes.Icmp, new("gateway", ProbeKind.Icmp, RequestFamily.IPv4), 800);
                if (tick % internetEvery == 0 || latencyDone) Probe(Lanes[1], probes.Tcp, new("web", ProbeKind.Tcp, RequestFamily.IPv4), 3000);
                if (!latencyDone && tick % webEvery == 0) Probe(Lanes[2], probes.Dns, new("web", ProbeKind.SystemDns, RequestFamily.IPv4), 3000);
                if (!latencyDone && tick % webEvery == webEvery / 2) Probe(Lanes[2], probes.Https, new("web", ProbeKind.Https, RequestFamily.Any), 5000);
                if (!await timer.WaitForNextTickAsync(run.Token)) break;
            }
            Progress = 1;
        }
        catch (OperationCanceledException)
        {
            _checkStopped = true;
        }
        finally
        {
            if (speed is not null)
            {
                try { await speed; }
                catch (OperationCanceledException) { }
            }
            await Task.WhenAll(pending);
            _links = LinkEvents(probes?.LinkEvents?.Invoke()); // before Dispose stops the monitors
            probes?.Dispose();
            (IsRunning, TimeLeft, _run) = (false, "", null);
            run.Dispose();
        }
        var facts = await FactsAsync(probes?.Facts);
        await CompleteAsync(facts, facts is null ? [] : await AppsAsync(probes?.AppTraffic));

        void Probe(LaneViewModel lane, IProbe probe, StreamKey stream, int timeoutMs) =>
            pending.Add(RecordAsync(lane, probe, new ProbeRequest(session, segment, sequence++, stream, clock.NowUs,
                TimeSpan.FromMilliseconds(timeoutMs)), run.Token));
    }

    private async Task MeasureSpeedAsync(IThroughputProbe probe, CancellationToken ct)
    {
        foreach (var (direction, tile, heroKey) in new[]
        {
            (ThroughputDirection.Download, Download, "Hero_SpeedDownload"),
            (ThroughputDirection.Upload, Upload, "Hero_SpeedUpload"),
        })
        {
            HeroTitle = _l.Get(heroKey, _ui);
            double from = _time.GetElapsedTime(_start).TotalSeconds;
            var live = new Progress<double>(mbps => tile.Value = Mbps(mbps));
            var result = await probe.MeasureAsync(direction, SpeedPhase, live, ct);
            var window = (from, _time.GetElapsedTime(_start).TotalSeconds);
            if (direction == ThroughputDirection.Download) _downloadWindow = window;
            else _uploadWindow = window;
            (tile.Value, tile.Detail) = result.Mbps is { } v
                ? (Mbps(v), string.Format(CultureInfo.CurrentCulture, "{0:0} MB", result.Bytes / 1e6))
                : ("—", _l.Get("Speed_Failed", _ui));
            if (direction == ThroughputDirection.Download) _downloadMbps = result.Mbps;
            else _uploadMbps = result.Mbps;
        }
        HeroTitle = _l.Get("Hero_Checking", _ui);
    }

    /// <summary>Shows latency tiles and returns idle and worst loaded median latency (bufferbloat input).</summary>
    private (double? Idle, double? Loaded) UpdateLatencyMetrics(Sample[] idleInternet)
    {
        var idle = StepStatistics.From(idleInternet);
        Latency.Value = MsText(idle.MedianMs);
        Variation.Value = MsText(idle.VariationMs);
        double? Loaded((double From, double To)? window) => window is not { } w ? null
            : StepStatistics.From([.. Lanes[1].Samples.Where(s => s.Seconds >= w.From && s.Seconds <= w.To)]).MedianMs;
        var (down, up) = (Loaded(_downloadWindow), Loaded(_uploadWindow));
        Latency.Detail = down is null && up is null ? ""
            : string.Format(_ui, _l.Get("Metric_UnderLoad", _ui), $"↓ {MsText(down)} · ↑ {MsText(up)}");
        (_loadedDownMs, _loadedUpMs) = (down, up);
        return (idle.MedianMs, down is null && up is null ? null : Math.Max(down ?? 0, up ?? 0));
    }

    private static string MsText(double? v) => v is { } x ? string.Format(CultureInfo.CurrentCulture, "{0:0} ms", x) : "—";

    private static (double Down, double Up) TrafficMbps((ulong Received, ulong Sent) from, (ulong Received, ulong Sent) to, double seconds)
    {
        static double Delta(ulong a, ulong b) => b >= a ? b - a : 0; // counters restart when an adapter resets
        double scale = 8 / 1e6 / Math.Max(seconds, 1);
        return (Delta(from.Received, to.Received) * scale, Delta(from.Sent, to.Sent) * scale);
    }

    private static string Mbps(double v) => string.Format(CultureInfo.CurrentCulture, v >= 100 ? "{0:0} Mbps" : v >= 10 ? "{0:0.0} Mbps" : "{0:0.00} Mbps", v);

    private async Task RecordAsync(LaneViewModel lane, IProbe probe, ProbeRequest request, CancellationToken ct)
    {
        var observation = await probe.ExecuteAsync(request, ct);
        var before = lane.State;
        double seconds = _time.GetElapsedTime(_start).TotalSeconds;
        lane.Record(observation, seconds);
        RecordOutcome(observation, seconds);
        if (request.Stream.Kind == ProbeKind.SystemDns && observation is { Status: ProbeStatus.Success, DurationUs: { } us })
            _dnsMs.Add(us / 1000.0);
        UpdateLiveSummary();
        if (before != LaneState.Waiting && before != lane.State)
            Emit(lane.AccessibleText, AnnouncementKind.LinkChange);
    }

    private void UpdateLiveSummary()
    {
        if (Lanes.FirstOrDefault(l => l.State == LaneState.NotResponding) is { } failing)
            Summary = string.Format(_ui, _l.Get("Summary_Problem", _ui), failing.Name);
        else if (Lanes.All(l => l.State == LaneState.Responding))
            Summary = _l.Get("Summary_AllGood", _ui);
    }

    private async Task CompleteAsync(SystemFacts? facts, IReadOnlyList<AppTraffic> apps)
    {
        // Judge the idle phase only: the speed test's own load must not create problems.
        Sample[] Idle(LaneViewModel lane) => [.. lane.Samples.Where(s => s.Seconds < _idleSeconds)];
        var report = HealthEvaluator.Evaluate(Idle(Lanes[0]), Idle(Lanes[1]), Idle(Lanes[2]), Settings.Thresholds);
        var verdicts = EvaluateVerdicts(Idle);
        // Never "no problem observed" next to a measured issue (the two evaluators look at different evidence).
        if (report.Issues.Count > 0) verdicts = [.. verdicts.Where(v => v.Rule != RuleId.R10)];
        // A link drop, failed name lookups or a rejected secure check are problems even when the samples stayed within limits.
        var floor = verdicts.Any(v => v.Rule is RuleId.R01 or RuleId.R03 or RuleId.R04 or RuleId.R05 or RuleId.R07) ? HealthLevel.Unhealthy
            : verdicts.Any(v => v.IsProblem) ? HealthLevel.Degraded : HealthLevel.NoIssue;
        if (floor > report.Level) report = report with { Level = floor };
        (_lastVerdicts, _lastVerdictsStartUtc) = (verdicts, _samplesStartUtc);
        var (idleMs, loadedMs) = UpdateLatencyMetrics(Idle(Lanes[1]));
        var bufferbloat = BufferbloatEvaluator.Evaluate(idleMs, _loadedDownMs, _loadedUpMs);
        BufferbloatText = bufferbloat is null
            ? _l.Get("Bufferbloat_NotMeasured", _ui)
            : string.Format(_ui, _l.Get("Bufferbloat_Summary", _ui), bufferbloat.Grade, bufferbloat.IncreaseMs.ToString("0", CultureInfo.CurrentCulture));
        var checkedAt = _time.GetUtcNow();
        _lastCheckedAtUtc = checkedAt;
        UpdateReportAvailability();
        RecordHistory(new CheckHistoryEntry(checkedAt, report.Level, _downloadMbps, _uploadMbps, idleMs,
            StepStatistics.From(Idle(Lanes[1])).VariationMs, bufferbloat?.Grade, bufferbloat?.IncreaseMs));
        Summary = LeadSentence(report, verdicts);
        Hero = report.Level switch
        {
            HealthLevel.NoIssue => HeroState.NoIssue,
            HealthLevel.Degraded => HeroState.Degraded,
            HealthLevel.Unhealthy => HeroState.Unhealthy,
            _ => HeroState.Inconclusive,
        };
        HeroTitle = Headline(report.Level);
        FinishLanes(report);
        LastLevel = report.Level;
        LastResult = string.Format(CultureInfo.CurrentCulture, "{0:t} · {1}", checkedAt.ToLocalTime(), Headline(report.Level));

        IReadOnlyList<ActionCode> actions = report.Issues.Count > 0 ? NextActionPlanner.Plan(report, _context) : [];
        var review = Advise(facts, apps, report, idleMs, loadedMs);
        var advice = review.Advice;
        // Only a check with a speed phase can re-measure latency under load (background and metered checks have none),
        // so the last measured advice is carried forward instead of silently dropped.
        if (loadedMs is null && _saved?.Advisories?.FirstOrDefault(a => a.Code == AdvisoryCode.LatencyUnderLoad) is { } underLoad)
            advice = [.. advice.Append(underLoad).OrderByDescending(a => a.Level).ThenBy(a => a.Code)];
        IReadOnlyList<CheckArea> passed = review.Passed;
        CheckedOkText = CheckedText(passed);
        // Inconclusive checks without issues keep the previous result: they prove nothing either way.
        IReadOnlyList<ActionCode> shownActions = [];
        IReadOnlyList<Advisory> shownAdvice = [];
        if (report.Issues.Count > 0 || verdicts.Any(v => v.IsProblem) || report.Level == HealthLevel.NoIssue && advice.Count > 0)
        {
            var saved = new SavedResult(checkedAt, report, _context, actions, advice, passed, _samplesStartUtc, verdicts);
            _results.Save(saved);
            await ShowSavedAsync(saved, previousSession: false);
            (shownActions, shownAdvice) = (_shownActions, _shownAdvice); // only what passed the online review
        }
        else if (report.Level == HealthLevel.NoIssue)
        {
            ClearSaved();
        }
        var important = shownAdvice.Where(a => a.Level == AdvisoryLevel.Important).Take(1).Select(AdviceTitle).ToList();
        // One line each, so "What to try" and "Worth checking" never run together.
        TryPreview = string.Join("\n", new[]
        {
            shownActions.Count == 0 ? "" : string.Format(_ui, _l.Get("Recommend_TryPrefix", _ui), JoinList(shownActions.Select(a => ActionTitle(a)))),
            important.Count == 0 ? "" : string.Format(_ui, _l.Get("Recommend_CheckPrefix", _ui), JoinList(important)),
        }.Where(s => s.Length > 0));
        Emit(TryPreview.Length == 0 ? Summary : Summary + " " + TryPreview, AnnouncementKind.FindingReady);

        // The shell decides whether to show it (not while the window is in the foreground).
        if (_alerts.Evaluate(report) is not { } kind) return;
        string title = _l.Get(kind switch
        {
            AlertKind.Problem => "Alert_Problem",
            AlertKind.Slow => "Alert_Slow",
            _ => "Alert_Recovered",
        }, _ui);
        string body = kind == AlertKind.Recovered ? _l.Get("Health_NoIssue", _ui)
            : TryPreview.Length == 0 ? Summary
            : Summary + "\n" + TryPreview;
        AlertRaised?.Invoke(this, new Alert(kind, title, body));
    }

    private void RecordHistory(CheckHistoryEntry entry)
    {
        _history = CheckHistoryAnalytics.Retain(_history.Append(entry), _time.GetUtcNow());
        _historyStore?.Save(_history);
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        var culture = CultureInfo.CurrentCulture;
        var zone = TimeZoneInfo.Local;
        var trends = CheckHistoryAnalytics.Daily(_history, Settings.PlanDownloadMbps, Settings.PlanUploadMbps, zone);
        DailyHistoryRows = [.. trends.Select(day =>
        {
            string rate = day.QualityScore is { } score
                ? string.Format(culture, _l.Get("History_QualityValue", _ui), score.ToString("0", culture),
                    (day.NoIssueCheckRate ?? 0).ToString("0", culture) + "%")
                : _l.Get("History_NotEnoughData", _ui);
            string speeds = string.Format(culture, _l.Get("History_Speeds", _ui), Format(day.AverageDownloadMbps), Format(day.AverageUploadMbps));
            bool hasPlan = day.AverageDownloadPercentOfPlan is not null || day.AverageUploadPercentOfPlan is not null;
            string plan = hasPlan
                ? string.Format(culture, _l.Get("History_PlanComparison", _ui), FormatPercent(day.AverageDownloadPercentOfPlan), FormatPercent(day.AverageUploadPercentOfPlan))
                : "";
            string accessible = string.Format(culture, _l.Get("History_DayAccessible", _ui),
                day.Day.ToDateTime(TimeOnly.MinValue).ToString("D", _ui), day.Checks, rate, speeds, plan);
            return new HistoryDayItem(day.Day.ToDateTime(TimeOnly.MinValue).ToString("d", _ui),
                string.Format(culture, _l.Get("History_Checks", _ui), day.Checks), rate,
                plan.Length == 0 ? speeds : speeds + " · " + plan, accessible);
        })];
        HistoryRows = [.. _history.Take(50).Select(row =>
        {
            string localTime = TimeZoneInfo.ConvertTime(row.CheckedAtUtc, zone).ToString("g", _ui);
            string level = Headline(row.Level);
            string metrics = string.Format(culture, _l.Get("History_RowMetrics", _ui), Format(row.MedianLatencyMs), Format(row.DownloadMbps), Format(row.UploadMbps));
            return new HistoryListItem(localTime, level, metrics, $"{localTime}. {level}. {metrics}");
        })];
        HistorySummary = _history.Count == 0
            ? _l.Get("History_NoSamples", _ui)
            : string.Format(culture, _l.Get("History_Summary", _ui), _history.Count,
                _history.Max(e => e.CheckedAtUtc).ToLocalTime().ToString("g", _ui));
        var worst = CheckHistoryAnalytics.WorstHours(_history, zone);
        WorstHoursText = worst.Count == 0
            ? _l.Get("History_NoProblems", _ui)
            : string.Format(culture, _l.Get("History_WorstHours", _ui),
                string.Join(", ", worst.Take(3).Select(h =>
                    $"{TimeOnly.MinValue.AddHours(h.Hour).ToString("t", _ui)} ({h.ProblemChecks}/{h.Checks})")));

        string Format(double? value) => value is { } v ? v.ToString("0.##", culture) : "—";
        string FormatPercent(double? value) => value is { } v ? v.ToString("0", culture) + "%" : "—";
    }

    /// <summary>Maps the report onto the path so the step view never contradicts the headline.</summary>
    private void FinishLanes(HealthReport report)
    {
        bool Has(Func<HealthIssue, bool> match) => report.Issues.Any(match);
        bool local = Has(i => i.Location == IssueLocation.LocalNetwork);
        bool Answered(LaneViewModel lane) => lane.Samples.Any(s => s.Answered);

        LaneState home = !Answered(Lanes[0]) ? LaneState.NotResponding
            : local && Has(i => i.Kind == IssueKind.Interrupted) ? LaneState.NotResponding
            : local ? LaneState.Slow
            : LaneState.Responding;
        LaneState internet = !Answered(Lanes[1]) || Has(i => i.Kind == IssueKind.Interrupted && i.Location != IssueLocation.LocalNetwork)
            ? LaneState.NotResponding
            : Has(i => i.Kind is IssueKind.Loss or IssueKind.Delay or IssueKind.Variation) ? LaneState.Slow
            : LaneState.Responding;
        LaneState web = !Answered(Lanes[2]) || Has(i => i.Kind == IssueKind.WebUnreachable) ? LaneState.NotResponding : LaneState.Responding;

        Lanes[0].Finish(home, _idleSeconds);
        Lanes[1].Finish(internet, _idleSeconds);
        Lanes[2].Finish(web, _idleSeconds);
    }

    /// <summary>
    /// Shows the recommendations of a saved result. With the online AI review on, the cards stay hidden until the reviewers
    /// answer or give up; advice two reviewers reject is left out, and confirmed advice is marked. Without an answer (for
    /// example offline), the advice is shown unchecked, because help matters most when the connection is bad.
    /// </summary>
    private async Task ShowSavedAsync(SavedResult saved, bool previousSession, bool reveal = false)
    {
        (_saved, _savedFromPreviousSession) = (saved, previousSession);
        _lastCheckedAtUtc = saved.CheckedAtUtc;
        if (saved.Verdicts is not null) (_lastVerdicts, _lastVerdictsStartUtc) = (saved.Verdicts, saved.StartedAtUtc);
        LastLevel = saved.Report.Level;
        LastResult = string.Format(CultureInfo.CurrentCulture, "{0:t} · {1}",
            TimeZoneInfo.ConvertTime(saved.CheckedAtUtc, _time.LocalTimeZone), Headline(saved.Report.Level));
        UpdateReportAvailability();
        int version = ++_reviewVersion;
        var checkedAt = TimeZoneInfo.ConvertTime(saved.CheckedAtUtc, _time.LocalTimeZone);
        RecommendationTime = string.Format(_ui, _l.Get("Recommend_BasedOn", _ui), checkedAt.ToString("g", CultureInfo.CurrentCulture));
        RecommendationStatus = "";
        IReadOnlyDictionary<string, ReviewVerdict>? verdicts = null;
        if (_reviewer is not null && Settings.AiReview)
        {
            if (_isConnected?.Invoke() == false)
            {
                verdicts = new Dictionary<string, ReviewVerdict>();
            }
            else
            {
                (RecommendationBlocks, ReviewStatus, IsReviewing) = ([], _l.Get("Review_InProgress", _ui), true);
                var texts = saved.Actions.Select(a => ReviewText(a, saved)).Concat((saved.Advisories ?? []).Select(ReviewText)).ToList();
                try { verdicts = await _reviewer.ReviewAsync(texts, CancellationToken.None); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                    or UnauthorizedAccessException or InvalidOperationException or FormatException or NotSupportedException
                    or System.Text.Json.JsonException)
                {
                    verdicts = new Dictionary<string, ReviewVerdict>();
                }
                finally
                {
                    if (version == _reviewVersion) IsReviewing = false;
                }
                if (version != _reviewVersion) return; // a newer result replaced this one during the review
            }
        }
        Publish(saved, verdicts);
        UpdateStale();
        if (reveal && HasRecommendations) Page = AppPage.Recommendations;
    }

    private void UpdateReportAvailability()
    {
        OnPropertyChanged(nameof(CanExportSupportReport));
        ExportHtmlReportCommand.NotifyCanExecuteChanged();
        PrintPdfReportCommand.NotifyCanExecuteChanged();
    }

    private void Publish(SavedResult saved, IReadOnlyDictionary<string, ReviewVerdict>? verdicts)
    {
        int heldBack = 0;
        var used = new List<ReviewVerdict>();
        ReviewVerdict? Verdict(string text)
        {
            var v = verdicts?.GetValueOrDefault(text);
            if (v is { Ok: true }) used.Add(v);
            return v;
        }
        var fixes = new List<RecommendationItem>();
        var shownActions = new List<ActionCode>();
        foreach (var a in saved.Actions)
        {
            var v = Verdict(ReviewText(a, saved));
            if (v is { Ok: false }) { heldBack++; continue; }
            fixes.Add(new((fixes.Count + 1).ToString(CultureInfo.CurrentCulture), ActionTitle(a), RemedyDetail(a, saved), ActionFor(a, saved.Context), v?.Ok == true));
            shownActions.Add(a);
        }
        var advice = new List<AdviceItem>();
        var shownAdvice = new List<Advisory>();
        foreach (var a in saved.Advisories ?? [])
        {
            var v = Verdict(ReviewText(a));
            if (v is { Ok: false }) { heldBack++; continue; }
            advice.Add(ToItem(a, saved.Context) with { Checked = v?.Ok == true });
            shownAdvice.Add(a);
        }
        (Recommendations, BestPractices, _shownActions, _shownAdvice) = (fixes, advice, shownActions, shownAdvice);
        (HasFixes, HasBestPractices) = (Recommendations.Count > 0, BestPractices.Count > 0);
        var told = VerdictSentences(saved.Verdicts?.Where(v => v.Rule != RuleId.R10).ToList(), saved.StartedAtUtc, saved.Context.Medium);
        string measured = saved.Report.Issues.Count > 0 ? Describe(saved.Report)
            : saved.Verdicts?.Any(v => v.IsProblem) == true ? "" : _l.Get("Recommend_HealthyNote", _ui);
        RecommendationFindings = string.Join("\n", told.Append(measured).Where(s => s.Length > 0));
        var blocks = new List<object>();
        if (HasFixes) blocks.AddRange([new SectionHeading(_l.Get("Section_Fixes", _ui)), new FindingsNote(RecommendationFindings, true), .. Recommendations]);
        else if (HasBestPractices) blocks.Add(new FindingsNote(RecommendationFindings, false));
        if (HasBestPractices) blocks.AddRange([new SectionHeading(_l.Get("Section_BestPractices", _ui)), .. BestPractices]);
        // A hand-edited or inconsistent file must never call a flagged area fine.
        var flagged = (saved.Advisories ?? []).Select(a => ConfigurationAdvisor.AreaOf(a.Code)).ToHashSet();
        if (CheckedText((saved.Passed ?? []).Where(a => !flagged.Contains(a))) is { Length: > 0 } checkedOk) blocks.Add(new CheckedNote(checkedOk));
        RecommendationBlocks = blocks;
        RecommendationCount = Recommendations.Count + BestPractices.Count;
        HasRecommendations = RecommendationCount > 0;
        ReviewStatus = verdicts is null ? "" : ReviewSummary(RecommendationCount, fixes.Count(f => f.Checked) + advice.Count(a => a.Checked), heldBack, used);
    }

    private string ReviewSummary(int shown, int confirmed, int heldBack, List<ReviewVerdict> used)
    {
        var parts = new List<string>();
        if (shown > 0)
            parts.Add(confirmed == shown
                ? string.Format(_ui, _l.Get("Review_AllChecked", _ui), string.Join(", ", used.Select(v => v.Reviewer).Distinct()))
                : confirmed > 0 ? _l.Get("Review_Partial", _ui) : _l.Get("Review_Unavailable", _ui));
        if (heldBack > 0) parts.Add(_l.Get("Review_HeldBack", _ui));
        return string.Join(" ", parts);
    }

    /// <summary>The generic English text an online reviewer sees: names become [name] and measurements N, so nothing
    /// personal leaves the PC, and the same wording is reviewed once and then remembered.</summary>
    private string ReviewText(Advisory a)
    {
        string text = Render(a, "Title", English, redact: true) + ". " + Render(a, "Detail", English, redact: true);
        if (a is { Code: AdvisoryCode.OtherTraffic, Args.Count: >= 4 })
            text += " " + string.Format(CultureInfo.InvariantCulture, _l.Get("Advice_OtherTraffic_Also", English), "[name]", "N");
        return text;
    }

    private string ReviewText(ActionCode code, SavedResult saved) =>
        ActionTitle(code, English) + ". " + RemedyDetail(code, saved, English, redact: true);

    private void ClearSaved()
    {
        _results.Clear();
        _saved = null;
        (HasRecommendations, HasFixes, HasBestPractices, RecommendationsStale, RecommendationCount) = (false, false, false, false, 0);
        (Recommendations, BestPractices, RecommendationBlocks, RecommendationStatus) = ([], [], [], "");
        (ReviewStatus, IsReviewing, _shownActions, _shownAdvice) = ("", false, [], []);
        _reviewVersion++; // a review still running for the cleared result must not show it again
        if (Page == AppPage.Recommendations) Page = AppPage.Check;
    }

    private async Task<SystemFacts?> FactsAsync(Task<SystemFacts>? facts)
    {
        if (facts is null) return null;
        try { return await facts.WaitAsync(TimeSpan.FromSeconds(5), _time); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; } // advice is optional; never fail a check
    }

    /// <summary>Apps that used the network around the check (Windows accounts per minute), for naming who caused the traffic.</summary>
    private async Task<IReadOnlyList<AppTraffic>> AppsAsync(Func<DateTimeOffset, DateTimeOffset, Task<IReadOnlyList<AppTraffic>>>? read)
    {
        if (read is null) return [];
        try { return await read(_checkStartedUtc, _time.GetUtcNow()).WaitAsync(TimeSpan.FromSeconds(5), _time); }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; } // attribution is optional
    }

    private AdvisorResult Advise(SystemFacts? facts, IReadOnlyList<AppTraffic> apps, HealthReport report, double? idleMs, double? loadedMs)
    {
        if (facts is null) return new([], []); // unknown settings are never reported as fine
        Symptom? symptom = SelectedSymptom is { } s && Enum.TryParse<Symptom>(s.Key.Replace("Symptom_", "", StringComparison.Ordinal), out var v)
            ? v : null;
        double? dns = _dnsMs.Count == 0 ? null : _dnsMs.Order().ElementAt(_dnsMs.Count / 2);
        return ConfigurationAdvisor.Review(new(facts, symptom, report, dns, idleMs, loadedMs,
            DateOnly.FromDateTime(_time.GetLocalNow().DateTime), _otherTraffic, apps, _downloadMbps, _uploadMbps, _loadedDownMs, _loadedUpMs));
    }

    private string CheckedText(IEnumerable<CheckArea> passed)
    {
        var names = passed.Select(a => _l.Get($"Area_{a}", _ui)).ToList();
        return names.Count == 0 ? "" : string.Format(_ui, _l.Get("Checked_Ok", _ui), JoinList(names));
    }

    private AdviceItem ToItem(Advisory a, CheckContext context)
    {
        bool important = a.Level == AdvisoryLevel.Important;
        string title = Render(a, "Title"), detail = Render(a, "Detail");
        if (a is { Code: AdvisoryCode.OtherTraffic, Args.Count: >= 4 })
            detail += " " + string.Format(CultureInfo.CurrentCulture, _l.Get("Advice_OtherTraffic_Also", _ui), Arg(a.Args[2]), Arg(a.Args[3]));
        return new(important, _l.Get(important ? "Advice_Important" : "Advice_Suggestion", _ui), title, detail, ActionFor(a, context, title));
    }

    /// <summary>
    /// Advice text in the most specific wording available: the variant for this exact situation, then the wording that
    /// names what was found on this PC, then the generic fallback (used only when a name could not be read).
    /// {0} and {1} are the values, {2} onwards the names; missing arguments render empty instead of failing.
    /// </summary>
    private string Render(Advisory a, string part, CultureInfo? ui = null, bool redact = false)
    {
        var culture = ui ?? _ui;
        string text = (a.Variant is { Length: > 0 } v ? _l.Find($"Advice_{a.Code}_{part}{v}", culture) : null)
            ?? (a.Args is { Count: > 0 } ? _l.Find($"Advice_{a.Code}_{part}Named", culture) : null)
            ?? _l.Get($"Advice_{a.Code}_{part}", culture);
        // A month name inside a translated sentence follows the UI language (numeric dates follow the regional format).
        object first = redact ? (a.Code == AdvisoryCode.OldNetworkDriver ? "[date]" : "N")
            : a.Code == AdvisoryCode.OldNetworkDriver ? new DateTime((int)a.Value / 100, Math.Clamp((int)a.Value % 100, 1, 12), 1).ToString("Y", culture)
            : a.Value;
        var values = new List<object> { first, redact ? "N" : a.Value2 };
        values.AddRange((a.Args ?? []).Select(arg => Arg(arg, culture, redact)));
        while (values.Count < 8) values.Add("");
        return string.Format(redact ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture, text, [.. values]);
    }

    /// <summary>"#12.5" is an invariant number shown in the regional format; "@Key" is a resource shown in the UI language.
    /// Redacted (for the online review): names become [name] and numbers N; resources stay, because they are not personal.</summary>
    private object Arg(string arg, CultureInfo? ui = null, bool redact = false) =>
        arg.StartsWith('#') && double.TryParse(arg.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (redact ? "N" : n)
        : arg.StartsWith('@') ? _l.Find(arg[1..], ui ?? _ui) ?? arg[1..]
        : redact ? "[name]" : arg;

    // Weak signal, slow cable links and old Wi-Fi adapters need physical changes, so they have no helper.
    private RecommendationAction? ActionFor(Advisory a, CheckContext context, string title) => a.Code switch
    {
        AdvisoryCode.LatencyUnderLoad => Router(context, title),
        AdvisoryCode.OtherTraffic when a.Variant == "Windows" => OpenSettings("ms-settings:delivery-optimization-advanced", title),
        AdvisoryCode.OtherTraffic when a.Variant is "OneDrive" or "Sync" or "Game" or "Browser" => null, // the steps are inside that app
        AdvisoryCode.OtherTraffic => Helper(RecommendationActionKind.Open, "Action_OpenTaskManager", "taskmgr", title),
        AdvisoryCode.TcpAutoTuningLimited => Helper(RecommendationActionKind.CopyText, "Action_CopyCommand", "netsh int tcp set global autotuninglevel=normal", title),
        AdvisoryCode.WifiLinkSlow when a.Variant == "Legacy" => null,
        AdvisoryCode.WifiLinkSlow or AdvisoryCode.SlowDns or AdvisoryCode.MeteredConnection => OpenSettings(NetworkPage(context), title),
        AdvisoryCode.AdapterPowerSaving or AdvisoryCode.EnergyEfficientEthernet => Helper(RecommendationActionKind.Open, "Action_OpenDeviceManager", "devmgmt.msc", title),
        AdvisoryCode.UsbSelectiveSuspend => Helper(RecommendationActionKind.Open, "Action_OpenPowerOptions", "powercfg.cpl", title),
        AdvisoryCode.PowerSaving when a.Variant is "WifiBattery" or "WifiPlugged" => Helper(RecommendationActionKind.Open, "Action_OpenPowerOptions", "powercfg.cpl", title),
        AdvisoryCode.PowerSaving => OpenSettings("ms-settings:powersleep", title),
        AdvisoryCode.OldNetworkDriver when a.Variant == "Intel" => Helper(RecommendationActionKind.Open, "Action_OpenDownloadPage", IntelDriverPage, title),
        AdvisoryCode.OldNetworkDriver => OpenSettings("ms-settings:windowsupdate-optionalupdates", title),
        AdvisoryCode.ProxyConfigured => OpenSettings("ms-settings:network-proxy", title),
        AdvisoryCode.VpnActive => OpenSettings("ms-settings:network-vpn", title),
        AdvisoryCode.Ipv6Disabled when a.Variant == "Adapter" => Helper(RecommendationActionKind.Open, "Action_OpenNetworkConnections", "ncpa.cpl", title),
        AdvisoryCode.Ipv6Disabled when a.Variant == "Registry" => null, // needs an administrator
        AdvisoryCode.Ipv6Disabled => OpenSettings("ms-settings:network-advancedsettings", title),
        _ => null,
    };

    /// <summary>Fix instructions using this check's evidence: which app on this PC, or that this PC was quiet (so another
    /// device is the cause), the router's address, and what to tell the provider. Generic text only without evidence.</summary>
    private string RemedyDetail(ActionCode code, SavedResult saved, CultureInfo? ui = null, bool redact = false)
    {
        var culture = ui ?? _ui;
        var f = redact ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture;
        var traffic = saved.Advisories?.FirstOrDefault(x => x.Code == AdvisoryCode.OtherTraffic);
        return code switch
        {
            ActionCode.PauseHouseholdUploads when traffic is { Args.Count: >= 2 } =>
                string.Format(f, _l.Get("Remedy_PauseHouseholdUploads_DetailThisPc", culture), Arg(traffic.Args[0], culture, redact), Arg(traffic.Args[1], culture, redact)),
            ActionCode.PauseHouseholdUploads when saved.Passed?.Contains(CheckArea.OtherTraffic) == true =>
                _l.Get("Remedy_PauseHouseholdUploads_DetailOtherDevices", culture),
            ActionCode.RestartRouter when saved.Context.Gateway is { Length: > 0 } gateway =>
                string.Format(f, _l.Get("Remedy_RestartRouter_DetailNamed", culture), redact ? "[address]" : gateway),
            ActionCode.ContactIsp when saved.Report.Issues.Count > 0 =>
                string.Format(f, _l.Get("Remedy_ContactIsp_DetailNamed", culture), redact ? "[what the check found]" : Describe(saved.Report)),
            _ => _l.Get($"Remedy_{code}_Detail", culture),
        };
    }

    private RecommendationAction? ActionFor(ActionCode code, CheckContext context)
    {
        string title = ActionTitle(code);
        return code switch
        {
            ActionCode.RestartRouter => Router(context, title),
            ActionCode.DisconnectVpn => OpenSettings("ms-settings:network-vpn", title),
            ActionCode.PauseHouseholdUploads => Helper(RecommendationActionKind.Open, "Action_OpenTaskManager", "taskmgr", title),
            ActionCode.ContactIsp => Helper(RecommendationActionKind.CopySummary, "Action_CopySummary", "", title),
            _ => null,
        };
    }

    private RecommendationAction Helper(RecommendationActionKind kind, string labelKey, string target, string title)
    {
        string label = _l.Get(labelKey, _ui);
        return new(kind, label, target, $"{label}: {title}");
    }

    private RecommendationAction OpenSettings(string uri, string title) => Helper(RecommendationActionKind.Open, "Action_OpenSettings", uri, title);

    private RecommendationAction? Router(CheckContext context, string title) =>
        context.Gateway is { Length: > 0 } gateway ? Helper(RecommendationActionKind.Open, "Action_OpenRouter", $"http://{gateway}/", title) : null;

    private static string NetworkPage(CheckContext context) => context.Medium switch
    {
        ConnectionMedium.WiFi => "ms-settings:network-wifi",
        ConnectionMedium.Ethernet => "ms-settings:network-ethernet",
        _ => "ms-settings:network-status",
    };

    /// <summary>Plain-text summary for the internet provider: check time, findings and the latest measurements.</summary>
    private string SummaryText()
    {
        string metrics = string.Join(" · ", Metrics.Where(m => m.Value != "—").Select(m => $"{m.Label}: {m.Value}"));
        return string.Join(Environment.NewLine, new[] { "ConnectionClue", RecommendationTime, RecommendationFindings, metrics }.Where(s => s.Length > 0));
    }

    private string AdviceTitle(Advisory a) => Render(a, "Title");

    private void UpdateStale() => RecommendationsStale =
        _saved is { } s && (_savedFromPreviousSession || _time.GetUtcNow() - s.CheckedAtUtc > StaleAfter);

    private string ActionTitle(ActionCode action, CultureInfo? ui = null) => _l.Get($"Remedy_{action}_Title", ui ?? _ui);

    private string JoinList(IEnumerable<string> items) => string.Join(_ui.TwoLetterISOLanguageName switch
    {
        "zh" or "ja" => "、",
        "ar" or "ur" => "، ",
        _ => ", ",
    }, items);

    private string Headline(HealthLevel level) => _l.Get(level switch
    {
        HealthLevel.NoIssue => "Health_NoIssue",
        HealthLevel.Degraded => "Alert_Slow",
        HealthLevel.Unhealthy => "Alert_Problem",
        _ => "Health_Inconclusive",
    }, _ui);

    private string Describe(HealthReport report)
    {
        if (report.Issues.Count == 0) return Headline(report.Level);
        string gap = _ui.TwoLetterISOLanguageName is "zh" or "ja" ? "" : " ";
        return string.Join(gap, report.Issues.Select(i => Sentence(i, gap)));
    }

    private string Sentence(HealthIssue i, string gap)
    {
        var f = CultureInfo.CurrentCulture;
        string F(string key, params object[] args) => string.Format(f, _l.Get(key, _ui), args);
        string Pct(double v) => (v / 100).ToString("P0", f);
        string where = i.Location switch
        {
            IssueLocation.LocalNetwork => gap + _l.Get("Issue_WhereLocal", _ui),
            IssueLocation.BeyondRouter => gap + _l.Get("Issue_WhereUpstream", _ui),
            _ => "",
        };
        return i.Kind switch
        {
            IssueKind.Interrupted when i.Location == IssueLocation.LocalNetwork => _l.Get("Issue_LocalInterrupted", _ui),
            IssueKind.Interrupted => F("Issue_InternetInterrupted", Math.Round(i.Value)) + where,
            IssueKind.WebUnreachable => _l.Get("Issue_WebFailing", _ui),
            IssueKind.Loss => F("Issue_Loss", Pct(i.Value), Pct(i.Limit)) + where,
            IssueKind.Delay => F("Issue_Delay", Math.Round(i.Value), Math.Round(i.Limit)) + where,
            _ => F("Issue_Variation", Math.Round(i.Value), Math.Round(i.Limit)) + where,
        };
    }

    private void Say(string key, AnnouncementKind kind) => Emit(_l.Get(key, _ui), kind);

    private void Emit(string text, AnnouncementKind kind)
    {
        var decision = _announcements.Evaluate(kind);
        if (decision.Announce) Announce?.Invoke(this, new Announcement(text, decision.Urgency));
    }
}
