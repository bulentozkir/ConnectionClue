namespace ConnectionClue.Analysis.Tests;

public class VerdictTests
{
    private static Sample[] Series(int count, Func<int, double?> ms) =>
        [.. Enumerable.Range(0, count).Select(i => new Sample(i, ms(i)))];

    private static readonly Sample[] Router = Series(30, _ => 3);
    private static readonly Sample[] Internet = Series(30, _ => 20);
    private static readonly Outcome[] Dns = [.. Enumerable.Range(0, 5).Select(i => new Outcome(i * 6, 15))];
    private static readonly Outcome[] Web = [.. Enumerable.Range(0, 5).Select(i => new Outcome(i * 6 + 3, 80))];

    private static IReadOnlyList<Verdict> Eval(Sample[]? router = null, Sample[]? internet = null, Outcome[]? dns = null,
        Outcome[]? web = null, LinkEvent[]? links = null, double[]? markers = null, bool tunnel = false) =>
        VerdictEvaluator.Evaluate(new VerdictInput(router ?? Router, internet ?? Internet, dns ?? Dns, web ?? Web,
            links ?? [], markers ?? [], tunnel, new HealthThresholds()));

    [Fact]
    public void Healthy_check_is_R10_and_notes_marks()
    {
        Assert.Equal(RuleId.R10, Assert.Single(Eval()).Rule);
        Assert.True(Assert.Single(Eval(markers: [12])).AtMarker);
    }

    [Fact]
    public void Link_drop_is_R01_with_time_and_duration_and_explains_its_outage()
    {
        var verdicts = Eval(internet: Series(30, i => i is >= 10 and < 18 ? null : 20),
            links: [new(9.5, false), new(17.5, true)]);
        var drop = verdicts[0];
        Assert.Equal((RuleId.R01, 9.5, 8.0), (drop.Rule, drop.StartSeconds!.Value, drop.DurationSeconds!.Value));
        Assert.DoesNotContain(verdicts, v => v.Rule is RuleId.R03 or RuleId.R04);
        Assert.Null(Eval(links: [new(25, false)])[0].DurationSeconds); // still down when the check ended
    }

    [Fact]
    public void Outage_while_router_answers_is_beyond_router()
    {
        var v = Eval(internet: Series(30, i => i is >= 10 and < 16 ? null : 20))[0];
        Assert.True(v is { Rule: RuleId.R04, BeyondRouter: true, StartSeconds: 10 });
        Assert.Equal(6, v.DurationSeconds);
    }

    [Fact]
    public void Router_failing_with_internet_after_answering_is_R03()
    {
        var v = Eval(router: Series(30, i => i is >= 10 and < 16 ? null : 3), internet: Series(30, i => i is >= 10 and < 16 ? null : 20))[0];
        Assert.Equal((RuleId.R03, 10.0), (v.Rule, v.StartSeconds!.Value));
    }

    [Fact]
    public void Dns_failures_while_direct_connections_work_are_R05_with_kind()
    {
        var dns = new Outcome[] { new(0, 15), new(6, null, FailureKind.Timeout), new(12, null, FailureKind.Timeout), new(18, 15) };
        var v = Assert.Single(Eval(dns: dns), x => x.Rule == RuleId.R05);
        Assert.Equal((6.0, FailureKind.Timeout, 2), (v.StartSeconds!.Value, v.Failure, v.Count));
        Assert.DoesNotContain(Eval(dns: [new(6, null, FailureKind.ServerError)]), x => x.Rule == RuleId.R05); // one failure is not a pattern
    }

    [Fact]
    public void Web_validation_failure_is_R07_with_category()
    {
        var v = Assert.Single(Eval(web: [new(3, 80), new(9, null, FailureKind.Redirect)]), x => x.Rule == RuleId.R07);
        Assert.Equal((9.0, FailureKind.Redirect), (v.StartSeconds!.Value, v.Failure));
    }

