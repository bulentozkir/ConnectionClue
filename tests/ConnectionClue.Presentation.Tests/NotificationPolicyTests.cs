using ConnectionClue.Presentation.Alerts;

namespace ConnectionClue.Presentation.Tests;

public sealed class NotificationPolicyTests
{
    [Fact]
    public void All_notification_sources_share_one_budget_for_the_entire_app_session()
    {
        var policy = new NotificationPolicy();
        Assert.False(policy.HasNotified);
        Assert.True(policy.TryNotify());
        Assert.True(policy.HasNotified);
        foreach (var kind in Enum.GetValues<AlertKind>())
            for (int i = 0; i < 1000; i++) Assert.False(policy.TryNotify(), $"{kind} must not create another notification.");
    }

    [Fact]
    public void A_new_app_session_has_its_own_budget()
    {
        var first = new NotificationPolicy();
        Assert.True(first.TryNotify());
        Assert.False(first.TryNotify());
        Assert.True(new NotificationPolicy().TryNotify());
    }

    [Fact]
    public void The_app_routes_every_balloon_through_the_shared_budget()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        Assert.Equal(1, source.Split("_tray.Notify(", StringSplitOptions.None).Length - 1);
        Assert.Contains("if (!_notifications.TryNotify()) return false;", source, StringComparison.Ordinal);
        Assert.Contains("Notify(a.Title, a.Body, a.Warning);", source, StringComparison.Ordinal);
        Assert.Contains("!Notify(\"ConnectionClue\"", source, StringComparison.Ordinal);
    }
}
