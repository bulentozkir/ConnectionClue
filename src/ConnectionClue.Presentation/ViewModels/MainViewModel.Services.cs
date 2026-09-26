using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.Presentation.ViewModels;

/// <summary>One tested service. Reached picks the icon; the text always says the outcome.</summary>
public sealed record ServiceResultItem(string Name, string Endpoint, string Result, bool Reached)
{
    public string AccessibleText => $"{Name} ({Endpoint}): {Result}";
}

/// <summary>Real services behind each "What's happening?" choice (game platforms, call relays, streaming, connectivity).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Results of the latest service test for the selected choice.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ServiceResultItem> ServiceResults { get; set; } = [];

    /// <summary>One-sentence outcome of the latest service test (status card and Insights); empty before a test.</summary>
    [ObservableProperty]
    public partial string ServiceSummary { get; set; } = "";

    private Symptom SelectedSymptomValue =>
        Enum.TryParse<Symptom>(SelectedSymptom?.Key.Replace("Symptom_", "", StringComparison.Ordinal), out var symptom) ? symptom : Symptom.Gaming;

    /// <summary>The built-in services tested for the selected choice, for example "Xbox network, Steam, Epic Games".</summary>
    public string ServiceNames => JoinList(SymptomServices.For(SelectedSymptomValue).Select(s => s.Name));

    private bool CanTestServices() => _serviceTargetProbe is not null && !IsRunning && !IsDisconnected && !IsTestingTarget;

    [RelayCommand(CanExecute = nameof(CanTestServices))]
    private Task TestServicesAsync() => RunServiceTestAsync();

    /// <summary>Tests the built-in services for the selected choice plus the user's own target, if one is saved.</summary>
    private async Task RunServiceTestAsync()
    {
        if (_serviceTargetProbe is null) return;
        if (_isConnected?.Invoke() == false)
        {
            UpdateConnectivity(false, IsMobileNetwork);
            ServiceSummary = _l.Get("Target_Disconnected", _ui);
            Emit(ServiceSummary, AnnouncementKind.Error);
            return;
        }
        string label = SelectedSymptom?.Label ?? "";
        var services = SymptomServices.For(SelectedSymptomValue).ToList();
        bool invalidCustom = false;
        if (!string.IsNullOrWhiteSpace(FocusedTarget))
        {
            if (ServiceTargetParser.TryParse(FocusedTarget, out var custom) && custom is not null) services.Add(new(_l.Get("Service_Custom", _ui), custom));
            else invalidCustom = true;
        }
        IsTestingTarget = true;
        ServiceSummary = string.Format(_ui, _l.Get("Service_Testing", _ui), label);
        Emit(ServiceSummary, AnnouncementKind.SessionState);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var results = await SymptomServices.TestAsync(_serviceTargetProbe, services, timeout.Token);
            ServiceResults = [.. results.Select(ToServiceItem)];
            var reached = results.Where(r => r.Status == ServiceTargetStatus.Connected).ToList();
            var slowest = reached.MaxBy(r => r.MedianMilliseconds);
            string summary = slowest is null
                ? string.Format(_ui, _l.Get("Service_SummaryNone", _ui), label, results.Count)
                : string.Format(_ui, _l.Get("Service_Summary", _ui), label, reached.Count, results.Count, slowest.Service.Name,
                    MsText(slowest.MedianMilliseconds));
            ServiceSummary = invalidCustom ? summary + " " + _l.Get("Target_Invalid", _ui) : summary;
            Emit(ServiceSummary, AnnouncementKind.FindingReady);
        }
        catch (OperationCanceledException)
        {
            ServiceSummary = string.Format(_ui, _l.Get("Service_TimedOutAll", _ui), label);
            Emit(ServiceSummary, AnnouncementKind.Error);
        }
        finally
        {
            IsTestingTarget = false;
        }
    }

    private ServiceResultItem ToServiceItem(ServiceTestResult r) => new(r.Service.Name, $"{r.Service.Target.Host}:{r.Service.Target.Port}",
        r.Status switch
        {
            ServiceTargetStatus.Connected => string.Format(_ui, _l.Get("Service_Connected", _ui), MsText(r.MedianMilliseconds)),
            ServiceTargetStatus.TimedOut => _l.Get("Service_TimedOut", _ui),
            ServiceTargetStatus.Refused => _l.Get("Service_Refused", _ui),
            ServiceTargetStatus.NameLookupFailed => _l.Get("Service_NameLookupFailed", _ui),
            _ => _l.Get("Service_Unreachable", _ui),
        }, r.Status == ServiceTargetStatus.Connected);
}
