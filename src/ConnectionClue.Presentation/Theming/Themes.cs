namespace ConnectionClue.Presentation.Theming;

/// <summary>Colour themes the user can pick. System follows Windows (light or dark); Windows high contrast always wins.</summary>
public enum AppTheme { Dark, Light, HighContrastDark, HighContrastLight, System }

/// <summary>A filled element: background, its text, a border and an accent (icon or tint) colour.</summary>
public sealed record Swatch(string Fill, string Text, string Border, string Accent);

/// <summary>
/// One theme's colours as #RRGGBB, so WCAG contrast is verified by unit tests without WPF (ThemeContrastTests): text 4.5:1
/// and component boundaries 3:1 in every theme, 7:1 for text in the high-contrast themes. Each colour has one meaning
/// everywhere: headings, field labels, body text, supporting text and links all differ, and every action has its own
/// colour by intent (Roles). Colour is never the only cue: headings are larger, links are underlined, buttons carry an icon
/// and a label, and disabled controls get a dashed outline. Hues follow the colour-blind-safe Okabe–Ito set where possible.
/// </summary>
public sealed record ThemePalette(
    AppTheme Theme, bool IsDark, bool IsHighContrast,
    string Background, string Card, string CardStroke,
    string Text, string TextSecondary, string TextTertiary, string Heading, string Label,
    string Link, string Critical, string ControlFill, string ControlStroke, string SubtleFill,
    string DisabledFill, string DisabledText,
    IReadOnlyDictionary<string, Swatch> Roles,
    IReadOnlyDictionary<string, Swatch> Metrics,
    IReadOnlyDictionary<string, Swatch> Symptoms,
    ChartColors Chart)
{
    /// <summary>Minimum text contrast this theme promises: AAA for high contrast, AA otherwise.</summary>
    public double TextContrast => IsHighContrast ? 7 : 4.5;
}

/// <summary>Chart colours: bars, bars above the limit, unanswered checks (hatched), lag markers, grid lines.</summary>
public sealed record ChartColors(string Normal, string Over, string NoAnswer, string Marker, string Grid);

public static class Themes
{
    public static readonly AppTheme Default = AppTheme.Dark;

    /// <summary>
    /// Actions: Primary starts a check or test; Tool runs a diagnostic; Admin changes a Windows setting after an administrator
    /// prompt; Mark records a lag moment (the colour of the chart's lag markers); Danger stops; Advice opens recommendations;
    /// Neutral dismisses. Status badges and path lines: Success, Warning, Danger, Muted.
    /// </summary>
    public static readonly IReadOnlyList<string> RoleNames =
        ["Primary", "Tool", "Admin", "Mark", "Danger", "Advice", "Success", "Warning", "Neutral", "Muted"];

    /// <summary>Roles drawn as an outline on the card (their Fill is the card colour); the others are filled.</summary>
    public static readonly IReadOnlyList<string> OutlinedRoles = ["Tool", "Neutral"];

    private static Swatch Filled(string fill, string text) => new(fill, text, fill, fill);

    private static Swatch Outlined(string card, string text, string border) => new(card, text, border, text);

    // Dark: light fills with black text, so every button stands out from the dark card (Windows 11 dark style).
    public static readonly ThemePalette Dark = new(AppTheme.Dark, IsDark: true, IsHighContrast: false,
        Background: "#202020", Card: "#2B2B2B", CardStroke: "#3F3F3F",
        Text: "#FFFFFF", TextSecondary: "#C8C8C8", TextTertiary: "#9E9E9E", Heading: "#FFD479", Label: "#D0C4FF",
        Link: "#99EBFF", Critical: "#FF99A4", ControlFill: "#3A3A3A", ControlStroke: "#7A7A7A", SubtleFill: "#383838",
        DisabledFill: "#333333", DisabledText: "#9A9A9A",
        Roles: new Dictionary<string, Swatch>
        {
            ["Primary"] = Filled("#60CDFF", "#000000"),
            ["Tool"] = Outlined("#2B2B2B", "#5EEAD4", "#5EEAD4"),
            ["Admin"] = Filled("#FFB347", "#000000"),
            ["Mark"] = Filled("#E39BC4", "#000000"),
            ["Danger"] = Filled("#FF8A80", "#000000"),
            ["Advice"] = Filled("#C3A6FF", "#000000"),
            ["Success"] = Filled("#6CCB5F", "#000000"),
            ["Warning"] = Filled("#FCE100", "#000000"),
            ["Neutral"] = Outlined("#2B2B2B", "#E6E6E6", "#8A8A8A"),
            ["Muted"] = Filled("#8A8A8A", "#000000"),
        },
        Metrics: new Dictionary<string, Swatch>
        {
            ["Download"] = new("#0E3A55", "#FFFFFF", "#2C7DB3", "#7CC4F2"),
            ["Upload"] = new("#3A2A57", "#FFFFFF", "#7A5CB8", "#C9B3F5"),
            ["Latency"] = new("#4D3808", "#FFFFFF", "#B7860F", "#FFD166"),
            ["Variation"] = new("#0E4032", "#FFFFFF", "#2E9C74", "#7FE0B8"),
        },
        // Symptoms: Fill/Text when selected; Border and Accent (tint background) for the unselected chip.
        Symptoms: new Dictionary<string, Swatch>
        {
            ["Symptom_Gaming"] = new("#3F51B5", "#FFFFFF", "#7E8FE0", "#262D4D"),
            ["Symptom_Video"] = new("#B84A1C", "#FFFFFF", "#E08A62", "#452818"),
            ["Symptom_Calls"] = new("#1B7A4B", "#FFFFFF", "#4DB888", "#173F2C"),
            ["Symptom_Disconnects"] = new("#B0305A", "#FFFFFF", "#DE6E93", "#4A1F2E"),
        },
        Chart: new("#56B4E9", "#E69F00", "#FF7F50", "#E39BC4", "#666666"));

