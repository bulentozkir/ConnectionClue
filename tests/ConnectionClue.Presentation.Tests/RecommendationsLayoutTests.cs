using System.Xml.Linq;

namespace ConnectionClue.Presentation.Tests;

public sealed class RecommendationsLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XElement Page(string name) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"))
        .Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

    [Fact]
    public void Home_keeps_the_result_and_controls_without_advisory_details()
    {
        var home = Page("CheckPage");
        string[] moved = ["TryPreview", "RecommendationSummary", "CheckedOkText", "BufferbloatText", "ServiceSummary"];
        Assert.DoesNotContain(home.DescendantsAndSelf().Attributes(),
            a => moved.Any(property => a.Value.Contains("{Binding " + property, StringComparison.Ordinal)));
        foreach (string command in new[] { "QuickCheckCommand", "GoToRecommendationsCommand", "LongCaptureCommand", "ExportResultsCommand" })
            Assert.Contains(home.Descendants(Wpf + "Button"), e => (string?)e.Attribute("Command") == $"{{Binding {command}}}");
        Assert.Contains(home.Descendants(Wpf + "TextBlock"), e => (string?)e.Attribute("Text") == "{Binding Summary}");
    }

    [Theory]
    [InlineData("RecommendationSummary")]
    [InlineData("BufferbloatText")]
    [InlineData("ServiceSummary")]
    public void Recommendation_details_wrap_and_have_accessible_names(string property)
    {
        var page = Page("RecommendationsPage");
        Assert.Equal("Auto", (string?)page.Attribute("VerticalScrollBarVisibility"));
        var text = Assert.Single(page.Descendants(Wpf + "TextBlock"),
            e => (string?)e.Attribute("Text") == $"{{Binding {property}}}");
        Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
        Assert.Null(text.Attribute("TextTrimming"));
        Assert.Contains(text.Ancestors(),
            e => (string?)e.Attribute("AutomationProperties.Name") == $"{{Binding {property}}}");
    }

    [Fact]
    public void Recommendations_keep_full_advice_and_verified_checks()
    {
        var page = Page("RecommendationsPage");
        Assert.Contains(page.Descendants(Wpf + "ItemsControl"),
            e => (string?)e.Attribute("ItemsSource") == "{Binding RecommendationBlocks}");
        foreach (string type in new[] { "RecommendationItem", "AdviceItem", "CheckedNote" })
        {
            var template = Assert.Single(page.Descendants(Wpf + "DataTemplate"),
                e => (string?)e.Attribute("DataType") == $"{{x:Type vm:{type}}}");
            Assert.DoesNotContain(template.Descendants().Attributes("TextTrimming"), a => a.Value != "None");
        }
    }
}
