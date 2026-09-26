using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ConnectionClue.Presentation.Localization;

namespace ConnectionClue.Presentation.Tests;

public partial class LocalizationTests
{
    private static readonly Localizer L = Localizer.Default;
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    public static TheoryData<string> Languages => [.. SupportedLanguages.All.Skip(1).Select(c => c.Name)];

    [Fact]
    public void Twenty_distinct_languages_with_rtl_only_for_arabic_and_urdu()
    {
        Assert.Equal(20, SupportedLanguages.All.Select(c => c.Name).Distinct().Count());
        Assert.Equal(["ar", "ur"], SupportedLanguages.All.Where(c => c.TextInfo.IsRightToLeft).Select(c => c.Name).Order());
        Assert.All(SupportedLanguages.All, c => Assert.False(string.IsNullOrWhiteSpace(c.NativeName)));
    }

    [Fact]
    public void Msix_resource_languages_match_supported_ui_cultures()
    {
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml"));
        var actual = manifest.Descendants(ns + "Resource")
            .Select(e => (string?)e.Attribute("Language")
                ?? throw new InvalidDataException("MSIX resource is missing its Language attribute."))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = SupportedLanguages.All.Skip(1).Select(culture => culture.Name)
            .Append("en-US")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(expected.SetEquals(actual),
            $"Expected {string.Join(", ", expected)}; found {string.Join(", ", actual)}.");
    }

    [Theory]
    [InlineData(new[] { "zh-TW" }, null, "zh-Hant")]
    [InlineData(new[] { "zh-HK" }, null, "zh-Hant")]
    [InlineData(new[] { "zh-CN" }, null, "zh-Hans")]
    [InlineData(new[] { "pt-PT" }, null, "pt")]
    [InlineData(new[] { "pt-BR" }, null, "pt")]
    [InlineData(new[] { "ha-Latn" }, null, "ha")]
    [InlineData(new[] { "es-MX" }, null, "es")]
    [InlineData(new[] { "ar-EG" }, null, "ar")]
    [InlineData(new[] { "pl-PL", "de-DE" }, null, "de")]
    [InlineData(new[] { "pl-PL" }, null, "en")]
    [InlineData(new[] { "not-a-tag!" }, null, "en")]
    [InlineData(new[] { "de-DE" }, "tr", "tr")]
    public void Resolves_windows_preferences_to_a_supported_language(string[] preferred, string? userOverride, string expected) =>
        Assert.Equal(expected, LanguageResolver.Resolve(preferred, userOverride).Name);

