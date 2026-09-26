using static ConnectionClue.Analysis.ActionCode;

namespace ConnectionClue.Analysis.Tests;

public class NextActionPlannerTests
{
    private static HealthReport Report(params (IssueKind Kind, HealthLevel Severity, IssueLocation Where)[] issues) =>
        new(issues.Length == 0 ? HealthLevel.NoIssue : issues.Max(i => i.Severity),
            [.. issues.Select(i => new HealthIssue(i.Kind, i.Severity, i.Where, 1))]);

    [Fact]
    public void No_issues_no_actions() =>
        Assert.Empty(NextActionPlanner.Plan(Report(), new()));

    [Fact]
    public void Local_delay_on_wifi_suggests_wifi_fixes_first() =>
        Assert.Equal([ImproveWifiPlacement, RetestOnEthernet, PauseHouseholdUploads],
            NextActionPlanner.Plan(Report((IssueKind.Delay, HealthLevel.Degraded, IssueLocation.LocalNetwork)), new(ConnectionMedium.WiFi)));

    [Fact]
    public void Local_interruption_on_ethernet_checks_the_cable() =>
        Assert.Equal([CheckCable, RestartRouter],
            NextActionPlanner.Plan(Report((IssueKind.Interrupted, HealthLevel.Unhealthy, IssueLocation.LocalNetwork)), new(ConnectionMedium.Ethernet)));

    [Fact]
    public void Upstream_delay_suggests_household_load_then_router_then_provider() =>
        Assert.Equal([PauseHouseholdUploads, RestartRouter, ContactIsp],
            NextActionPlanner.Plan(Report((IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter)), new(ConnectionMedium.WiFi)));

    [Fact]
    public void Severe_issue_leads_and_actions_are_not_repeated() =>
        Assert.Equal([RestartRouter, ContactIsp, PauseHouseholdUploads],
            NextActionPlanner.Plan(Report(
                (IssueKind.Delay, HealthLevel.Degraded, IssueLocation.BeyondRouter),
                (IssueKind.Interrupted, HealthLevel.Unhealthy, IssueLocation.BeyondRouter)), new()));

    [Fact]
    public void Suspected_tunnel_adds_vpn_step() =>
        Assert.Equal([RestartRouter, DisconnectVpn, ContactIsp],
            NextActionPlanner.Plan(Report((IssueKind.WebUnreachable, HealthLevel.Unhealthy, IssueLocation.Unknown)), new(TunnelSuspected: true)));

    [Fact]
    public void Fast_streams_need_three_misses_for_an_outage()
    {
        Sample[] Series(double spacing, params int[] missing) =>
            [.. Enumerable.Range(0, 30).Select(i => new Sample(i * spacing, missing.Contains(i) ? null : 40))];
        var router = Enumerable.Range(0, 30).Select(i => new Sample(i, 4)).ToArray();
        var web = Enumerable.Range(0, 6).Select(i => new Sample(i * 5, 150)).ToArray();
        var limits = new HealthThresholds(MaxLossPercent: 50);

        Assert.DoesNotContain(HealthEvaluator.Evaluate(router, Series(1, 10, 11), web, limits).Issues, x => x.Kind == IssueKind.Interrupted);
        Assert.Contains(HealthEvaluator.Evaluate(router, Series(1, 10, 11, 12), web, limits).Issues, x => x.Kind == IssueKind.Interrupted);
    }
}
