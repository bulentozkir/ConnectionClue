using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.Presentation.ViewModels;

/// <summary>What started a check: the Quick check button, Capture longer, or the background schedule.</summary>
public enum CheckKind { Quick, Long, Background, Reconnect }

/// <summary>Export the finished quick check or longer capture, including its visuals, as PDF or MHTML.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Most table rows per path step; longer captures are sampled evenly, preferring unanswered checks.</summary>
    public const int MaxExportRowsPerStep = 1000;

    private readonly IResultsExporter? _resultsExporter;
    private CheckKind _currentKind = CheckKind.Background;
    private CheckKind? _lastKind;
    private HealthReport? _lastReport;
    private double _lastCheckSeconds;

    /// <summary>What happened to the latest export; empty before one.</summary>
    [ObservableProperty]
    public partial string ResultsExportStatus { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanExportResults))]
    [NotifyCanExecuteChangedFor(nameof(ExportResultsCommand), nameof(ExportMhtmlCommand), nameof(QuickCheckCommand), nameof(LongCaptureCommand))]
    public partial bool IsExportingResults { get; set; }

    /// <summary>After a Quick check or a longer capture, until the next check starts. A background check replaces what the
    /// Check page shows, so it hides the button again.</summary>
    public bool ShowExportResults => _resultsExporter is not null && _lastKind is (CheckKind.Quick or CheckKind.Long) && !IsRunning;

    public bool CanExportResults => ShowExportResults && !IsExportingResults;

    private void RememberForExport(HealthReport report)
    {
        (_lastKind, _lastReport, _lastCheckSeconds, ResultsExportStatus) = (_currentKind, report, _idleSeconds, "");
        OnPropertyChanged(nameof(ShowExportResults));
        OnPropertyChanged(nameof(CanExportResults));
        ExportResultsCommand.NotifyCanExecuteChanged();
        ExportMhtmlCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExportResults))]
    private Task ExportResultsAsync() => ExportAsync(mhtml: false);

    [RelayCommand(CanExecute = nameof(CanExportResults))]
    private Task ExportMhtmlAsync() => ExportAsync(mhtml: true);

    private async Task ExportAsync(bool mhtml)
    {
        if (_resultsExporter is null || !CanExportResults) return;
        IsExportingResults = true;
        ResultsExportStatus = "";
        ExportOutcome outcome;
        try
        {
            var data = BuildResultsExport();
            outcome = mhtml
                ? await _resultsExporter.ExportMhtmlAsync(data, CancellationToken.None)
                : await _resultsExporter.ExportPdfAsync(data, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            outcome = ExportOutcome.Cancelled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            outcome = ExportOutcome.Failed;
        }
        finally
        {
            IsExportingResults = false;
        }
        ResultsExportStatus = Loc(outcome switch
        {
            ExportOutcome.Sent => "Export_Sent",
            ExportOutcome.Saved => "Export_MhtmlSaved",
            ExportOutcome.Cancelled => "Export_Cancelled",
            ExportOutcome.NoPdfPrinter => "Export_NoPdfPrinter",
            _ => mhtml ? "Export_MhtmlFailed" : "Export_Failed",
        });
        Emit(ResultsExportStatus, outcome is ExportOutcome.Sent or ExportOutcome.Saved or ExportOutcome.Cancelled
            ? AnnouncementKind.SessionState : AnnouncementKind.Error);
    }

    /// <summary>Everything the finished check found and measured, in the current language; the view adds the images.</summary>
    public ResultsExportData BuildResultsExport()
    {
        var f = CultureInfo.CurrentCulture;
        var started = TimeZoneInfo.ConvertTime(_samplesStartUtc, _time.LocalTimeZone);
        string kind = _lastKind switch
        {
            CheckKind.Long => LocFormat("Export_KindLong", _lastCheckSeconds >= 3600
                ? string.Format(f, "{0:0.#} h", _lastCheckSeconds / 3600) : string.Format(f, "{0:0} min", _lastCheckSeconds / 60)),
            CheckKind.Quick => Loc("Export_KindQuick"),
            CheckKind.Reconnect => Loc("Export_KindReconnect"),
            _ => Loc("Export_KindBackground"),
        };
        string gap = _ui.TwoLetterISOLanguageName is "zh" or "ja" ? "" : " ";
        static IEnumerable<string> Lines(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var details = new List<string>
        {
            LocFormat("Export_DetailKind", kind),
            LocFormat("Export_DetailStarted", started.ToString("F", f)),
            LocFormat("Export_DetailLength", _lastCheckSeconds.ToString("0", f)),
            Loc(_downloadMbps is not null || _uploadMbps is not null ? "Export_DetailSpeedOn" : "Export_DetailSpeedOff"),
            LocFormat("Export_DetailLimits", Settings.DelayLimitMs, Settings.LossLimitPercent, Settings.VariationLimitMs),
            LocFormat("Export_DetailConnection", Loc(_context.Medium switch
            {
                ConnectionMedium.WiFi => "Export_MediumWiFi",
                ConnectionMedium.Ethernet => "Export_MediumEthernet",
                _ => "Export_MediumUnknown",
            })),
        };
        if (_context.TunnelSuspected) details.Add(Loc("Export_DetailVpn"));
        if (_context.Metered) details.Add(Loc("Export_DetailMetered"));

        ResultsSection Section(string key, IEnumerable<string> lines) =>
            new(Loc(key), [.. lines.Where(l => !string.IsNullOrWhiteSpace(l))]);
        ResultsSection[] sections =
        [
            Section("Export_SectionResult", [HeroTitle, .. Lines(Summary), .. Lines(TryPreview), CheckedOkText, BufferbloatText, ServiceSummary, MarkersText]),
            Section("Export_SectionFindings", [.. VerdictSentences(_lastVerdicts, _lastVerdictsStartUtc, _context.Medium),
                .. _lastReport?.Issues.Select(i => Sentence(i, gap)) ?? []]),
            Section("Export_SectionMeasurements", Metrics.Select(m => m.AccessibleText)),
            Section("Export_SectionPath", Lanes.Select(l => $"{l.AccessibleText} · {l.StatsText}")),
            Section("Export_SectionServices", ServiceResults.Select(r => r.AccessibleText)),
            Section("Action_Recommendations", [.. Recommendations.Select(r => r.AccessibleText), .. BestPractices.Select(a => a.AccessibleText)]),
            Section("Export_SectionDetails", details),
        ];

        var rows = new List<IReadOnlyList<string>>();
        int omitted = 0;
        foreach (var lane in Lanes)
        {
            var points = Downsample(lane.Samples, MaxExportRowsPerStep);
            omitted += lane.Samples.Count - points.Count;
            foreach (var s in points)
            {
                string at = TimeZoneInfo.ConvertTime(_samplesStartUtc.AddSeconds(s.Seconds), _time.LocalTimeZone).ToString("T", f)
                    + string.Format(f, " (+{0:0.#} s)", s.Seconds);
                rows.Add([lane.Name, at, s.Milliseconds is { } ms ? string.Format(f, "{0:0.#} ms", ms) : Loc("SupportReport_NoResponse")]);
            }
        }
        var samples = new ResultsTable(Loc("Export_SamplesHeading"),
            [Loc("SupportReport_StepHeader"), Loc("SupportReport_TimeHeader"), Loc("SupportReport_ObservationHeader")], rows,
            omitted > 0 ? LocFormat("Export_SamplesOmitted", omitted, MaxExportRowsPerStep) : null);

        return new ResultsExportData(_ui.Name, _ui.TextInfo.IsRightToLeft, Loc("Export_Title"), $"{kind} · {started.ToString("f", f)}",
            "ConnectionClue results " + started.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture),
            [.. sections.Where(s => s.Lines.Count > 0)], Loc("Export_VisualsHeading"),
            [Loc("Export_VisualCheckPage"), Loc("Export_VisualChart")], [samples], Loc("Export_Footer"));
    }

    /// <summary>At most max samples, evenly spread; within each stretch an unanswered check is preferred.</summary>
    private static List<Sample> Downsample(IReadOnlyList<Sample> samples, int max)
    {
        int stride = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)max));
        var points = new List<Sample>(Math.Min(samples.Count, max));
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
            points.Add(selected);
        }
        return points;
    }
}