    // Light: dark fills with white text on white cards.
    public static readonly ThemePalette Light = new(AppTheme.Light, IsDark: false, IsHighContrast: false,
        Background: "#F3F3F3", Card: "#FFFFFF", CardStroke: "#DADADA",
        Text: "#1B1B1B", TextSecondary: "#5C5C5C", TextTertiary: "#767676", Heading: "#7A4B00", Label: "#5B2FB0",
        Link: "#005A9E", Critical: "#C42B1C", ControlFill: "#F5F5F5", ControlStroke: "#8A8A8A", SubtleFill: "#EAEAEA",
        DisabledFill: "#F0F0F0", DisabledText: "#6E6E6E",
        Roles: new Dictionary<string, Swatch>
        {
            ["Primary"] = Filled("#005FB8", "#FFFFFF"),
            ["Tool"] = Outlined("#FFFFFF", "#00665E", "#00665E"),
            ["Admin"] = Filled("#9A4A00", "#FFFFFF"),
            ["Mark"] = Filled("#A1266D", "#FFFFFF"),
            ["Danger"] = Filled("#C42B1C", "#FFFFFF"),
            ["Advice"] = Filled("#6B3FA0", "#FFFFFF"),
            ["Success"] = Filled("#0F7B0F", "#FFFFFF"),
            ["Warning"] = Filled("#A15C00", "#FFFFFF"),
            ["Neutral"] = Outlined("#FFFFFF", "#1B1B1B", "#6B6B6B"),
            ["Muted"] = Filled("#6B6B6B", "#FFFFFF"),
        },
        Metrics: new Dictionary<string, Swatch>
        {
            ["Download"] = new("#DDEFFB", "#1B1B1B", "#5AA9DD", "#005A9E"),
            ["Upload"] = new("#ECE4FA", "#1B1B1B", "#9B7FD6", "#5B3FA3"),
            ["Latency"] = new("#FFF2D1", "#1B1B1B", "#D9A21B", "#7A5000"),
            ["Variation"] = new("#DDF4E9", "#1B1B1B", "#4DB58A", "#0B6B45"),
        },
        Symptoms: new Dictionary<string, Swatch>
        {
            ["Symptom_Gaming"] = new("#3F51B5", "#FFFFFF", "#6E82D6", "#E3E7FB"),
            ["Symptom_Video"] = new("#B84A1C", "#FFFFFF", "#C8663A", "#FBE6DC"),
            ["Symptom_Calls"] = new("#1B7A4B", "#FFFFFF", "#2E8B5E", "#DCF2E6"),
            ["Symptom_Disconnects"] = new("#B0305A", "#FFFFFF", "#D6557F", "#F9E0E8"),
        },
        Chart: new("#0072B2", "#B77A00", "#D55E00", "#A1266D", "#B0B0B0"));

