using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectionClue.Presentation.Accessibility;
using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.Presentation.ViewModels;

/// <summary>Insights tools: hop view, DNS comparison and switch, Wi-Fi channels. Each runs only when the user asks.</summary>
public sealed partial class MainViewModel
{
    private DnsChoice _dnsChoice = new(null, false);

    /// <summary>Outcome of the latest DNS switch or restore; empty before one.</summary>
    [ObservableProperty]
    public partial string DnsStatus { get; set; } = "";

    /// <summary>Windows blocked the Wi-Fi scan until location access is on; the card then offers a shortcut to that setting.</summary>
    [ObservableProperty]
    public partial bool WifiNeedsLocation { get; set; }

    [RelayCommand]
    private void OpenLocationSettings() => _shell?.Open("ms-settings:privacy-location");

    /// <summary>"Use Cloudflare DNS" once a comparison found a clearly faster service; a generic label before that.</summary>
    public string SwitchDnsLabel => _dnsChoice.Fastest is { } fastest
        ? string.Format(_ui, _l.Get("Diagnostics_UseDns", _ui), fastest.Provider)
        : _l.Get("Diagnostics_UseFastestDns", _ui);

    private string L(string key) => _l.Get(key, _ui);

    private string F(string key, params object?[] args) => string.Format(_ui, _l.Get(key, _ui), args);

