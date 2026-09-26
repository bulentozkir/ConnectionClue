namespace ConnectionClue.Analysis;

/// <summary>
/// Verdict rules (handoff §14). R06 (one of two independent operators affected) needs a second independent test service
/// and is not evaluated until one exists.
/// </summary>
public enum RuleId { R01 = 1, R02, R03, R04, R05, R06, R07, R08, R09, R10, R11 }

public enum FailureKind { None, Timeout, ServerError, Tls, ProxyAuth, Redirect, Unexpected, Other }

/// <summary>One name lookup or web request at a time offset; Milliseconds is set on success only.</summary>
public readonly record struct Outcome(double Seconds, double? Milliseconds, FailureKind Failure = FailureKind.None);

/// <summary>A link state change on the probed interface (cable or Wi-Fi), in seconds into the check.</summary>
public readonly record struct LinkEvent(double Seconds, bool Up);

public enum MissingEvidence { FewSamples, Tunnel, NoRouterView }

public sealed record VerdictInput(
    IReadOnlyList<Sample> Gateway, IReadOnlyList<Sample> Internet, IReadOnlyList<Outcome> Dns, IReadOnlyList<Outcome> Web,
    IReadOnlyList<LinkEvent> Links, IReadOnlyList<double> Markers, bool TunnelSuspected, HealthThresholds Limits);

/// <summary>
/// A rule's conclusion with its evidence. Start and Duration are seconds into the check (Duration null: not over when the
/// check ended). Value, Baseline and Router are delays in ms: the peak or average, the usual value, and the router's delay
/// at the time. Count is how often the rule's evidence occurred.
/// </summary>
public sealed record Verdict(RuleId Rule, double? StartSeconds = null, double? DurationSeconds = null, int Count = 1,
    double? Value = null, double? Baseline = null, double? Router = null, FailureKind Failure = FailureKind.None,
    bool BeyondRouter = false, bool AtMarker = false, IReadOnlyList<MissingEvidence>? Missing = null)
{
    /// <summary>Rules that report a problem (R02, R10 and R11 describe visibility, health or missing evidence).</summary>
    public bool IsProblem => Rule is RuleId.R01 or RuleId.R03 or RuleId.R04 or RuleId.R05 or RuleId.R06 or RuleId.R07
        or RuleId.R08 or RuleId.R09;
}

/// <summary>
/// Turns one check's evidence into ordered verdicts: marker overlap first, then rule priority
/// (R01 &gt; R03 &gt; R04 &gt; R05 &gt; R07 &gt; R08 &gt; R09 &gt; R06 &gt; R02 &gt; R10 &gt; R11), then earliest start.
/// </summary>
public static class VerdictEvaluator
{
    /// <summary>Evidence belongs to a lag mark when it starts up to 10 s before the mark or 2 s after it.</summary>
    public const double MarkerLeadSeconds = 10, MarkerLagSeconds = 2, OverlapSeconds = 5;

    /// <summary>A delay spike is at least double the usual delay and this much above it.</summary>
    public const double MinSpikeMs = 30, MinRouterSpikeMs = 20;

    /// <summary>Spikes that count as a pattern without a lag mark.</summary>
    public const int MinSpikes = 3;

    private static readonly RuleId[] Priority =
        [RuleId.R01, RuleId.R03, RuleId.R04, RuleId.R05, RuleId.R07, RuleId.R08, RuleId.R09, RuleId.R06, RuleId.R02, RuleId.R10, RuleId.R11];