    [Fact]
    public void Spikes_shared_with_router_are_R08_and_internet_only_spikes_are_R09()
    {
        var spiky = Series(30, i => i is 8 or 15 or 22 ? 150 : 20);
        var local = Eval(router: Series(30, i => i is 8 or 15 or 22 ? 90 : 3), internet: spiky)[0];
        Assert.True(local is { Rule: RuleId.R08, StartSeconds: 8, Value: 90, Baseline: 3, Count: 3 });

        var upstream = Eval(internet: spiky)[0];
        Assert.True(upstream is { Rule: RuleId.R09, StartSeconds: 8, Value: 150, Baseline: 20, Router: 3, BeyondRouter: true });
    }

    [Fact]
    public void One_spike_counts_only_at_a_lag_mark()
    {
        var single = Series(30, i => i == 20 ? 200 : 20);
        Assert.Equal(RuleId.R10, Eval(internet: single)[0].Rule);
        var marked = Eval(internet: single, markers: [21])[0];
        Assert.True(marked is { Rule: RuleId.R09, AtMarker: true, StartSeconds: 20 });
    }

    [Fact]
    public void Sustained_delay_is_localized_by_medians()
    {
        Assert.Equal(RuleId.R09, Eval(internet: Series(30, _ => 180))[0].Rule);
        Assert.True(Eval(router: Series(30, _ => 120), internet: Series(30, _ => 180))[0] is { Rule: RuleId.R08, Value: 120 });
    }

    [Fact]
    public void Tunnel_turns_delay_localization_into_R11()
    {
        var v = Eval(internet: Series(30, i => i % 7 == 3 ? 150 : 20), tunnel: true);
        Assert.Contains(v, x => x.Rule == RuleId.R11 && x.Missing!.Contains(MissingEvidence.Tunnel));
        Assert.DoesNotContain(v, x => x.Rule is RuleId.R08 or RuleId.R09);
    }

    [Fact]
    public void Too_few_samples_is_R11_and_silent_router_is_R02()
    {
        Assert.Equal(MissingEvidence.FewSamples, Assert.Single(Eval(internet: Series(3, _ => 20))).Missing!.Single());
        Assert.Equal([RuleId.R02, RuleId.R10], Eval(router: Series(30, _ => null)).Select(v => v.Rule));
    }

    [Fact]
    public void Delay_without_a_router_view_cannot_conclude_instead_of_reading_as_healthy()
    {
        var spike = Series(30, i => i == 20 ? 250 : 20);
        var silent = Eval(router: [], internet: spike, markers: [21]);
        Assert.True(Assert.Single(silent) is { Rule: RuleId.R11 } v && v.Missing!.Single() == MissingEvidence.NoRouterView);
        // The router answered earlier but not around the spike.
        var gap = Eval(router: Series(30, i => i is >= 15 and <= 25 ? null : 3), internet: spike, markers: [21]);
        Assert.DoesNotContain(gap, v => v.Rule == RuleId.R10);
        Assert.Contains(gap, v => v.Missing?.Contains(MissingEvidence.NoRouterView) == true);
    }

    [Fact]
    public void Any_failed_lookup_or_web_request_rules_out_no_problem_observed()
    {
        var v = Eval(dns: [new(6, null, FailureKind.Timeout)], web: [new(9, null, FailureKind.Other)]);
        Assert.DoesNotContain(v, x => x.Rule == RuleId.R10);
    }

    [Fact]
    public void Evidence_at_a_lag_mark_comes_first_then_priority()
    {
        var dns = new Outcome[] { new(2, null, FailureKind.Timeout), new(4, null, FailureKind.Timeout) };
        var verdicts = Eval(internet: Series(30, i => i == 25 ? 200 : 20), dns: dns, markers: [26]);
        Assert.Equal([RuleId.R09, RuleId.R05], verdicts.Select(v => v.Rule));
        Assert.Equal([RuleId.R05, RuleId.R09], Eval(internet: Series(30, i => i is 8 or 15 or 25 ? 200 : 20), dns: dns).Select(v => v.Rule));
    }
}
