using ConnectionClue.Core;

namespace ConnectionClue.Presentation.Accessibility;

public enum AnnouncementVerbosity { Minimal, Standard, Verbose }

public enum AnnouncementKind { SessionState, LinkChange, MarkerAcknowledged, Progress, FindingReady, Error }

/// <summary>Maps to UIA notification processing: Immediate = ImportantMostRecent, Polite = MostRecent.</summary>
public enum AnnouncementUrgency { Polite, Immediate }

public sealed record AccessibilityPreferences(
    AnnouncementVerbosity Verbosity = AnnouncementVerbosity.Standard,
    MarkerAllowance MarkerAllowance = MarkerAllowance.Standard);

/// <param name="Coalesced">Link changes suppressed since the last announcement, to be summarised in this one.</param>
public readonly record struct AnnouncementDecision(bool Announce, AnnouncementUrgency Urgency, int Coalesced = 0)
{
    public static readonly AnnouncementDecision Suppress = new(false, AnnouncementUrgency.Polite);
}

/// <summary>
/// Decides what reaches a screen reader during a capture: important events at once, progress throttled by
/// verbosity, bursts of link changes coalesced. Per-second values are never announced.
/// </summary>
public sealed class AnnouncementPolicy(TimeProvider time, AnnouncementVerbosity verbosity)
{
    private static readonly TimeSpan LinkCoalesceWindow = TimeSpan.FromSeconds(5);
    private long? _lastProgress, _lastLink;
    private int _pendingLinks;

    public int PendingLinkChanges => _pendingLinks;

    public AnnouncementDecision Evaluate(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Error or AnnouncementKind.MarkerAcknowledged => new(true, AnnouncementUrgency.Immediate),
        AnnouncementKind.SessionState or AnnouncementKind.FindingReady => new(true, AnnouncementUrgency.Polite, TakePending()),
        AnnouncementKind.LinkChange => LinkChange(),
        AnnouncementKind.Progress => Progress(),
        _ => AnnouncementDecision.Suppress,
    };

    private AnnouncementDecision LinkChange()
    {
        if (_lastLink is { } last && time.GetElapsedTime(last) < LinkCoalesceWindow)
        {
            _pendingLinks++;
            return AnnouncementDecision.Suppress;
        }
        _lastLink = time.GetTimestamp();
        return new(true, AnnouncementUrgency.Immediate, TakePending());
    }

    private AnnouncementDecision Progress()
    {
        TimeSpan? interval = verbosity switch
        {
            AnnouncementVerbosity.Verbose => TimeSpan.FromSeconds(30),
            AnnouncementVerbosity.Standard => TimeSpan.FromSeconds(60),
            _ => null,
        };
        if (interval is null || (_lastProgress is { } last && time.GetElapsedTime(last) < interval)) return AnnouncementDecision.Suppress;
        _lastProgress = time.GetTimestamp();
        return new(true, AnnouncementUrgency.Polite, TakePending());
    }

    private int TakePending()
    {
        int pending = _pendingLinks;
        _pendingLinks = 0;
        return pending;
    }
}
