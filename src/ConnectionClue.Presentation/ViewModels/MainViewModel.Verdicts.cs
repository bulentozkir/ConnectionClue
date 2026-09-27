using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;

namespace ConnectionClue.Presentation.ViewModels;

/// <summary>Verdicts (R01–R11): the evidence a check gathers beyond its lanes, and the sentences that explain it.</summary>
public sealed partial class MainViewModel
{
    private readonly Lock _outcomeGate = new();
    private readonly List<Outcome> _dnsOutcomes = [], _webOutcomes = [];
    private IReadOnlyList<LinkEvent> _links = [];
    private DateTimeOffset _samplesStartUtc;
    private IReadOnlyList<Verdict>? _lastVerdicts;
    private DateTimeOffset? _lastVerdictsStartUtc;
    private bool _checkStopped; // the user pressed Stop: nothing more runs after the check

    private void ResetEvidence()
    {
        lock (_outcomeGate)
        {
            _dnsOutcomes.Clear();
            _webOutcomes.Clear();
        }
        _links = [];
    }

    /// <summary>Keeps name-lookup and web outcomes apart, so R05 (DNS) and R07 (web validation) can be told apart.</summary>
    private void RecordOutcome(ProbeObservation observation, double seconds)
    {
        var kind = observation.Request.Stream.Kind;
        if (kind is not (ProbeKind.SystemDns or ProbeKind.Https)) return;
        FailureKind? failure = observation.Status switch
        {
            ProbeStatus.Success => FailureKind.None,
            ProbeStatus.Timeout => FailureKind.Timeout,
            ProbeStatus.DnsFailure when kind == ProbeKind.SystemDns => FailureKind.ServerError,
            ProbeStatus.TlsFailure => FailureKind.Tls,
            ProbeStatus.ProxyAuthRequired => FailureKind.ProxyAuth,
            ProbeStatus.HttpUnexpected when observation.ErrorCode == "Redirect" => FailureKind.Redirect,
            ProbeStatus.HttpUnexpected => FailureKind.Unexpected,
            // These say nothing about the network.
            ProbeStatus.Cancelled or ProbeStatus.Skipped or ProbeStatus.PermissionDenied or ProbeStatus.Unsupported
                or ProbeStatus.InternalError => null,
            _ => FailureKind.Other,
        };
        if (failure is not { } f) return;
        var outcome = new Outcome(seconds, f == FailureKind.None && observation.DurationUs is { } us ? us / 1000.0 : null, f);
        lock (_outcomeGate) (kind == ProbeKind.SystemDns ? _dnsOutcomes : _webOutcomes).Add(outcome);
    }

    /// <summary>Maps monitor notifications (TimeProvider timestamps) to link changes in seconds into the check.</summary>
    private List<LinkEvent> LinkEvents(IReadOnlyList<MonitorEvent>? events)
    {
        if (events is null) return [];
        var links = new List<LinkEvent>();
        foreach (var e in events)
        {
            bool? up = e.Kind switch
            {
                ConnectionEventKind.LinkUp => true,
                // Windows also reports a failed connection attempt as "connection complete", with a non-zero reason code.
                ConnectionEventKind.WlanConnected => e.ReasonCode is null or 0 ? true : null,
                ConnectionEventKind.LinkDown or ConnectionEventKind.WlanDisconnected => false,
                _ => null,
            };
            double seconds = _time.GetElapsedTime(_start, e.Timestamp).TotalSeconds;
            if (up is { } isUp && seconds >= 0) links.Add(new(seconds, isUp));
        }
        return links;
    }

    private IReadOnlyList<Verdict> EvaluateVerdicts(Func<LaneViewModel, Sample[]> idle)
    {
        Outcome[] dns, web;
        lock (_outcomeGate)
            (dns, web) = ([.. _dnsOutcomes.Where(o => o.Seconds < _idleSeconds)], [.. _webOutcomes.Where(o => o.Seconds < _idleSeconds)]);
        // Link drops count for the whole check, including a speed phase: a drop is evidence whenever it happens.
        return VerdictEvaluator.Evaluate(new VerdictInput(idle(Lanes[0]), idle(Lanes[1]), dns, web, _links, Markers,
            _context.TunnelSuspected, Settings.Thresholds));
    }