    // High contrast: pure black or white surfaces, strong outlines, saturated accents; text at AAA (7:1).
    public static readonly ThemePalette HighContrastDark = new(AppTheme.HighContrastDark, IsDark: true, IsHighContrast: true,
        Background: "#000000", Card: "#000000", CardStroke: "#FFFFFF",
        Text: "#FFFFFF", TextSecondary: "#E6E6E6", TextTertiary: "#C0C0C0", Heading: "#7FDBFF", Label: "#D6B4FD",
        Link: "#FFFF00", Critical: "#FF8C8C", ControlFill: "#000000", ControlStroke: "#FFFFFF", SubtleFill: "#262626",
        DisabledFill: "#000000", DisabledText: "#A6A6A6",
        Roles: new Dictionary<string, Swatch>
        {
            ["Primary"] = Filled("#1AEBFF", "#000000"),
            ["Tool"] = Outlined("#000000", "#1AEBFF", "#1AEBFF"),
            ["Admin"] = Filled("#FFB366", "#000000"),
            ["Mark"] = Filled("#D6B4FD", "#000000"),
            ["Danger"] = Filled("#FF8C8C", "#000000"),
            ["Advice"] = Filled("#D6B4FD", "#000000"),
            ["Success"] = Filled("#3FF23F", "#000000"),
            ["Warning"] = Filled("#FFFF00", "#000000"),
            ["Neutral"] = Outlined("#000000", "#FFFFFF", "#FFFFFF"),
            ["Muted"] = Filled("#C0C0C0", "#000000"),
        },
        Metrics: new Dictionary<string, Swatch>
        {
            ["Download"] = new("#000000", "#FFFFFF", "#1AEBFF", "#1AEBFF"),
            ["Upload"] = new("#000000", "#FFFFFF", "#D6B4FD", "#D6B4FD"),
            ["Latency"] = new("#000000", "#FFFFFF", "#FFFF00", "#FFFF00"),
            ["Variation"] = new("#000000", "#FFFFFF", "#3FF23F", "#3FF23F"),
        },
        Symptoms: new Dictionary<string, Swatch>
        {
            ["Symptom_Gaming"] = new("#1AEBFF", "#000000", "#1AEBFF", "#000000"),
            ["Symptom_Video"] = new("#FFB366", "#000000", "#FFB366", "#000000"),
            ["Symptom_Calls"] = new("#3FF23F", "#000000", "#3FF23F", "#000000"),
            ["Symptom_Disconnects"] = new("#FF8C8C", "#000000", "#FF8C8C", "#000000"),
        },
        Chart: new("#1AEBFF", "#FFFF00", "#FF8C8C", "#D6B4FD", "#808080"));

    public static readonly ThemePalette HighContrastLight = new(AppTheme.HighContrastLight, IsDark: false, IsHighContrast: true,
        Background: "#FFFFFF", Card: "#FFFFFF", CardStroke: "#000000",
        Text: "#000000", TextSecondary: "#1A1A1A", TextTertiary: "#3D3D3D", Heading: "#5C3A00", Label: "#4B0082",
        Link: "#0000C0", Critical: "#A80000", ControlFill: "#FFFFFF", ControlStroke: "#000000", SubtleFill: "#E6E6E6",
        DisabledFill: "#FFFFFF", DisabledText: "#595959",
        Roles: new Dictionary<string, Swatch>
        {
            ["Primary"] = Filled("#00008B", "#FFFFFF"),
            ["Tool"] = Outlined("#FFFFFF", "#004D40", "#004D40"),
            ["Admin"] = Filled("#7A2E00", "#FFFFFF"),
            ["Mark"] = Filled("#4B0082", "#FFFFFF"),
            ["Danger"] = Filled("#A80000", "#FFFFFF"),
            ["Advice"] = Filled("#4B0082", "#FFFFFF"),
            ["Success"] = Filled("#005A00", "#FFFFFF"),
            ["Warning"] = Filled("#5C3A00", "#FFFFFF"),
            ["Neutral"] = Outlined("#FFFFFF", "#000000", "#000000"),
            ["Muted"] = Filled("#3D3D3D", "#FFFFFF"),
        },
        Metrics: new Dictionary<string, Swatch>
        {
            ["Download"] = new("#FFFFFF", "#000000", "#00008B", "#00008B"),
            ["Upload"] = new("#FFFFFF", "#000000", "#4B0082", "#4B0082"),
            ["Latency"] = new("#FFFFFF", "#000000", "#5C3A00", "#5C3A00"),
            ["Variation"] = new("#FFFFFF", "#000000", "#005A00", "#005A00"),
        },
        Symptoms: new Dictionary<string, Swatch>
        {
            ["Symptom_Gaming"] = new("#00008B", "#FFFFFF", "#00008B", "#FFFFFF"),
            ["Symptom_Video"] = new("#7A2E00", "#FFFFFF", "#7A2E00", "#FFFFFF"),
            ["Symptom_Calls"] = new("#005A00", "#FFFFFF", "#005A00", "#FFFFFF"),
            ["Symptom_Disconnects"] = new("#A80000", "#FFFFFF", "#A80000", "#FFFFFF"),
        },
        Chart: new("#00008B", "#8A4B00", "#A80000", "#4B0082", "#767676"));

    public static IReadOnlyList<ThemePalette> All { get; } = [Dark, Light, HighContrastDark, HighContrastLight];

    /// <summary>
    /// The palette to use, or null when Windows high contrast is on: then the user's own Windows colours apply whatever
    /// theme is chosen, because people who rely on them set them system-wide.
    /// </summary>
    public static ThemePalette? Resolve(AppTheme chosen, bool windowsHighContrast, bool windowsUsesLight) =>
        windowsHighContrast ? null : chosen switch
        {
            AppTheme.Light => Light,
            AppTheme.HighContrastDark => HighContrastDark,
            AppTheme.HighContrastLight => HighContrastLight,
            AppTheme.System => windowsUsesLight ? Light : Dark,
            _ => Dark,
        };

    /// <summary>WCAG 2.x contrast ratio of two #RRGGBB colours.</summary>
    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#') throw new ArgumentException($"Expected #RRGGBB, got {hex}", nameof(hex));
        double Channel(int i)
        {
            double c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