    public static IReadOnlyList<Verdict> Evaluate(VerdictInput input)
    {
        var markers = input.Markers;
        var found = new List<Verdict>();
        var drops = Drops(input.Links);
        if (drops.Count > 0)
        {
            var (start, duration) = drops.MinBy(d => Rank(d.Start, markers));
            found.Add(new(RuleId.R01, start, duration, drops.Count));
        }

        var internet = StepStatistics.From(input.Internet);
        if (internet.Sent < HealthEvaluator.MinInternetSamples)
        {
            found.Add(new(RuleId.R11, Missing: [MissingEvidence.FewSamples]));
            return Order(found, markers);
        }
        var gateway = StepStatistics.From(input.Gateway);

        // Outages: consecutive unanswered internet checks. A link drop explains the outage it causes.
        var outages = internet.Runs(HealthEvaluator.OutageRun(internet.TypicalSpacingSeconds))
            .Where(o => !drops.Any(d => Overlaps(o.StartSeconds, o.EndSeconds, d.Start, d.Start + (d.Duration ?? double.PositiveInfinity))))
            .ToList();
        var local = new List<FailureRun>();
        var external = new List<(FailureRun Run, bool Beyond)>();
        foreach (var o in outages)
        {
            bool routerFailed = gateway.Runs(HealthEvaluator.LocalRun).Any(r => Overlaps(r.StartSeconds, r.EndSeconds, o.StartSeconds, o.EndSeconds));
            if (routerFailed && input.Gateway.Any(s => s.Answered && s.Seconds < o.StartSeconds)) local.Add(o);
            else external.Add((o, !routerFailed && input.Gateway.Any(s => s.Answered && s.Seconds >= o.StartSeconds && s.Seconds <= o.EndSeconds)));
        }
        double? Lasted(FailureRun o) => input.Internet.Any(s => s.Answered && s.Seconds > o.EndSeconds - 0.001) ? o.DurationSeconds : null;
        if (local.Count > 0)
        {
            var o = local.MinBy(r => Rank(r.StartSeconds, markers))!;
            found.Add(new(RuleId.R03, o.StartSeconds, Lasted(o), local.Count));
        }
        if (external.Count > 0)
        {
            var o = external.MinBy(r => Rank(r.Run.StartSeconds, markers)).Run;
            found.Add(new(RuleId.R04, o.StartSeconds, Lasted(o), external.Count, BeyondRouter: external.All(e => e.Beyond)));
        }

        bool InOutage(double t) => outages.Any(o => t >= o.StartSeconds - OverlapSeconds && t <= o.EndSeconds + OverlapSeconds);
        bool DirectWorked(double t) => input.Internet.Any(s => s.Answered && Math.Abs(s.Seconds - t) <= OverlapSeconds);

        // R05: name lookups failed while direct-IP connections kept working.
        var dnsFailed = input.Dns.Where(d => d.Failure is FailureKind.Timeout or FailureKind.ServerError
            && !InOutage(d.Seconds) && DirectWorked(d.Seconds)).ToList();
        if (dnsFailed.Count >= 2)
        {
            bool timeouts = 2 * dnsFailed.Count(d => d.Failure == FailureKind.Timeout) >= dnsFailed.Count;
            found.Add(new(RuleId.R05, dnsFailed.MinBy(d => Rank(d.Seconds, markers)).Seconds, Count: dnsFailed.Count,
                Failure: timeouts ? FailureKind.Timeout : FailureKind.ServerError));
        }

        // R07: the secure web check reached the service but failed validation.
        var webFailed = input.Web.Where(w => w.Failure is FailureKind.Tls or FailureKind.ProxyAuth or FailureKind.Redirect
            or FailureKind.Unexpected && DirectWorked(w.Seconds)).ToList();
        if (webFailed.Count > 0)
        {
            var first = webFailed.MinBy(w => Rank(w.Seconds, markers));
            found.Add(new(RuleId.R07, first.Seconds, Count: webFailed.Count, Failure: first.Failure));
        }

        AddDelayVerdict(input, internet, gateway, found);

        if (gateway.Sent > 0 && gateway.Answered == 0 && internet.Answered > 0
            && !found.Any(v => v.Missing?.Contains(MissingEvidence.NoRouterView) == true)) found.Add(new(RuleId.R02));

        bool Failed(Outcome o) => o.Failure != FailureKind.None;
        bool withinLimits = internet.LossPercent <= input.Limits.MaxLossPercent
            && (internet.MedianMs ?? 0) <= input.Limits.MaxDelayMs
            && (internet.VariationMs ?? 0) <= input.Limits.MaxVariationMs
            && !input.Dns.Any(Failed) && !input.Web.Any(Failed);
        if (withinLimits && !found.Any(v => v.IsProblem || v.Rule == RuleId.R11)) found.Add(new(RuleId.R10, AtMarker: markers.Count > 0));
        return Order(found, markers);
    }