    /// <summary>The status line after a check: the leading problem verdict, else the measured issues, else the health verdict.</summary>
    private string LeadSentence(HealthReport report, IReadOnlyList<Verdict> verdicts)
    {
        var sentences = VerdictSentences(verdicts, _samplesStartUtc, _context.Medium);
        string? Of(Func<Verdict, bool> match)
        {
            for (int i = 0; i < verdicts.Count; i++)
                if (match(verdicts[i])) return sentences[i];
            return null;
        }
        if (Of(v => v.IsProblem) is { } problem) return problem;
        string gap = _ui.TwoLetterISOLanguageName is "zh" or "ja" ? "" : " ";
        string? missing = Of(v => v.Rule == RuleId.R11);
        if (report.Issues.Count > 0) return missing is null ? Describe(report) : Describe(report) + gap + missing;
        // "Everything is responding so far" describes a check in progress; a finished check without a verdict adds nothing
        // to its headline.
        return Of(v => v.Rule == RuleId.R10) ?? missing ?? "";
    }

    /// <summary>One plain sentence per verdict, in verdict order, with local clock times (what a provider compares with its logs).</summary>
    private IReadOnlyList<string> VerdictSentences(IReadOnlyList<Verdict>? verdicts, DateTimeOffset? startedUtc, ConnectionMedium medium)
    {
        if (verdicts is null || verdicts.Count == 0) return [];
        var f = CultureInfo.CurrentCulture;
        string L(string key) => _l.Get(key, _ui);
        string F(string key, params object?[] args) => string.Format(_ui, L(key), args);
        string At(double? seconds) => startedUtc is { } start
            ? TimeZoneInfo.ConvertTime(start.AddSeconds(seconds ?? 0), _time.LocalTimeZone).ToString("T", f)
            : string.Format(f, "{0:0} s", seconds ?? 0);
        string Ms(double? v) => v is { } x ? string.Format(f, "{0:0} ms", x) : "—";
        string Down(double? duration) => duration is { } d
            ? F("Verdict_DownFor", d < 90 ? string.Format(f, "{0:0} s", Math.Max(1, d)) : string.Format(f, "{0:0.#} min", d / 60))
            : L("Verdict_DownUntilEnd");

        return [.. verdicts.Select(v =>
        {
            string text = v.Rule switch
            {
                RuleId.R01 => F("Verdict_R01", L(medium switch
                {
                    ConnectionMedium.WiFi => "Verdict_LinkWifi",
                    ConnectionMedium.Ethernet => "Verdict_LinkCable",
                    _ => "Verdict_LinkOther",
                }), At(v.StartSeconds), Down(v.DurationSeconds)),
                RuleId.R02 => L("Verdict_R02"),
                RuleId.R03 => F("Verdict_R03", At(v.StartSeconds), Down(v.DurationSeconds)),
                RuleId.R04 => F(v.BeyondRouter ? "Verdict_R04Beyond" : "Verdict_R04", At(v.StartSeconds), Down(v.DurationSeconds)),
                RuleId.R05 => F("Verdict_R05", At(v.StartSeconds), L(v.Failure == FailureKind.Timeout ? "Verdict_DnsTimeout" : "Verdict_DnsError")),
                RuleId.R07 => F("Verdict_R07", At(v.StartSeconds), L(v.Failure switch
                {
                    FailureKind.Tls => "Verdict_WebTls",
                    FailureKind.ProxyAuth => "Verdict_WebProxy",
                    FailureKind.Redirect => "Verdict_WebRedirect",
                    _ => "Verdict_WebUnexpected",
                })),
                RuleId.R08 => (v.StartSeconds is null ? F("Verdict_R08Sustained", Ms(v.Value)) : F("Verdict_R08", At(v.StartSeconds), Ms(v.Value), Ms(v.Baseline)))
                    + (medium == ConnectionMedium.WiFi ? " " + L("Verdict_WifiContext") : ""),
                RuleId.R09 => (v.StartSeconds is null ? F("Verdict_R09Sustained", Ms(v.Value), Ms(v.Router))
                    : F("Verdict_R09", At(v.StartSeconds), Ms(v.Value), Ms(v.Baseline), Ms(v.Router))) + " " + L("Verdict_UploadContext"),
                RuleId.R10 => L(v.AtMarker ? "Verdict_R10Marks" : "Verdict_R10"),
                RuleId.R11 => F("Verdict_R11",
                    JoinList((v.Missing ?? []).Select(m => L($"Verdict_Missing{m}"))),
                    JoinList((v.Missing ?? []).Select(m => L($"Verdict_Next{m}")))),
                _ => "",
            };
            if (v.Count > 1 && v.StartSeconds is not null) text += " " + F("Verdict_Repeated", v.Count);
            return v.AtMarker && v.IsProblem ? F("Verdict_AtMark", text) : text;
        })];
    }

    /// <summary>The headline measurements for the report; nothing when the check measured none.</summary>
    private IEnumerable<string> MeasurementsLine()
    {
        if (Metrics.All(m => m.Value == "—")) yield break;
        yield return string.Format(_ui, _l.Get("SupportReport_Measurements", _ui), Download.Value, Upload.Value, Latency.Value,
            Variation.Value, BufferbloatText);
    }
}
