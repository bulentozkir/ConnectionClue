using ConnectionClue.Presentation.Theming;

namespace ConnectionClue.Presentation.Tests;

/// <summary>WCAG 2.2 contrast for every theme: 4.5:1 for text (7:1 in high contrast) and 3:1 for graphics and outlines.</summary>
public class ThemeContrastTests
{
    public static TheoryData<AppTheme> Palettes => [.. Themes.All.Select(t => t.Theme)];

    private static ThemePalette P(AppTheme theme) => Themes.All.Single(t => t.Theme == theme);

    private static void AtLeast(string foreground, string background, double minimum, string what)
    {
        double ratio = Themes.Contrast(foreground, background);
        Assert.True(ratio >= minimum, $"{what}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Text_links_and_errors_are_readable_on_every_surface(AppTheme theme)
    {
        var p = P(theme);
        foreach (var surface in new[] { p.Card, p.Background })
        {
            AtLeast(p.Text, surface, p.TextContrast, "text");
            AtLeast(p.TextSecondary, surface, p.TextContrast, "secondary text");
            AtLeast(p.Heading, surface, p.TextContrast, "heading");
            AtLeast(p.Label, surface, p.TextContrast, "label");
            AtLeast(p.Link, surface, p.TextContrast, "link");
            AtLeast(p.Critical, surface, 4.5, "error text");
            AtLeast(p.TextTertiary, surface, 3, "tertiary lines");
        }
        AtLeast(p.Text, p.ControlFill, p.TextContrast, "text on controls");
        AtLeast(p.Text, p.SubtleFill, p.TextContrast, "text on hover");
        AtLeast(p.ControlStroke, p.Card, 3, "control outline");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Headings_labels_body_and_links_each_have_their_own_colour(AppTheme theme)
    {
        var p = P(theme);
        string[] text = [p.Text, p.Heading, p.Label, p.Link];
        Assert.Equal(text.Length, text.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Every_action_is_readable_and_its_outline_or_fill_stands_out(AppTheme theme)
    {
        var p = P(theme);
        Assert.Equal(Themes.RoleNames.Order(), p.Roles.Keys.Order());
        foreach (var (role, s) in p.Roles)
        {
            AtLeast(s.Text, s.Fill, p.TextContrast, role + " label");
            double boundary = Math.Max(Themes.Contrast(s.Fill, p.Card), Themes.Contrast(s.Border, p.Card));
            Assert.True(boundary >= 3, $"{role} button boundary is {boundary:0.00}:1 on the card, needs 3:1");
        }
        foreach (string role in Themes.OutlinedRoles) Assert.Equal(p.Card, p.Roles[role].Fill);
        // Primary, tool and admin actions never share a colour, so their purpose shows at a glance.
        string[] actions = [p.Roles["Primary"].Fill, p.Roles["Tool"].Border, p.Roles["Admin"].Fill, p.Roles["Danger"].Fill];
        Assert.Equal(actions.Length, actions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Disabled_controls_stay_legible(AppTheme theme)
    {
        var p = P(theme);
        // Disabled controls are exempt from WCAG contrast; ConnectionClue still keeps them at 3:1 and adds a dashed outline.
        AtLeast(p.DisabledText, p.DisabledFill, 3, "disabled label");
        AtLeast(p.DisabledText, p.Card, 3, "disabled outline");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Metric_tiles_and_symptom_chips_are_readable(AppTheme theme)
    {
        var p = P(theme);
        foreach (var (metric, s) in p.Metrics)
        {
            AtLeast(s.Text, s.Fill, p.TextContrast, metric + " value");
            AtLeast(p.TextSecondary, s.Fill, 4.5, metric + " detail");
            AtLeast(s.Accent, s.Fill, 3, metric + " icon");
        }
        foreach (var (symptom, s) in p.Symptoms)
        {
            AtLeast(s.Text, s.Fill, p.TextContrast, symptom + " selected");
            AtLeast(p.Text, s.Accent, p.TextContrast, symptom + " unselected");
            AtLeast(s.Border, p.Card, 3, symptom + " outline");
        }
        Assert.Equal(4, p.Metrics.Count);
        Assert.Equal(4, p.Symptoms.Count);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Chart_marks_stand_out_from_the_card(AppTheme theme)
    {
        var c = P(theme).Chart;
        foreach (var (what, color) in new[] { ("bars", c.Normal), ("over limit", c.Over), ("no answer", c.NoAnswer), ("lag markers", c.Marker) })
            AtLeast(color, P(theme).Card, 3, what);
        Assert.NotEqual(c.Normal, c.Over);
        Assert.Equal(c.Marker, P(theme).Roles["Mark"].Fill); // the lag button has the colour of the marks it draws
    }

    [Fact]
    public void Dark_is_the_default_and_windows_high_contrast_wins()
    {
        Assert.Equal(AppTheme.Dark, Themes.Default);
        Assert.Null(Themes.Resolve(AppTheme.Light, windowsHighContrast: true, windowsUsesLight: true));
        Assert.Same(Themes.Light, Themes.Resolve(AppTheme.System, windowsHighContrast: false, windowsUsesLight: true));
        Assert.Same(Themes.Dark, Themes.Resolve(AppTheme.System, windowsHighContrast: false, windowsUsesLight: false));
        Assert.Same(Themes.HighContrastLight, Themes.Resolve(AppTheme.HighContrastLight, false, false));
    }

    [Fact]
    public void Contrast_matches_the_wcag_formula()
    {
        Assert.Equal(21, Themes.Contrast("#000000", "#FFFFFF"), 2);
        Assert.Equal(4.54, Themes.Contrast("#767676", "#FFFFFF"), 2);
    }

    [Fact]
    public void Settings_default_to_dark_and_offer_five_named_themes()
    {
        var settings = new ConnectionClue.Presentation.ViewModels.SettingsViewModel(
            ConnectionClue.Presentation.Localization.Localizer.Default, System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(["Dark", "Light", "High contrast dark", "High contrast light", "Use Windows setting"], settings.Themes.Select(t => t.Label));
    }
}
