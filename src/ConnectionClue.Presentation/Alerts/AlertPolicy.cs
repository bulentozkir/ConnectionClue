using ConnectionClue.Analysis;

namespace ConnectionClue.Presentation.Alerts;

public enum AlertKind { Problem, Slow, Recovered }

/// <summary>
/// Alerts on change, not on every check: a new or different problem, an hourly reminder while it persists,
/// and one recovery notice. Inconclusive checks never alert and never clear an active problem.
/// </summary>
public sealed class AlertPolicy(TimeProvider time)
{
    public static readonly TimeSpan Reminder = TimeSpan.FromHours(1);
    private string? _active;
    private long _alertedAt;

    public AlertKind? Evaluate(HealthReport report)
    {
        if (report.Level == HealthLevel.Inconclusive) return null;
        if (report.Level == HealthLevel.NoIssue)
        {
            if (_active is null) return null;
            _active = null;
            return AlertKind.Recovered;
        }

        string signature = $"{report.Level}:{string.Join(",", report.Issues.Select(i => i.Kind).Distinct().Order())}";
        if (signature == _active && time.GetElapsedTime(_alertedAt) < Reminder) return null;
        (_active, _alertedAt) = (signature, time.GetTimestamp());
        return report.Level == HealthLevel.Unhealthy ? AlertKind.Problem : AlertKind.Slow;
    }
}