    [Theory]
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 21, PluralCategory.One)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("ru", 3, PluralCategory.Few)]
    [InlineData("ru", 13, PluralCategory.Many)]
    [InlineData("ru", 25, PluralCategory.Many)]
    [InlineData("ar", 0, PluralCategory.Zero)]
    [InlineData("ar", 2, PluralCategory.Two)]
    [InlineData("ar", 7, PluralCategory.Few)]
    [InlineData("ar", 11, PluralCategory.Many)]
    [InlineData("ar", 100, PluralCategory.Other)]
    [InlineData("ar", 103, PluralCategory.Few)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("pt", 1_000_000, PluralCategory.Many)]
    [InlineData("es", 0, PluralCategory.Other)]
    [InlineData("hi", 0, PluralCategory.One)]
    [InlineData("en", 0, PluralCategory.Other)]
    [InlineData("ja", 1, PluralCategory.Other)]
    [InlineData("zh-Hant", 1, PluralCategory.Other)]
    public void Cldr_plural_categories(string culture, long n, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Select(C(culture), n));

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_translates_localized_keys_with_required_plural_forms(string name)
    {
        var culture = C(name);
        var neutral = Entries(CultureInfo.InvariantCulture);
        var translated = Entries(culture);
        foreach (var key in neutral.Keys.Where(k => !k.Contains('.') && !UsesEnglishFallback(k)))
            Assert.True(translated.TryGetValue(key, out var text) && text.Length > 0, $"{name}: missing {key}");

        foreach (var plural in neutral.Keys.Where(k => k.Contains('.')).Select(k => k[..k.IndexOf('.')]).Distinct())
        {
            var forms = translated.Keys.Where(k => k.StartsWith(plural + ".", StringComparison.Ordinal))
                .Select(k => Enum.Parse<PluralCategory>(k[(plural.Length + 1)..], ignoreCase: true)).ToHashSet();
            var required = PluralRules.Required(culture);
            var missing = required.Where(form => !forms.Contains(form)
                && !UsesEnglishFallback($"{plural}.{form.ToString().ToLowerInvariant()}"));
            Assert.True(!missing.Any() && forms.IsSubsetOf(required),
                $"{name}: {plural} forms {string.Join(",", forms)}");
        }

        foreach (var (key, text) in translated)
        {
            var source = neutral.GetValueOrDefault(key) ?? neutral[key[..key.IndexOf('.')] + ".other"];
            Assert.True(Placeholders(text).IsSubsetOf(Placeholders(source)), $"{name}: {key} has unknown placeholders");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Unreviewed_feature_copy_falls_back_to_English(string name)
    {
        var culture = C(name);
        var neutral = Entries(CultureInfo.InvariantCulture);
        var translated = Entries(culture);
        foreach (var key in neutral.Keys.Where(UsesEnglishFallback))
            if (!translated.ContainsKey(key))
                Assert.Equal(neutral[key], L.Get(key, culture));
    }

    [Theory]
    [InlineData("en", 1, "1 minute left")]
    [InlineData("ru", 21, "Осталась 21 минута")]
    [InlineData("ru", 5, "Осталось 5 минут")]
    [InlineData("ar", 2, "دقيقتان متبقيتان")]
    [InlineData("fr", 0, "0 minute restante")]
    [InlineData("ja", 3, "残り 3 分")]
    public void Plural_messages_render_per_language(string culture, long n, string expected) =>
        Assert.Equal(expected, L.Plural("Capture_MinutesLeft", n, C(culture), C(culture)));

    [Fact]
    public void Unsupported_culture_falls_back_to_english() =>
        Assert.Equal("Stop", L.Get("Action_Stop", C("pl")));

    [Fact]
    public void Pseudo_localization_expands_and_keeps_placeholders()
    {
        var pseudo = PseudoLocalizer.Transform("{0} minutes left");
        Assert.Contains("{0}", pseudo);
        Assert.True(pseudo.Length >= "{0} minutes left".Length * 1.3);
        Assert.StartsWith("[", pseudo);
    }

    private static Dictionary<string, string> Entries(CultureInfo culture)
    {
        var set = L.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new MissingSatelliteAssemblyException(culture.Name);
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static bool UsesEnglishFallback(string key) =>
        key.StartsWith("Target_", StringComparison.Ordinal)
        || key.StartsWith("Settings_Targets", StringComparison.Ordinal)
        || key.StartsWith("Diagnostics_", StringComparison.Ordinal)
        || key.StartsWith("SupportReport_", StringComparison.Ordinal)
        || key.StartsWith("History_", StringComparison.Ordinal)
        || key.StartsWith("Bufferbloat_", StringComparison.Ordinal)
        || key.StartsWith("Startup_", StringComparison.Ordinal)
        || key.StartsWith("Update_", StringComparison.Ordinal)
        || key.StartsWith("Capture_Long", StringComparison.Ordinal)
        || key.StartsWith("Settings_Startup", StringComparison.Ordinal)
        || key.StartsWith("Settings_Plan", StringComparison.Ordinal)
        || key.StartsWith("Settings_Shortcut", StringComparison.Ordinal)
        || key.StartsWith("Verdict_", StringComparison.Ordinal)
        || key.StartsWith("Service_", StringComparison.Ordinal)
        || key.StartsWith("Jump_", StringComparison.Ordinal)
        || key is "Nav_Insights" or "Settings_InvalidPlanSpeed" or "Settings_SectionUpdates"
            or "Capture_MinutesLeft.one" or "Capture_SecondsLeft.one" or "Duration_Minutes.one";

    private static HashSet<string> Placeholders(string text) => [.. PlaceholderPattern().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
