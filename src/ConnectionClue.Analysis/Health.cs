namespace ConnectionClue.Analysis;

/// <summary>One check result at a time offset; Milliseconds is null when the check got no answer.</summary>
public readonly record struct Sample(double Seconds, double? Milliseconds)
{
    public bool Answered => Milliseconds is not null;
}

/// <summary>Consecutive unanswered checks. End includes one typical spacing: the outage lasted at least that long.</summary>
public sealed record FailureRun(double StartSeconds, double EndSeconds, int Count)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public sealed record StepStatistics(
    int Sent, int Failed, double? MedianMs, double? P95Ms, double? VariationMs, IReadOnlyList<FailureRun> FailureRuns,
    double TypicalSpacingSeconds)
{
    public const int MinP95Samples = 100, MinVariationPairs = 20;

    public int Answered => Sent - Failed;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Failed / Sent;

    public IEnumerable<FailureRun> Runs(int minCount) => FailureRuns.Where(r => r.Count >= minCount);

    public static StepStatistics From(IReadOnlyList<Sample> samples)
    {
        var ordered = samples.OrderBy(s => s.Seconds).ToArray();
        var values = ordered.Where(s => s.Answered).Select(s => s.Milliseconds!.Value).Order().ToArray();
        double spacing = TypicalSpacing(ordered);

        var deltas = new List<double>();
        for (int i = 1; i < ordered.Length; i++)
        {
            var (a, b) = (ordered[i - 1], ordered[i]);
            if (a.Answered && b.Answered && b.Seconds - a.Seconds <= 2 * spacing)
                deltas.Add(Math.Abs(b.Milliseconds!.Value - a.Milliseconds!.Value));
        }
        deltas.Sort();

        var runs = new List<FailureRun>();
        int count = 0;
        double start = 0, last = 0;
        foreach (var s in ordered)
        {
            if (!s.Answered)
            {
                if (count++ == 0) start = s.Seconds;
                last = s.Seconds;
            }
            else if (count > 0)
            {
                runs.Add(new FailureRun(start, last + spacing, count));
                count = 0;
            }
        }
        if (count > 0) runs.Add(new FailureRun(start, last + spacing, count));

        return new StepStatistics(
            ordered.Length,
            ordered.Length - values.Length,
            values.Length == 0 ? null : Median(values),
            values.Length >= MinP95Samples ? NearestRank(values, 0.95) : null,
            deltas.Count >= MinVariationPairs ? Median(deltas) : null,
            runs,
            spacing);
    }

    /// <summary>Nearest-rank percentile of ascending values: index ⌈q·n⌉−1.</summary>
    public static double NearestRank(IReadOnlyList<double> sorted, double q) =>
        sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static double TypicalSpacing(Sample[] ordered)
    {
        if (ordered.Length < 2) return 1;
        var d = new double[ordered.Length - 1];
        for (int i = 1; i < ordered.Length; i++) d[i - 1] = ordered[i].Seconds - ordered[i - 1].Seconds;
        Array.Sort(d);
        return Math.Max(Median(d), 0.001);
    }
}

/// <summary>The user's own limits (defaults are starting points, not verdicts about the network).</summary>
public sealed record HealthThresholds(double MaxDelayMs = 100, double MaxLossPercent = 2, double MaxVariationMs = 30);

public enum HealthLevel { NoIssue, Inconclusive, Degraded, Unhealthy }

public enum IssueKind { Interrupted, WebUnreachable, Loss, Delay, Variation }

public enum IssueLocation { Unknown, LocalNetwork, BeyondRouter }

public sealed record HealthIssue(IssueKind Kind, HealthLevel Severity, IssueLocation Location, double Value, double Limit = 0);

public sealed record HealthReport(HealthLevel Level, IReadOnlyList<HealthIssue> Issues)
{
    public static HealthReport Inconclusive { get; } = new(HealthLevel.Inconclusive, []);
}

