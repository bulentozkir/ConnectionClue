using System.Xml.Linq;

namespace ConnectionClue.Presentation.Tests;

public sealed class SettingsLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XElement Settings() => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"))
        .Descendants(Wpf + "TabControl").Single(e => (string?)e.Attribute(Xaml + "Name") == "SettingsTabs");

    [Fact]
    public void Settings_uses_three_named_tabs_instead_of_one_long_page()
    {
        var settings = Settings();
        var tabs = settings.Elements(Wpf + "TabItem").ToArray();
        Assert.Equal(["CheckSettingsTab", "NetworkSettingsTab", "PreferencesSettingsTab"],
            tabs.Select(t => (string?)t.Attribute(Xaml + "Name")));
        Assert.All(tabs, tab =>
        {
            Assert.Equal((string?)tab.Attribute("Header"), (string?)tab.Attribute("AutomationProperties.Name"));
            Assert.NotNull(tab.Attribute("AutomationProperties.HelpText"));
            var scroll = Assert.Single(tab.Elements(Wpf + "ScrollViewer"));
            Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility")); // Only needed for enlarged text.
            Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
            Assert.Equal(2, scroll.Element(Wpf + "Grid")!.Elements(Wpf + "StackPanel").Count());
        });
        Assert.DoesNotContain(settings.Ancestors(), e => e.Name == Wpf + "ScrollViewer");
    }

    [Fact]
    public void All_existing_editable_preferences_are_kept_once()
    {
        var bindings = Settings().Descendants()
            .Where(e => e.Name == Wpf + "TextBox" || e.Name == Wpf + "CheckBox" || e.Name == Wpf + "ComboBox")
            .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "Text" or "IsChecked" or "SelectedValue"))
            .Select(a => a.Value).ToArray();
        string[] properties =
        [
            "Settings.CheckSecondsText", "Settings.MeasureSpeed", "Settings.BackgroundEnabled",
            "Settings.IntervalMinutes", "Settings.BackgroundOnMobileEnabled", "Settings.StartWithWindows",
            "Settings.DelayLimitMs", "Settings.LossLimitPercent", "Settings.VariationLimitMs",
            "Settings.PlanDownloadText", "Settings.PlanUploadText", "Settings.GamingTarget", "Settings.VideoTarget",
            "Settings.CallsTarget", "Settings.DisconnectTarget", "Settings.Theme", "CurrentLanguage", "Settings.AiReview",
        ];
        Assert.Equal(properties.Length, bindings.Length);
        foreach (string property in properties)
            Assert.Single(bindings, b => b == $"{{Binding {property}}}" || b.StartsWith($"{{Binding {property},", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_checking_is_removed_but_history_controls_remain()
    {
        var settings = Settings();
        Assert.DoesNotContain(settings.DescendantsAndSelf().Attributes(),
            a => a.Value.Contains("Update_", StringComparison.Ordinal) || a.Value.Contains("CheckForUpdates", StringComparison.Ordinal)
                || a.Value.Contains("UpdateStatus", StringComparison.Ordinal) || a.Value.Contains("Settings_SectionUpdates", StringComparison.Ordinal));
        foreach (string command in new[] { "ClearHistoryCommand", "ConfirmClearHistoryCommand", "CancelClearHistoryCommand" })
            Assert.Single(settings.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Command") == $"{{Binding {command}}}");
    }

    [Fact]
    public void The_total_duration_hint_uses_the_full_card_width()
    {
        var hint = Settings().Descendants(Wpf + "TextBlock").Single(t => (string?)t.Attribute("Text") == "{Binding Settings.CheckTotalHint}");
        Assert.DoesNotContain(hint.Ancestors(), a => a.Name == Wpf + "DockPanel");
        Assert.Equal("Wrap", (string?)hint.Attribute("TextWrapping"));
    }

    [Fact]
    public void Tab_headers_use_the_verified_palette_and_a_visible_selection_marker()
    {
        var style = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppStyles.xaml"))
            .Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "SettingsTab");
        Assert.Contains(style.Descendants(Wpf + "Border"),
            b => (string?)b.Attribute(Xaml + "Name") == "TabFace" &&
                 (string?)b.Attribute("Background") == "{DynamicResource CardBackgroundFillColorDefaultBrush}");
        var selected = style.Descendants(Wpf + "Trigger").Single(t => (string?)t.Attribute("Property") == "IsSelected");
        Assert.Contains(selected.Elements(Wpf + "Setter"),
            s => (string?)s.Attribute("TargetName") == "TabHeader" &&
                 (string?)s.Attribute("Value") == "{DynamicResource OnPrimaryBrush}");
        Assert.Contains(selected.Elements(Wpf + "Setter"),
            s => (string?)s.Attribute("TargetName") == "SelectedTabMark" && (string?)s.Attribute("Value") == "Visible");
    }
}