    [RelayCommand(CanExecute = nameof(CanRunNetworkTool))]
    private async Task TraceRouteAsync()
    {
        if (_networkDiagnostics is null) return;
        if (!NetworkAvailableForTool())
        {
            TraceRouteText = L("Diagnostics_Disconnected");
            return;
        }
        IsRunningNetworkTool = true;
        TraceRouteText = L("Diagnostics_Running");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var hops = await _networkDiagnostics.TraceRouteAsync(timeout.Token);
            if (hops.Count == 0)
            {
                TraceRouteText = L("Diagnostics_NoResults");
                return;
            }
            string summary = TraceSummaryText(hops);
            TraceRouteText = string.Join(Environment.NewLine, [summary, .. hops.Select(TraceRow)]);
            Emit(summary, AnnouncementKind.FindingReady);
        }
        catch (OperationCanceledException)
        {
            TraceRouteText = L("Diagnostics_TimedOut");
        }
        finally
        {
            IsRunningNetworkTool = false;
        }
    }

    /// <summary>Which hop adds lasting delay, where it probably is, and where lasting loss starts.</summary>
    private string TraceSummaryText(IReadOnlyList<TraceHop> hops)
    {
        var s = DiagnosticsAnalysis.Summarize(hops);
        var f = CultureInfo.CurrentCulture;
        string text = s.DelayHop is not { } hop
            ? F("Diagnostics_TraceNoDelay", DiagnosticsAnalysis.NotableIncreaseMs.ToString("0", f))
            : F("Diagnostics_TraceDelay", hop, s.DelayAddress ?? "—", s.DelayIncreaseMs!.Value.ToString("0", f)) + " "
              + L(hop == 1 ? "Diagnostics_TraceWhereRouter"
                  : DiagnosticsAnalysis.IsPrivate(s.DelayAddress) ? "Diagnostics_TraceWherePrivate" : "Diagnostics_TraceWherePublic");
        return s.LossFromHop is { } loss ? text + " " + F("Diagnostics_TraceLoss", loss) : text;
    }

    private string TraceRow(TraceHop h)
    {
        var f = CultureInfo.CurrentCulture;
        string?[] parts =
        [
            h.Address ?? "—",
            h.MedianRoundTripMilliseconds is { } ms ? ms.ToString("0.#", f) + " ms" : null,
            F("Diagnostics_TraceReplies", h.Replies, h.Probes),
            h.IncreaseFromPreviousHopMilliseconds is { } up and >= 0.5 ? F("Diagnostics_TraceIncrease", up.ToString("0.#", f)) : null,
            ToolStatus(h.Status),
        ];
        return F("Diagnostics_TraceRow", h.Hop, string.Join(" · ", parts.Where(p => p is not null)));
    }

    [RelayCommand(CanExecute = nameof(CanRunNetworkTool))]
    private async Task ComparePublicDnsAsync()
    {
        if (_networkDiagnostics is null) return;
        if (!NetworkAvailableForTool())
        {
            DnsComparisonText = L("Diagnostics_Disconnected");
            return;
        }
        IsRunningNetworkTool = true;
        DnsComparisonText = L("Diagnostics_Running");
        DnsStatus = "";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var results = await _networkDiagnostics.ComparePublicDnsAsync(timeout.Token);
            _dnsMeasurements = results;
            _dnsChoice = DiagnosticsAnalysis.ChooseDns(results);
            OnPropertyChanged(nameof(SwitchDnsLabel));
            bool currentMeasured = results.Any(r => r.IsCurrent && r.MedianMilliseconds is not null);
            string verdict = _dnsChoice.Fastest is { } fastest
                ? F(currentMeasured ? "Diagnostics_DnsFaster" : "Diagnostics_DnsFastest", fastest.Provider, MsText(fastest.MedianMilliseconds))
                : _dnsChoice.KeepCurrent ? L("Diagnostics_DnsKeepCurrent") : L("Diagnostics_NoResults");
            DnsComparisonText = string.Join(Environment.NewLine, [verdict, .. results.Select(r => F("Diagnostics_DnsRow",
                !r.IsCurrent ? r.Provider : r.Provider.Length > 0 ? F("Diagnostics_DnsCurrentNamed", r.Provider) : L("Diagnostics_DnsCurrent"),
                r.Server, MsText(r.MedianMilliseconds), r.SuccessfulQueries, ToolStatus(r.Status)))]);
            Emit(verdict, AnnouncementKind.FindingReady);
        }
        catch (OperationCanceledException)
        {
            DnsComparisonText = L("Diagnostics_TimedOut");
        }
        finally
        {
            IsRunningNetworkTool = false;
        }
    }

    private bool CanSwitchDns() => CanRunNetworkTool() && _dnsChoice.Fastest is not null;

    // Changing DNS mid-check would flush the cache and fake name-lookup failures, so neither runs during a check.
    private bool CanRestoreDns() => _networkDiagnostics is not null && !IsRunning && !IsRunningNetworkTool;

    [RelayCommand(CanExecute = nameof(CanSwitchDns))]
    private Task SwitchToFastestDnsAsync() => ChangeDnsAsync(_dnsChoice.Fastest?.Provider);

    [RelayCommand(CanExecute = nameof(CanRestoreDns))]
    private Task RestoreAutomaticDnsAsync() => ChangeDnsAsync(null);

    /// <summary>Switches DNS (provider) or restores automatic DNS (null). Windows asks for administrator approval.</summary>
    private async Task ChangeDnsAsync(string? provider)
    {
        if (_networkDiagnostics is null) return;
        IsRunningNetworkTool = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // includes answering the Windows prompt
            var result = await _networkDiagnostics.SetDnsAsync(provider, timeout.Token);
            DnsStatus = result switch
            {
                DnsSwitchResult.Switched => F("Diagnostics_DnsSwitched", provider),
                DnsSwitchResult.Restored => L("Diagnostics_DnsRestored"),
                DnsSwitchResult.Cancelled => L("Diagnostics_DnsSwitchCancelled"),
                DnsSwitchResult.NoAdapter => L("Diagnostics_DnsNoAdapter"),
                _ => L("Diagnostics_DnsSwitchFailed"),
            };
            bool changed = result is DnsSwitchResult.Switched or DnsSwitchResult.Restored;
            if (changed)
            {
                _dnsChoice = new(null, false); // the comparison no longer describes this PC's DNS
                OnPropertyChanged(nameof(SwitchDnsLabel));
            }
            Emit(DnsStatus, changed ? AnnouncementKind.SessionState : AnnouncementKind.Error);
        }
        catch (OperationCanceledException)
        {
            DnsStatus = L("Diagnostics_DnsSwitchFailed");
            Emit(DnsStatus, AnnouncementKind.Error);
        }
        finally
        {
            IsRunningNetworkTool = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunNetworkTool))]
    private async Task ScanWifiAsync()
    {
        if (_networkDiagnostics is null) return;
        if (!NetworkAvailableForTool())
        {
            WifiAnalysisText = L("Diagnostics_Disconnected");
            return;
        }
        IsRunningNetworkTool = true;
        WifiAnalysisText = L("Diagnostics_Running");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _networkDiagnostics.ScanWifiAsync(timeout.Token);
            WifiNeedsLocation = result.Status == WifiScanStatus.PermissionDenied;
            var advice = result.Status == WifiScanStatus.Success ? WifiAdviceLines(result.Channels).ToList() : [];
            WifiAnalysisText = result.Status switch
            {
                WifiScanStatus.NoAdapter => L("Diagnostics_WifiNoAdapter"),
                WifiScanStatus.NoNetworks => L("Diagnostics_WifiNoNetworks"),
                WifiScanStatus.PermissionDenied => L("Diagnostics_WifiPermission"),
                WifiScanStatus.Failed => L("Diagnostics_WifiFailed"),
                _ when result.Channels.Count == 0 => L("Diagnostics_NoResults"),
                _ => string.Join(Environment.NewLine, [.. advice, .. result.Channels.Select(c => F("Diagnostics_WifiRow", c.Band, c.Channel,
                    c.NearbyNetworks, (c.Recommended ? L("Diagnostics_Recommended") : "") + (c.Connected ? L("Diagnostics_WifiYours") : "")))]),
            };
            if (advice.Count > 0) Emit(string.Join(" ", advice), AnnouncementKind.FindingReady);
        }
        catch (OperationCanceledException)
        {
            WifiAnalysisText = L("Diagnostics_TimedOut");
        }
        finally
        {
            IsRunningNetworkTool = false;
        }
    }

    /// <summary>The connected channel, a quieter channel on the same band, band advice, and the quietest channel per band.</summary>
    private IEnumerable<string> WifiAdviceLines(IReadOnlyList<WifiChannelSummary> channels)
    {
        var advice = DiagnosticsAnalysis.AdviseWifi(channels);
        if (advice.Connected is { } connected) yield return F("Diagnostics_WifiConnected", connected.Band, connected.Channel, connected.NearbyNetworks);
        if (advice.BetterChannel is { } better) yield return F("Diagnostics_WifiChangeChannel", better.Channel, better.Band, better.NearbyNetworks);
        if (advice.FasterBandNearby && advice.Connected?.Band == "2.4") yield return L("Diagnostics_WifiBandAdvice");
        if (advice.Best.Count > 0) yield return F("Diagnostics_WifiBest", JoinList(advice.Best.Select(b => F("Diagnostics_WifiChannel", b.Channel, b.Band))));
    }

    [RelayCommand]
    private void OpenNetworkSettings()
    {
        if (_networkDiagnostics is null) return;
        try { _networkDiagnostics.OpenNetworkSettings(); }
        catch (System.ComponentModel.Win32Exception)
        {
            DnsStatus = L("Diagnostics_SettingsFailed");
        }
    }

    private string ToolStatus(NetworkToolStatus status) => L(status switch
    {
        NetworkToolStatus.Success => "Diagnostics_StatusReached",
        NetworkToolStatus.Responded => "Diagnostics_StatusResponded",
        NetworkToolStatus.NoReply => "Diagnostics_StatusNoReply",
        NetworkToolStatus.TimedOut => "Diagnostics_StatusTimedOut",
        NetworkToolStatus.PermissionDenied => "Diagnostics_StatusPermission",
        NetworkToolStatus.Unsupported => "Diagnostics_StatusUnsupported",
        _ => "Diagnostics_StatusFailed",
    });
}
