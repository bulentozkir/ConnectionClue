using ConnectionClue.Analysis;

namespace ConnectionClue.Analysis.Tests;

public class HealthTests
{
    private static readonly HealthThresholds Limits = new();

    /// <summary>Samples every <paramref name="spacing"/> s; ms(i) returns null for an unanswered check.</summary>
    private static Sample[] Series(int count, double spacing, Func<int, double?> ms) =>
        [.. Enumerable.Range(0, count).Select(i => new Sample(i * spacing, ms(i)))];

    private static readonly Sample[] Router = Series(120, 1, _ => 4);
    private static readonly Sample[] Web = Series(16, 7.5, _ => 150);
    private static Sample[] Internet(Func<int, double?> ms) => Series(24, 5, ms);

    private static HealthReport Eval(Sample[] home, Sample[] internet, Sample[]? web = null, HealthThresholds? limits = null) =>
        HealthEvaluator.Evaluate(home, internet, web ?? Web, limits ?? Limits);

    [Fact]
    public void Statistics_follow_minimum_counts()
    {
        var few = StepStatistics.From(Series(50, 1, i => i));
        Assert.Equal(24.5, few.MedianMs);
        Assert.Null(few.P95Ms);
        Assert.Equal(1, few.VariationMs);

        var many = StepStatistics.From(Series(100, 1, i => i + 1));
        Assert.Equal(95, many.P95Ms);

        var gappy = StepStatistics.From(Series(30, 1, i => i % 2 == 0 ? 10 : null));
        Assert.Null(gappy.VariationMs);
        Assert.Equal(50, gappy.LossPercent);
    }

    [Fact]
    public void Failure_runs_include_one_spacing()
    {
        var s = StepStatistics.From(Series(10, 5, i => i is 3 or 4 or 5 ? null : 30));
        var run = Assert.Single(s.FailureRuns);
        Assert.Equal((15, 30, 3), (run.StartSeconds, run.EndSeconds, run.Count));
    }

    [Fact]
    public void Healthy_check_has_no_issue() =>
        Assert.Equal(HealthLevel.NoIssue, Eval(Router, Internet(_ => 40)).Level);

    [Fact]
    public void Too_few_internet_samples_is_inconclusive() =>
        Assert.Equal(HealthLevel.Inconclusive, Eval(Router, Series(5, 5, _ => 40)).Level);

    [Fact]
    public void Internet_outage_with_responsive_router_is_beyond_router()
    {
        var r = Eval(Router, Internet(i => i is 10 or 11 or 12 ? null : 40));
        var issue = Assert.Single(r.Issues);
        Assert.Equal((HealthLevel.Unhealthy, IssueKind.Interrupted, IssueLocation.BeyondRouter), (r.Level, issue.Kind, issue.Location));
        Assert.Equal(15, issue.Value);
    }

    [Fact]
    public void Outage_with_router_failing_too_is_local()
    {
        var home = Series(120, 1, i => i is >= 50 and < 60 ? null : 4);
        Assert.Equal(IssueLocation.LocalNetwork, Assert.Single(Eval(home, Internet(i => i is 10 or 11 ? null : 40)).Issues).Location);
    }

    [Fact]
    public void Silent_router_cannot_localize() =>
        Assert.Equal(IssueLocation.Unknown,
            Assert.Single(Eval(Series(120, 1, _ => null), Internet(i => i is 10 or 11 ? null : 40)).Issues).Location);

    [Fact]
    public void Router_only_silence_raises_nothing() =>
        Assert.Equal(HealthLevel.NoIssue, Eval(Series(120, 1, _ => null), Internet(_ => 40)).Level);

    [Fact]
    public void Web_failures_while_internet_works()
    {
        var r = Eval(Router, Internet(_ => 40), Series(16, 7.5, i => i is 4 or 5 ? null : 150));
        Assert.Equal(IssueKind.WebUnreachable, Assert.Single(r.Issues).Kind);
    }

    [Fact]
    public void Scattered_loss_above_limit_is_degraded()
    {
        var r = Eval(Router, Internet(i => i is 3 or 15 ? null : 40));
        var issue = Assert.Single(r.Issues);
        Assert.Equal((HealthLevel.Degraded, IssueKind.Loss, IssueLocation.BeyondRouter), (r.Level, issue.Kind, issue.Location));
        Assert.Equal(HealthLevel.NoIssue, Eval(Router, Internet(i => i is 3 or 15 ? null : 40), limits: new(MaxLossPercent: 10)).Level);
    }

    [Theory]
    [InlineData(4, IssueLocation.BeyondRouter)]
    [InlineData(120, IssueLocation.LocalNetwork)]
    public void Delay_above_limit_is_localized(double routerMs, IssueLocation expected)
    {
        var issue = Assert.Single(Eval(Series(120, 1, _ => routerMs), Internet(_ => 180)).Issues);
        Assert.Equal((IssueKind.Delay, 180.0, 100.0, expected), (issue.Kind, issue.Value, issue.Limit, issue.Location));
    }

    [Fact]
    public void Variation_above_limit_is_degraded()
    {
        var issue = Assert.Single(Eval(Router, Internet(i => i % 2 == 0 ? 20 : 80)).Issues);
        Assert.Equal((IssueKind.Variation, 60.0), (issue.Kind, issue.Value));
    }
}
