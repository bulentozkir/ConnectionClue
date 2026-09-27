using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.History;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class SessionFeatureTests : IDisposable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly (CultureInfo, CultureInfo) _cultures = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly HistoryStore _history = new();
    private readonly Exporter _exporter = new();

    public SessionFeatureTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = En;

    public void Dispose() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = _cultures;

    private MainViewModel Vm() => new(Localizer.Default, En, _time, (_, _) => Task.FromResult(Probes()), new(),
        new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false, checkSeconds: 10, measureSpeed: false, aiReview: false),
        new NoStore(), historyStore: _history, resultsExporter: _exporter);

    private static PreviewProbes Probes() =>
        new(new Probe(3), new Probe(20), new Probe(15), new Probe(60), new CheckContext(ConnectionMedium.Ethernet));

    private async Task RunToEnd(Task check, TimeSpan? step = null)
    {
        for (int i = 0; i < 60 && !check.IsCompleted; i++)
        {
            _time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await check;
    }

    [Fact]
    public void Symptom_choice_is_a_single_selection_that_cannot_be_cleared()
    {
        using var vm = Vm();
        vm.SelectSymptomCommand.Execute(vm.Symptoms[2]);
        Assert.Same(vm.Symptoms[2], vm.SelectedSymptom);
        vm.SelectSymptomCommand.Execute(null);
        Assert.Same(vm.Symptoms[2], vm.SelectedSymptom);
    }

    [Fact]
    public void Clearing_history_asks_first_and_can_be_cancelled()
    {
        _history.Entries = [.. Enumerable.Range(1, 3).Select(h => new CheckHistoryEntry(_time.GetUtcNow().AddHours(-h), HealthLevel.NoIssue,
            100, 20, 18, 2, null, null))];
        using var vm = Vm();
        Assert.StartsWith("Saved check summaries: 3.", vm.HistoryCountText, StringComparison.Ordinal);

        vm.ClearHistoryCommand.Execute(null);
        Assert.True(vm.IsConfirmingClearHistory);
        Assert.Equal("Clear all saved check summaries (3)? This can't be undone.", vm.ClearHistoryConfirmText);
        vm.CancelClearHistoryCommand.Execute(null);
        Assert.False(vm.IsConfirmingClearHistory);
        Assert.Equal(3, vm.HistoryRows.Count);

        vm.ClearHistoryCommand.Execute(null);
        vm.ConfirmClearHistoryCommand.Execute(null);
        Assert.Empty(_history.Entries);
        Assert.Empty(vm.HistoryRows);
        Assert.Equal("Insights history cleared.", vm.HistoryClearStatus);
        Assert.False(vm.ClearHistoryCommand.CanExecute(null)); // nothing left to clear
    }

    [Fact]
    public async Task Export_appears_after_a_quick_check_and_carries_the_full_results()
    {
        using var vm = Vm();
        Assert.False(vm.ShowExportResults);
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        Assert.True(vm.ShowExportResults);

        await vm.ExportResultsCommand.ExecuteAsync(null);
        var data = _exporter.Last!;
        Assert.Equal("ConnectionClue check results", data.Title);
        Assert.StartsWith("Quick check · ", data.Subtitle, StringComparison.Ordinal);
        Assert.StartsWith("ConnectionClue results 2026-09-27 ", data.FileName, StringComparison.Ordinal);
        Assert.Equal(["Result", "Findings", "Measurements", "Path steps", "Check details"], data.Sections.Select(s => s.Heading));
        Assert.Contains("Delay measured for 10 seconds", data.Sections[^1].Lines);
        Assert.Equal(2, data.VisualCaptions.Count);
        var samples = Assert.Single(data.Tables);
        Assert.Equal(vm.Lanes.Sum(l => l.Samples.Count), samples.Rows.Count);
        Assert.Null(samples.Note);
        Assert.Equal("Sent to Microsoft Print to PDF. Choose where to save the file when Windows asks.", vm.ResultsExportStatus);
    }

    [Fact]
    public async Task A_background_check_replaces_the_results_so_export_hides()
    {
        using var vm = Vm();
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        await RunToEnd(vm.RunCheckAsync(measureSpeed: false));
        Assert.False(vm.ShowExportResults);
        Assert.False(vm.ExportResultsCommand.CanExecute(null));
        Assert.False(vm.ExportMhtmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Mhtml_exports_the_same_results_as_pdf_after_a_quick_check()
    {
        using var vm = Vm();
        Assert.False(vm.ExportMhtmlCommand.CanExecute(null));
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        await vm.ExportResultsCommand.ExecuteAsync(null);
        var pdf = _exporter.Last!;
        await vm.ExportMhtmlCommand.ExecuteAsync(null);
        var mhtml = _exporter.Last!;
        Assert.Equal(pdf.Title, mhtml.Title);
        Assert.Equal(pdf.Sections.SelectMany(s => s.Lines), mhtml.Sections.SelectMany(s => s.Lines));
        Assert.Equal(pdf.Tables.SelectMany(t => t.Rows).SelectMany(r => r), mhtml.Tables.SelectMany(t => t.Rows).SelectMany(r => r));
        Assert.Equal(pdf.VisualCaptions, mhtml.VisualCaptions);
        Assert.Equal(1, _exporter.PdfCalls);
        Assert.Equal(1, _exporter.MhtmlCalls);
        Assert.Equal("MHTML archive saved. Open it in Microsoft Edge or another MHTML-compatible browser.", vm.ResultsExportStatus);
    }

    [Fact]
    public async Task Both_exports_are_available_after_a_longer_capture()
    {
        using var vm = Vm();
        var capture = vm.LongCaptureCommand.ExecuteAsync(null);
        Assert.False(vm.ExportResultsCommand.CanExecute(null));
        Assert.False(vm.ExportMhtmlCommand.CanExecute(null));
        await RunToEnd(capture, TimeSpan.FromMinutes(1));
        Assert.True(vm.ExportResultsCommand.CanExecute(null));
        Assert.True(vm.ExportMhtmlCommand.CanExecute(null));
        await vm.ExportMhtmlCommand.ExecuteAsync(null);
        Assert.StartsWith("Longer capture (15 min)", _exporter.Last!.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_serializes_the_actions_and_prevents_a_new_check_from_replacing_its_results()
    {
        using var vm = Vm();
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        var pending = new TaskCompletionSource<ExportOutcome>();
        _exporter.Mhtml = _ => pending.Task;
        var export = vm.ExportMhtmlCommand.ExecuteAsync(null);
        Assert.True(vm.IsExportingResults);
        Assert.False(vm.ExportResultsCommand.CanExecute(null));
        Assert.False(vm.ExportMhtmlCommand.CanExecute(null));
        Assert.False(vm.QuickCheckCommand.CanExecute(null));
        Assert.False(vm.LongCaptureCommand.CanExecute(null));
        await vm.ExportResultsCommand.ExecuteAsync(null);
        await vm.RunCheckAsync(measureSpeed: false);
        Assert.Equal(0, _exporter.PdfCalls);
        Assert.False(vm.IsRunning);
        pending.SetResult(ExportOutcome.Saved);
        await export;
        Assert.False(vm.IsExportingResults);
        Assert.True(vm.ExportResultsCommand.CanExecute(null));
        Assert.True(vm.ExportMhtmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Mhtml_save_failure_is_reported_and_does_not_disable_future_exports()
    {
        using var vm = Vm();
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        _exporter.Mhtml = _ => Task.FromException<ExportOutcome>(new IOException("Disk is full."));
        await vm.ExportMhtmlCommand.ExecuteAsync(null);
        Assert.StartsWith("The MHTML archive could not be saved.", vm.ResultsExportStatus, StringComparison.Ordinal);
        Assert.False(vm.IsExportingResults);
        Assert.True(vm.ExportMhtmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelled_mhtml_export_does_not_claim_a_saved_file()
    {
        using var vm = Vm();
        await RunToEnd(vm.QuickCheckCommand.ExecuteAsync(null));
        _exporter.Mhtml = _ => Task.FromResult(ExportOutcome.Cancelled);
        await vm.ExportMhtmlCommand.ExecuteAsync(null);
        Assert.Equal("The export was canceled.", vm.ResultsExportStatus);
        Assert.False(vm.IsExportingResults);
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

    private sealed class Exporter : IResultsExporter
    {
        public ResultsExportData? Last { get; private set; }
        public int PdfCalls { get; private set; }
        public int MhtmlCalls { get; private set; }
        public Func<ResultsExportData, Task<ExportOutcome>>? Mhtml { get; set; }

        public Task<ExportOutcome> ExportPdfAsync(ResultsExportData data, CancellationToken cancellationToken)
        {
            Last = data;
            PdfCalls++;
            return Task.FromResult(ExportOutcome.Sent);
        }

        public Task<ExportOutcome> ExportMhtmlAsync(ResultsExportData data, CancellationToken cancellationToken)
        {
            Last = data;
            MhtmlCalls++;
            return Mhtml?.Invoke(data) ?? Task.FromResult(ExportOutcome.Saved);
        }
    }
}
