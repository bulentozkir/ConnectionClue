using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ConnectionClue.Presentation.Theming;
using Microsoft.Win32;

namespace ConnectionClue.App;

/// <summary>
/// Applies the chosen theme at run time, without a restart. Light and dark use WPF's Fluent theme with the app's verified
/// colours on top. The high-contrast themes also re-colour every Fluent control brush (combo boxes, tooltips, scroll bars,
/// text boxes) by what it paints, so no control keeps a low-contrast colour. When Windows high contrast is on, it wins:
/// the Fluent high-contrast theme and the user's own Windows colours are used, whatever theme is chosen.
/// Every palette is contrast-tested (ThemeContrastTests); every coloured element also has an icon and a label.
/// </summary>
internal static class ThemeManager
{
    private static readonly HashSet<object> Overrides = [];

    public static ThemePalette? Palette { get; private set; } = Themes.Dark;

    /// <summary>True while Windows high contrast is on (system colours in use).</summary>
    public static bool WindowsHighContrast => SystemParameters.HighContrast;

    /// <summary>Raised after a theme change, so custom-drawn controls (the chart) repaint.</summary>
    public static event EventHandler? Changed;

    public static void Apply(Application app, AppTheme chosen)
    {
        var palette = Themes.Resolve(chosen, WindowsHighContrast, WindowsUsesLight());
        var resources = app.Resources;
        foreach (var key in Overrides) resources.Remove(key);
        Overrides.Clear();
#pragma warning disable WPF0001 // ThemeMode is the supported way to pick the Fluent light or dark theme
        app.ThemeMode = palette is null ? ThemeMode.System : palette.IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Palette = palette;
        if (palette is null) ApplySystemColours(resources);
        else
        {
            if (palette.IsHighContrast) RecolourFluent(resources, palette);
            ApplyPalette(resources, palette);
        }
        WrapToolTips(resources);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Long help texts wrap in every tooltip, on top of the theme's own tooltip style.</summary>
    private static void WrapToolTips(ResourceDictionary resources)
    {
        resources.Remove(typeof(ToolTip));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var style = new Style(typeof(ToolTip), Application.Current?.TryFindResource(typeof(ToolTip)) as Style);
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 360.0));
        style.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, new DataTemplate { VisualTree = text }));
        resources[typeof(ToolTip)] = style;
        Overrides.Add(typeof(ToolTip));
    }

    private static void Set(ResourceDictionary resources, object key, string hex) => Set(resources, key, Brush(hex));

    private static void Set(ResourceDictionary resources, object key, Brush brush)
    {
        resources[key] = brush;
        Overrides.Add(key);
    }

    private static void ApplyPalette(ResourceDictionary r, ThemePalette p)
    {
        Set(r, "ApplicationBackgroundBrush", p.Background);
        Set(r, "CardBackgroundFillColorDefaultBrush", p.Card);
        Set(r, "CardStrokeColorDefaultBrush", p.CardStroke);
        Set(r, "TextFillColorPrimaryBrush", p.Text);
        Set(r, "TextFillColorSecondaryBrush", p.TextSecondary);
        Set(r, "TextFillColorTertiaryBrush", p.TextTertiary);
        Set(r, "HeadingBrush", p.Heading);
        Set(r, "LabelBrush", p.Label);
        Set(r, "HyperlinkForeground", p.Link);
        Set(r, "LinkBrush", p.Link);
        Set(r, "AccentTextFillColorPrimaryBrush", p.Link);
        Set(r, "SystemFillColorCriticalBrush", p.Critical);
        Set(r, "ControlFillColorDefaultBrush", p.ControlFill);
        Set(r, "ControlStrokeColorDefaultBrush", p.ControlStroke);
        Set(r, "SubtleFillColorSecondaryBrush", p.SubtleFill);
        Set(r, "DisabledFillBrush", p.DisabledFill);
        Set(r, "DisabledTextBrush", p.DisabledText);
        foreach (var (role, s) in p.Roles)
        {
            Set(r, role + "Brush", s.Fill);
            Set(r, "On" + role + "Brush", s.Text);
            Set(r, role + "BorderBrush", s.Border);
        }
        foreach (var (metric, s) in p.Metrics)
        {
            Set(r, $"Metric{metric}FillBrush", s.Fill);
            Set(r, $"Metric{metric}TextBrush", s.Text);
            Set(r, $"Metric{metric}BorderBrush", s.Border);
            Set(r, $"Metric{metric}AccentBrush", s.Accent);
        }
        foreach (var (symptom, s) in p.Symptoms)
        {
            string name = symptom.Replace("Symptom_", "", StringComparison.Ordinal);
            Set(r, $"Chip{name}FillBrush", s.Fill);
            Set(r, $"Chip{name}TextBrush", s.Text);
            Set(r, $"Chip{name}BorderBrush", s.Border);
            Set(r, $"Chip{name}TintBrush", s.Accent);
        }
        Set(r, "ChipSelectedTextBrush", p.Symptoms.Values.First().Text);
        Set(r, "ChartGridBrush", p.Chart.Grid);
    }

    /// <summary>Windows high contrast: every custom colour maps to the user's own system colours.</summary>
    private static void ApplySystemColours(ResourceDictionary r)
    {
        Set(r, "HeadingBrush", SystemColors.WindowTextBrush);
        Set(r, "LabelBrush", SystemColors.WindowTextBrush);
        Set(r, "LinkBrush", SystemColors.HotTrackBrush);
        Set(r, "DisabledFillBrush", SystemColors.WindowBrush);
        Set(r, "DisabledTextBrush", SystemColors.GrayTextBrush);
        foreach (string role in Themes.RoleNames)
        {
            bool outlined = Themes.OutlinedRoles.Contains(role);
            Set(r, role + "Brush", outlined ? SystemColors.WindowBrush : SystemColors.HighlightBrush);
            Set(r, "On" + role + "Brush", outlined ? SystemColors.WindowTextBrush : SystemColors.HighlightTextBrush);
            Set(r, role + "BorderBrush", SystemColors.WindowTextBrush);
        }
        foreach (var metric in new[] { "Download", "Upload", "Latency", "Variation" })
        {
            Set(r, $"Metric{metric}FillBrush", SystemColors.WindowBrush);
            Set(r, $"Metric{metric}TextBrush", SystemColors.WindowTextBrush);
            Set(r, $"Metric{metric}BorderBrush", SystemColors.WindowTextBrush);
            Set(r, $"Metric{metric}AccentBrush", SystemColors.WindowTextBrush);
        }
        foreach (var chip in new[] { "Gaming", "Video", "Calls", "Disconnects" })
        {
            Set(r, $"Chip{chip}FillBrush", SystemColors.HighlightBrush);
            Set(r, $"Chip{chip}TextBrush", SystemColors.HighlightTextBrush);
            Set(r, $"Chip{chip}BorderBrush", SystemColors.WindowTextBrush);
            Set(r, $"Chip{chip}TintBrush", SystemColors.WindowBrush);
        }
        Set(r, "ChipSelectedTextBrush", SystemColors.HighlightTextBrush);
        Set(r, "ChartGridBrush", SystemColors.GrayTextBrush);
    }

    /// <summary>
    /// Re-colours every Fluent brush by what its name says it paints, so controls that the app does not style itself
    /// (combo boxes, tooltips, scroll bars, text boxes) also reach high contrast. Order matters: the most specific rule wins.
    /// </summary>
    private static void RecolourFluent(ResourceDictionary r, ThemePalette p)
    {
        string hover = p.SubtleFill, accent = p.Roles["Primary"].Fill, onAccent = p.Roles["Primary"].Text;
        foreach (string key in FluentBrushKeys(r))
        {
            string color =
                key.Contains("Disabled", StringComparison.Ordinal) ? p.TextTertiary
                : key.Contains("Hyperlink", StringComparison.Ordinal) && !key.Contains("Background", StringComparison.Ordinal) ? p.Link
                : key.Contains("Critical", StringComparison.Ordinal) ? p.Critical
                : key.Contains("OnAccent", StringComparison.Ordinal) || key.StartsWith("TextOnAccent", StringComparison.Ordinal)
                    || key.Contains("Selected", StringComparison.Ordinal) && (key.Contains("Foreground", StringComparison.Ordinal) || key.Contains("Text", StringComparison.Ordinal)) ? onAccent
                : key.StartsWith("Accent", StringComparison.Ordinal) && key.Contains("Text", StringComparison.Ordinal) ? p.Link
                : key.Contains("Accent", StringComparison.Ordinal) || key.Contains("Selected", StringComparison.Ordinal) || key.Contains("Pill", StringComparison.Ordinal)
                    || key.Contains("SelectionHighlight", StringComparison.Ordinal) ? accent
                : key.Contains("Stroke", StringComparison.Ordinal) || key.Contains("Border", StringComparison.Ordinal) || key.Contains("Thumb", StringComparison.Ordinal)
                    || key.Contains("Strong", StringComparison.Ordinal) ? p.Text
                : key.Contains("Foreground", StringComparison.Ordinal) || key.StartsWith("TextFill", StringComparison.Ordinal) || key.Contains("Glyph", StringComparison.Ordinal)
                    || key.Contains("Placeholder", StringComparison.Ordinal) || key.Contains("Arrow", StringComparison.Ordinal) ? p.Text
                : key.Contains("PointerOver", StringComparison.Ordinal) || key.Contains("Pressed", StringComparison.Ordinal) ? hover
                : p.Background;
            Set(r, key, color);
        }
    }

    private static IEnumerable<string> FluentBrushKeys(ResourceDictionary r)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Walk(ResourceDictionary d)
        {
            foreach (var key in d.Keys) if (key is string s && d[key] is SolidColorBrush) keys.Add(s);
            foreach (var merged in d.MergedDictionaries) Walk(merged);
        }
        foreach (var merged in r.MergedDictionaries) Walk(merged);
        return keys;
    }

    private static bool WindowsUsesLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light != 0;
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
