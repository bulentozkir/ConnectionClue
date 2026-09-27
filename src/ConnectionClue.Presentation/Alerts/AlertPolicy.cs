using ConnectionClue.Analysis;

namespace ConnectionClue.Presentation.Alerts;

public enum AlertKind { Problem, Slow, Recovered, Disconnected, Reconnected }

/// <summary>What started the check: the user, the regular background schedule, or the one-time check after a reconnection.</summary>
public enum AlertTrigger { Manual, Background, Reconnect }

/// <summary>A notification to show. FirstSeen is set when the same problem was already reported: it continues since then.</summary>
public sealed record AlertDecision(AlertKind Kind, HealthLevel Level, DateTimeOffset? FirstSeen = null);

/// <summary>
/// Manual checks alert on change: a new or different problem, an hourly reminder while it persists, and one recovery notice.
/// Background checks alert on every check that finds a problem (saying since when it continues) and once on recovery.
/// The one-time check after a reconnection always reports its result. Inconclusive checks never clear an active problem,
/// and only the reconnect check reports them.
/// </summary>
public sealed class AlertPolicy(TimeProvider time)
{
    public static readonly TimeSpan Reminder = TimeSpan.FromHours(1);
    private string? _active;
    private long _alertedAt;
    private DateTimeOffset _firstSeen;

    public AlertDecision? Evaluate(HealthReport report, AlertTrigger trigger = AlertTrigger.Manual)
    {
        bool reconnect = trigger == AlertTrigger.Reconnect;
        if (report.Level == HealthLevel.Inconclusive) return reconnect ? new(AlertKind.Reconnected, report.Level) : null;
        if (report.Level == HealthLevel.NoIssue)
        {
            bool recovered = _active is not null;
            _active = null;
            return reconnect ? new(AlertKind.Reconnected, report.Level) : recovered ? new(AlertKind.Recovered, report.Level) : null;
        }

        string signature = $"{report.Level}:{string.Join(",", report.Issues.Select(i => i.Kind).Distinct().Order())}";
        bool continues = signature == _active;
        if (continues && trigger == AlertTrigger.Manual && time.GetElapsedTime(_alertedAt) < Reminder) return null;
        if (!continues) (_active, _firstSeen) = (signature, time.GetUtcNow());
        _alertedAt = time.GetTimestamp();
        var kind = reconnect ? AlertKind.Reconnected : report.Level == HealthLevel.Unhealthy ? AlertKind.Problem : AlertKind.Slow;
        return new(kind, report.Level, continues ? _firstSeen : null);
    }
}
