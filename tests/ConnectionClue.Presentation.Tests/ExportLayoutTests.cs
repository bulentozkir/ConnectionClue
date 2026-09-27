using System.Globalization;
using System.Xml.Linq;
using ConnectionClue.Presentation.Localization;

namespace ConnectionClue.Presentation.Tests;

public sealed class ExportLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Pdf_and_mhtml_are_adjacent_after_recommendations_and_share_visibility()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var home = root.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "CheckPage");
        var pdf = home.Descendants(Wpf + "Button").Single(e => (string?)e.Attribute("Command") == "{Binding ExportResultsCommand}");
        var mhtml = home.Descendants(Wpf + "Button").Single(e => (string?)e.Attribute("Command") == "{Binding ExportMhtmlCommand}");
        Assert.Same(pdf.Parent, mhtml.Parent);
        Assert.Same(mhtml, pdf.ElementsAfterSelf().First());
        Assert.Equal("{Binding ShowExportResults, Converter={StaticResource Shown}}", (string?)pdf.Parent!.Attribute("Visibility"));
        Assert.Equal("{Binding GoToRecommendationsCommand}", (string?)pdf.Parent.ElementsBeforeSelf().Last().Attribute("Command"));
        Assert.Equal("Export (PDF)", Localizer.Default.Get("Export_Button", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Export (MHTML)", Localizer.Default.Get("Export_MhtmlButton", CultureInfo.GetCultureInfo("en")));
        Assert.Contains(home.Descendants(Wpf + "TextBlock"),
            e => (string?)e.Attribute("Text") == "{Binding ResultsExportStatus}" &&
                 (string?)e.Attribute("AutomationProperties.LiveSetting") == "Polite");
    }

    [Fact]
    public void Every_main_window_button_has_hover_keyboard_and_screen_reader_help()
    {
        var main = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppStyles.xaml"));
        var styles = main.Descendants(Wpf + "Style").Concat(app.Descendants(Wpf + "Style"))
            .Where(s => s.Attribute(Xaml + "Key") is not null)
            .ToDictionary(s => (string)s.Attribute(Xaml + "Key")!);
        foreach (var button in main.Descendants().Where(e => e.Name == Wpf + "Button" ||
                     e.Name == Wpf + "RadioButton" || e.Name == Wpf + "CheckBox"))
        {
            string name = (string?)button.Attribute("Command") ?? (string?)button.Attribute("IsChecked") ?? button.ToString();
            Assert.False(string.IsNullOrWhiteSpace(Property(button, "AutomationProperties.HelpText")), name);
            Assert.True(Property(button, "ToolTip") is not null || button.Element(Wpf + "Button.ToolTip") is not null, name);
            Assert.Equal("True", Property(button, "ToolTipService.ShowsToolTipOnKeyboardFocus"));
        }

        string? Property(XElement element, string property)
        {
            if (element.Attribute(property) is { } direct) return direct.Value;
            return StyleProperty((string?)element.Attribute("Style"), property);
        }

        string? StyleProperty(string? reference, string property)
        {
            const string prefix = "{StaticResource ";
            if (reference is null || !reference.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var style = styles[reference[prefix.Length..^1]];
            var setter = style.Elements(Wpf + "Setter").SingleOrDefault(e => (string?)e.Attribute("Property") == property);
            return setter is null
                ? StyleProperty((string?)style.Attribute("BasedOn"), property)
                : (string?)setter.Attribute("Value");
        }
    }
}
