using ConnectionClue.Core;
using ConnectionClue.Presentation.Accessibility;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public class AnnouncementPolicyTests
{
    private readonly FakeTimeProvider _time = new();

    [Theory]
    [InlineData(AnnouncementVerbosity.Minimal)]
    [InlineData(AnnouncementVerbosity.Standard)]
    [InlineData(AnnouncementVerbosity.Verbose)]
    public void Important_events_are_always_immediate(AnnouncementVerbosity verbosity)
    {
        var policy = new AnnouncementPolicy(_time, verbosity);
        Assert.Equal(new AnnouncementDecision(true, AnnouncementUrgency.Immediate), policy.Evaluate(AnnouncementKind.MarkerAcknowledged));
        Assert.Equal(new AnnouncementDecision(true, AnnouncementUrgency.Immediate), policy.Evaluate(AnnouncementKind.Error));
        Assert.True(policy.Evaluate(AnnouncementKind.FindingReady).Announce);
        Assert.True(policy.Evaluate(AnnouncementKind.LinkChange).Announce);
    }

    [Theory]
    [InlineData(AnnouncementVerbosity.Minimal, 0)]
    [InlineData(AnnouncementVerbosity.Standard, 2)]
    [InlineData(AnnouncementVerbosity.Verbose, 4)]
    public void Progress_is_throttled_by_verbosity(AnnouncementVerbosity verbosity, int expectedInTwoMinutes)
    {
        var policy = new AnnouncementPolicy(_time, verbosity);
        int announced = 0;
        for (int second = 0; second < 120; second++, _time.Advance(TimeSpan.FromSeconds(1)))
            if (policy.Evaluate(AnnouncementKind.Progress).Announce) announced++;
        Assert.Equal(expectedInTwoMinutes, announced);
    }

    [Fact]
    public void Link_change_bursts_are_coalesced_and_summarised()
    {
        var policy = new AnnouncementPolicy(_time, AnnouncementVerbosity.Standard);
        Assert.True(policy.Evaluate(AnnouncementKind.LinkChange).Announce);
        Assert.False(policy.Evaluate(AnnouncementKind.LinkChange).Announce);
        Assert.False(policy.Evaluate(AnnouncementKind.LinkChange).Announce);
        Assert.Equal(2, policy.PendingLinkChanges);

        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(new AnnouncementDecision(true, AnnouncementUrgency.Immediate, 2), policy.Evaluate(AnnouncementKind.LinkChange));
        Assert.Equal(0, policy.PendingLinkChanges);
    }

    [Fact]
    public void Session_end_reports_links_still_pending()
    {
        var policy = new AnnouncementPolicy(_time, AnnouncementVerbosity.Minimal);
        policy.Evaluate(AnnouncementKind.LinkChange);
        policy.Evaluate(AnnouncementKind.LinkChange);
        Assert.Equal(1, policy.Evaluate(AnnouncementKind.SessionState).Coalesced);
    }

    [Fact]
    public void Extended_marker_allowance_gives_more_reaction_time()
    {
        Assert.Equal((TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)), MarkerWindow.For(MarkerAllowance.Standard));
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5)), MarkerWindow.For(MarkerAllowance.Extended));
        Assert.Equal(MarkerAllowance.Standard, new AccessibilityPreferences().MarkerAllowance);
    }
}