/// <summary>
/// Evaluates one check of three path steps: home network (router ICMP), internet (TCP RTT) and web services
/// (DNS + HTTPS). Router-only symptoms never raise issues (routers deprioritize ICMP); the router is used to
/// localize internet symptoms. Thresholds are proposed defaults pending lab calibration.
/// </summary>
public static class HealthEvaluator
{
    public const int MinInternetSamples = 6, MinDelaySamples = 5, WebRun = 2, LocalRun = 3;

    /// <summary>Consecutive failures that make an outage: 3 on fast (≤2 s) streams, 2 on slower ones (handoff §12).</summary>
    public static int OutageRun(double spacingSeconds) => spacingSeconds <= 2 ? 3 : 2;
    private const double OverlapSeconds = 5;

    public static HealthReport Evaluate(
        IReadOnlyList<Sample> home, IReadOnlyList<Sample> internet, IReadOnlyList<Sample> web, HealthThresholds limits)
    {
        var i = StepStatistics.From(internet);
        if (i.Sent < MinInternetSamples) return HealthReport.Inconclusive;
        var h = StepStatistics.From(home);
        var w = StepStatistics.From(web);
        var issues = new List<HealthIssue>();

        var outages = i.Runs(OutageRun(i.TypicalSpacingSeconds)).ToList();
        if (outages.Count > 0)
        {
            issues.Add(new(IssueKind.Interrupted, HealthLevel.Unhealthy, LocateOutage(h, home, outages),
                outages.Max(r => r.DurationSeconds)));
        }
        else if (w.Runs(WebRun).Any())
        {
            issues.Add(new(IssueKind.WebUnreachable, HealthLevel.Unhealthy, IssueLocation.Unknown,
                w.Runs(WebRun).Max(r => r.DurationSeconds)));
        }

        // An interruption already explains its own unanswered checks.
        if (outages.Count == 0 && i.Failed >= 2 && i.LossPercent > limits.MaxLossPercent)
            issues.Add(new(IssueKind.Loss, HealthLevel.Degraded, Locate(h, h.LossPercent, i.LossPercent), i.LossPercent, limits.MaxLossPercent));

        if (i.Answered >= MinDelaySamples && i.MedianMs is { } delay && delay > limits.MaxDelayMs)
            issues.Add(new(IssueKind.Delay, HealthLevel.Degraded, Locate(h, h.MedianMs, delay), delay, limits.MaxDelayMs));

        if (i.VariationMs is { } variation && variation > limits.MaxVariationMs)
            issues.Add(new(IssueKind.Variation, HealthLevel.Degraded, Locate(h, h.VariationMs, variation), variation, limits.MaxVariationMs));

        return new HealthReport(issues.Count == 0 ? HealthLevel.NoIssue : issues.Max(x => x.Severity), issues);
    }

    // The home network explains most of an internet symptom when it shows at least half of it.
    private static IssueLocation Locate(StepStatistics home, double? homeValue, double internetValue) =>
        home.Answered == 0 || homeValue is null ? IssueLocation.Unknown
        : homeValue >= 0.5 * internetValue ? IssueLocation.LocalNetwork
        : IssueLocation.BeyondRouter;

    private static IssueLocation LocateOutage(StepStatistics h, IReadOnlyList<Sample> home, List<FailureRun> outages)
    {
        if (h.Answered == 0) return IssueLocation.Unknown; // router never answers ICMP: limited visibility
        if (h.Runs(LocalRun).Any(r => outages.Any(o => Overlaps(r, o)))) return IssueLocation.LocalNetwork;
        bool routerAnswered = outages.All(o =>
            home.Any(s => s.Answered && s.Seconds >= o.StartSeconds && s.Seconds <= o.EndSeconds));
        return routerAnswered ? IssueLocation.BeyondRouter : IssueLocation.Unknown;
    }

    private static bool Overlaps(FailureRun a, FailureRun b) =>
        a.StartSeconds <= b.EndSeconds + OverlapSeconds && b.StartSeconds <= a.EndSeconds + OverlapSeconds;
}
