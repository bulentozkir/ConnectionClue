namespace ConnectionClue.Analysis;

/// <summary>User-performed remediation from the handoff action catalog (§12). The app never performs these itself.</summary>
public enum ActionCode
{
    ImproveWifiPlacement, RetestOnEthernet, CheckCable, RestartRouter, PauseHouseholdUploads, DisconnectVpn, ContactIsp,
}

public enum ConnectionMedium { Unknown, WiFi, Ethernet }

/// <summary>Path facts at check time that decide which actions make sense. Gateway is the router's IPv4 address, for "Open router page".</summary>
public sealed record CheckContext(ConnectionMedium Medium = ConnectionMedium.Unknown, bool TunnelSuspected = false, bool Metered = false,
    string? Gateway = null);

/// <summary>Maps issues to an ordered, de-duplicated list of actions: the most severe issue first, local fixes before upstream ones.</summary>
public static class NextActionPlanner
{
    public static IReadOnlyList<ActionCode> Plan(HealthReport report, CheckContext context)
    {
        var actions = new List<ActionCode>();
        foreach (var issue in report.Issues.OrderByDescending(i => i.Severity))
        {
            bool local = issue.Location == IssueLocation.LocalNetwork;
            switch (issue.Kind)
            {
                case IssueKind.Interrupted or IssueKind.Loss when local:
                    LocalLink();
                    Add(ActionCode.RestartRouter);
                    break;
                case IssueKind.Interrupted or IssueKind.Loss:
                    Add(ActionCode.RestartRouter, ActionCode.ContactIsp);
                    break;
                case IssueKind.WebUnreachable:
                    Add(ActionCode.RestartRouter);
                    if (context.TunnelSuspected) Add(ActionCode.DisconnectVpn);
                    Add(ActionCode.ContactIsp);
                    break;
                case IssueKind.Delay or IssueKind.Variation when local:
                    LocalLink();
                    Add(ActionCode.PauseHouseholdUploads);
                    break;
                default: // delay or variation beyond the router, or not localizable
                    Add(ActionCode.PauseHouseholdUploads, ActionCode.RestartRouter, ActionCode.ContactIsp);
                    break;
            }
        }
        if (context.TunnelSuspected && actions.Count > 0) Add(ActionCode.DisconnectVpn);
        return actions;

        void Add(params ActionCode[] codes)
        {
            foreach (var c in codes)
                if (!actions.Contains(c)) actions.Add(c);
        }

        void LocalLink()
        {
            switch (context.Medium)
            {
                case ConnectionMedium.WiFi: Add(ActionCode.ImproveWifiPlacement, ActionCode.RetestOnEthernet); break;
                case ConnectionMedium.Ethernet: Add(ActionCode.CheckCable); break;
                default: Add(ActionCode.ImproveWifiPlacement, ActionCode.CheckCable); break;
            }
        }
    }
}
