using System.Globalization;
using System.Resources;
using System.Text;

namespace ConnectionClue.Presentation.Localization;

/// <summary>
/// The 20 UI languages: the most-spoken languages worldwide, merged by written UI locale. Egyptian Arabic uses
/// Arabic (ar), Nigerian Pidgin falls back to English, and Yue plus Taiwanese Mandarin use Traditional Chinese.
/// </summary>
public static class SupportedLanguages
{
    public static IReadOnlyList<CultureInfo> All { get; } =
    [
        .. new[]
        {
            "en", "zh-Hans", "hi", "es", "ar", "fr", "bn", "pt", "ru", "id",
            "ur", "de", "ja", "mr", "vi", "te", "ha", "tr", "ta", "zh-Hant",
        }.Select(CultureInfo.GetCultureInfo),
    ];

    public static CultureInfo Default => All[0];
}

/// <summary>
/// Picks the UI language: explicit user override, else the first Windows preferred language whose culture chain
/// reaches a supported language (zh-TW → zh-Hant, pt-PT → pt), else English.
/// </summary>
public static class LanguageResolver
{
    public static CultureInfo Resolve(IEnumerable<string> preferredLanguages, string? userOverride = null)
    {
        foreach (var tag in userOverride is null ? preferredLanguages : preferredLanguages.Prepend(userOverride))
        {
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(tag); }
            catch (CultureNotFoundException) { continue; }
            for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
                if (SupportedLanguages.All.FirstOrDefault(s => s.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
                    return match;
        }
        return SupportedLanguages.Default;
    }
}

public enum PluralCategory { Zero, One, Two, Few, Many, Other }

/// <summary>CLDR cardinal plural rules for whole numbers, for the supported languages only.</summary>
public static class PluralRules
{
    public static PluralCategory Select(CultureInfo culture, long count)
    {
        long n = Math.Abs(count);
        bool millions = n != 0 && n % 1_000_000 == 0;
        return culture.TwoLetterISOLanguageName switch
        {
            "zh" or "ja" or "vi" or "id" => PluralCategory.Other,
            "hi" or "bn" => n <= 1 ? PluralCategory.One : PluralCategory.Other,
            "fr" or "pt" => n <= 1 ? PluralCategory.One : millions ? PluralCategory.Many : PluralCategory.Other,
            "es" => n == 1 ? PluralCategory.One : millions ? PluralCategory.Many : PluralCategory.Other,
            "ru" => (n % 10, n % 100) switch
            {
                (1, not 11) => PluralCategory.One,
                (>= 2 and <= 4, not (>= 12 and <= 14)) => PluralCategory.Few,
                _ => PluralCategory.Many,
            },
            "ar" => n switch
            {
                0 => PluralCategory.Zero,
                1 => PluralCategory.One,
                2 => PluralCategory.Two,
                _ when n % 100 is >= 3 and <= 10 => PluralCategory.Few,
                _ when n % 100 is >= 11 and <= 99 => PluralCategory.Many,
                _ => PluralCategory.Other,
            },
            _ => n == 1 ? PluralCategory.One : PluralCategory.Other, // en, de, tr, ur, mr, te, ta, ha
        };
    }

    /// <summary>Forms a translation must provide (CLDR always requires Other).</summary>
    public static IReadOnlySet<PluralCategory> Required(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "zh" or "ja" or "vi" or "id" => new HashSet<PluralCategory> { PluralCategory.Other },
        "fr" or "pt" or "es" => new HashSet<PluralCategory> { PluralCategory.One, PluralCategory.Many, PluralCategory.Other },
        "ru" => new HashSet<PluralCategory> { PluralCategory.One, PluralCategory.Few, PluralCategory.Many, PluralCategory.Other },
        "ar" => new HashSet<PluralCategory>(Enum.GetValues<PluralCategory>()),
        _ => new HashSet<PluralCategory> { PluralCategory.One, PluralCategory.Other },
    };
}

/// <summary>
/// Resource lookup: text comes from the UI culture, numbers from the regional-format culture.
/// Plural keys are "Key.one", "Key.other", … following CLDR category names.
/// </summary>
public sealed class Localizer(ResourceManager resources)
{
    public static Localizer Default { get; } =
        new(new ResourceManager("ConnectionClue.Presentation.Resources.Strings", typeof(Localizer).Assembly));

    public ResourceManager Resources => resources;

    public string Get(string key, CultureInfo? ui = null) =>
        resources.GetString(key, ui ?? CultureInfo.CurrentUICulture) ?? throw new MissingManifestResourceException(key);

    /// <summary>The text for an optional key (for example a situation-specific variant), or null when there is none.</summary>
    public string? Find(string key, CultureInfo? ui = null) => resources.GetString(key, ui ?? CultureInfo.CurrentUICulture);

    public string Plural(string key, long count, CultureInfo? ui = null, CultureInfo? format = null)
    {
        ui ??= CultureInfo.CurrentUICulture;
        var category = PluralRules.Select(ui, count);
        var pattern = resources.GetString($"{key}.{Suffix(category)}", ui) ?? Get($"{key}.other", ui);
        return string.Format(format ?? CultureInfo.CurrentCulture, pattern, count);
    }

    public static string Suffix(PluralCategory category) => category.ToString().ToLowerInvariant();
}

/// <summary>
/// Pseudo-localization for UI tests: accents, about 40 % expansion and brackets expose hard-coded strings,
/// truncation and clipping. Placeholders such as {0} are preserved.
/// </summary>
public static class PseudoLocalizer
{
    private const string Plain = "aceinorsuyACEINORSUY", Accented = "àçéîñöřšüýÀÇÉÎÑÖŘŠÜÝ";

    public static string Transform(string text)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && text.IndexOf('}', i) is var end and > 0)
            {
                sb.Append(text, i, end - i + 1);
                i = end;
                continue;
            }
            int k = Plain.IndexOf(text[i]);
            sb.Append(k >= 0 ? Accented[k] : text[i]);
        }
        int padding = (int)Math.Ceiling(text.Length * 0.4);
        return sb.Append(' ').Append('~', padding).Append(']').ToString();
    }
}