    /// <summary>R08/R09: where delay starts. Spikes count when they repeat, match a lag mark, or the delay stays high.</summary>
    private static void AddDelayVerdict(VerdictInput input, StepStatistics internet, StepStatistics gateway, List<Verdict> found)
    {
        if (internet.MedianMs is not { } usual || internet.Answered < HealthEvaluator.MinDelaySamples) return;
        var markers = input.Markers;
        double spikeAt = usual + Math.Max(MinSpikeMs, usual);
        var spikes = input.Internet.Where(s => s.Milliseconds > spikeAt).ToList();
        bool sustained = usual > input.Limits.MaxDelayMs;
        bool jittery = internet.VariationMs > input.Limits.MaxVariationMs;
        bool pattern = spikes.Count >= MinSpikes || spikes.Any(s => NearMarker(s.Seconds, null, markers)) || (jittery && spikes.Count > 0);
        if (!sustained && !pattern) return;
        if (input.TunnelSuspected)
        {
            found.Add(new(RuleId.R11, Missing: [MissingEvidence.Tunnel]));
            return;
        }
        // Without router replies the delay can't be placed; say so rather than let it read as healthy.
        var unlocated = new Verdict(RuleId.R11, Missing: [MissingEvidence.NoRouterView]);
        if (gateway.MedianMs is not { } routerUsual)
        {
            found.Add(unlocated);
            return;
        }

        if (!pattern)
        {
            // High all check long: the router explains it when it shows at least half of it (as HealthEvaluator does).
            found.Add(routerUsual >= 0.5 * usual
                ? new(RuleId.R08, Value: routerUsual)
                : new(RuleId.R09, Value: usual, Router: routerUsual, BeyondRouter: true));
            return;
        }

        double routerSpikeAt = routerUsual + Math.Max(MinRouterSpikeMs, routerUsual);
        double? RouterPeak(double t) => input.Gateway.Where(g => g.Answered && Math.Abs(g.Seconds - t) <= OverlapSeconds)
            .Select(g => (double?)g.Milliseconds!.Value).Max();
        var shared = spikes.Where(s => RouterPeak(s.Seconds) > routerSpikeAt).ToList();
        if (shared.Count > 0)
        {
            var first = shared.MinBy(s => Rank(s.Seconds, markers));
            found.Add(new(RuleId.R08, first.Seconds, Count: shared.Count, Value: RouterPeak(first.Seconds), Baseline: routerUsual));
        }
        else if (spikes.Any(s => RouterPeak(s.Seconds) is not null))
        {
            var first = spikes.MinBy(s => Rank(s.Seconds, markers));
            found.Add(new(RuleId.R09, first.Seconds, Count: spikes.Count, Value: first.Milliseconds, Baseline: usual,
                Router: routerUsual, BeyondRouter: true));
        }
        else
        {
            found.Add(unlocated);
        }
    }

    private static List<(double Start, double? Duration)> Drops(IReadOnlyList<LinkEvent> links)
    {
        var drops = new List<(double, double?)>();
        double? down = null;
        foreach (var e in links.OrderBy(e => e.Seconds))
        {
            if (!e.Up) down ??= e.Seconds;
            else if (down is { } start)
            {
                drops.Add((start, e.Seconds - start));
                down = null;
            }
        }
        if (down is { } open) drops.Add((open, null));
        return drops;
    }

    private static IReadOnlyList<Verdict> Order(List<Verdict> found, IReadOnlyList<double> markers) =>
        [.. found.Select(v => v with { AtMarker = v.AtMarker || v.StartSeconds is { } s && NearMarker(s, v.DurationSeconds, markers) })
            .OrderByDescending(v => v.AtMarker)
            .ThenBy(v => Array.IndexOf(Priority, v.Rule))
            .ThenBy(v => v.StartSeconds ?? double.MaxValue)
            .ThenBy(v => v.Rule)];

    private static bool NearMarker(double start, double? duration, IReadOnlyList<double> markers) =>
        markers.Any(m => start <= m + MarkerLagSeconds && start + (duration ?? 0) >= m - MarkerLeadSeconds);

    /// <summary>Sort key that prefers evidence at a lag mark, then the earliest.</summary>
    private static (int, double) Rank(double seconds, IReadOnlyList<double> markers) =>
        (NearMarker(seconds, null, markers) ? 0 : 1, seconds);

    private static bool Overlaps(double aStart, double aEnd, double bStart, double bEnd) =>
        aStart <= bEnd + OverlapSeconds && bStart <= aEnd + OverlapSeconds;
}
