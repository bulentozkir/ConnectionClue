using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Data;
using ConnectionClue.Presentation.History;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.Review;
using ConnectionClue.Presentation.ViewModels;

namespace ConnectionClue.App;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses a text element when its string is empty.</summary>
public sealed class TextToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Two-way enum ↔ radio button: checked when the value equals the parameter name.</summary>
public sealed class EnumIs : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>True when both values are the same item: a radio button's own item and the selected one.</summary>
public sealed class SameItem : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// User settings, stored as plain JSON in the per-user app data folder (not encrypted, by design). SettingsVersion tells
/// files written before a new default existed (0 when missing) from later ones, where an empty value is the user's choice.
/// </summary>
internal sealed record AppSettings(
    string? Language = null,
    bool BackgroundEnabled = true,
    int IntervalMinutes = 15,
    int DelayLimitMs = 100,
    int LossLimitPercent = 2,
    int VariationLimitMs = 30,
    int CheckSeconds = 30,
    bool MeasureSpeed = true,
    bool AiReview = true,
    ConnectionClue.Presentation.Theming.AppTheme Theme = ConnectionClue.Presentation.Theming.AppTheme.Dark,
    bool StartWithWindows = false,
    double PlanDownloadMbps = 0,
    double PlanUploadMbps = 0,
    string GamingTarget = "",
    string VideoTarget = "",
    string CallsTarget = "",
    string DisconnectTarget = "",
    int LongCaptureMinutes = SettingsViewModel.DefaultLongCaptureMinutes,
    bool BackgroundOnMobileEnabled = false,
    int SettingsVersion = 0)
{
    /// <summary>2: symptom targets have defaults, and Capture longer defaults to 15 minutes instead of 1 hour.</summary>
    public const int CurrentVersion = 2;
}

internal static class AppData
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectionClue");

    public static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static T? Read<T>(string name)
    {
        try
        {
            string path = Path.Combine(Folder, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return default; // unreadable or from an incompatible version: start fresh
        }
    }

    /// <summary>Temp file + replace, so a crash never leaves a half-written file.</summary>
    public static void Write<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, name), temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a preference or a saved result must not break a check.
        }
    }

    public static void Delete(string name)
    {
        try { File.Delete(Path.Combine(Folder, name)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

internal static class SettingsStore
{
    public static AppSettings Load() => AppData.Read<AppSettings>("settings.json") ?? new();

    public static void Save(AppSettings settings) => AppData.Write("settings.json", settings);
}

/// <summary>Last check with issues (codes, values and time), so recommendations survive a restart.</summary>
internal sealed class ResultStore : IResultStore
{
    private const string Name = "last-result.json";

    public SavedResult? Load() => AppData.Read<SavedResult>(Name);

    public void Save(SavedResult result) => AppData.Write(Name, result);

    public void Clear() => AppData.Delete(Name);
}

/// <summary>Local sampled check summaries only; no adapter, network, endpoint, or user identifiers.</summary>
internal sealed class CheckHistoryStore : ICheckHistoryStore
{
    private const string Name = "check-history.json";

    public IReadOnlyList<CheckHistoryEntry> Load() => AppData.Read<CheckHistoryEntry[]>(Name) ?? [];

    public void Save(IReadOnlyList<CheckHistoryEntry> entries) => AppData.Write(Name, entries.ToArray());
}

/// <summary>Online review verdicts by text hash (plain JSON, like everything else the app stores).</summary>
internal sealed class ReviewCacheStore : IReviewCache
{
    private const string Name = "review-cache.json";

    public IReadOnlyDictionary<string, ReviewVerdict> Load() => AppData.Read<Dictionary<string, ReviewVerdict>>(Name) ?? [];

    public void Save(IReadOnlyDictionary<string, ReviewVerdict> verdicts) => AppData.Write(Name, verdicts);
}

/// <summary>
/// The online LLMs that review recommendations, tried in order. Defaults are free, anonymous and documented as such:
/// LLM7.io (fast default model), the Pollinations legacy anonymous text API, and LLM7.io's GLM model as a slower last
/// resort, so two independent services back each other up. Free services change, so reviewers.json in the app data
/// folder, if present, replaces the list (for example with keyed free tiers such as Groq, OpenRouter or Gemini).
/// </summary>
internal static class Reviewers
{
    public static readonly ReviewerEndpoint[] Defaults =
    [
        new("LLM7.io", new Uri("https://api.llm7.io/v1/chat/completions"), "default", "unused", 30),
        new("Pollinations", new Uri("https://text.pollinations.ai/openai"), "openai-fast", null, 30),
        new("LLM7.io GLM", new Uri("https://api.llm7.io/v1/chat/completions"), "GLM-5.3-Flash", "unused", 45),
    ];

    public static IReadOnlyList<ReviewerEndpoint> Load() =>
        AppData.Read<ReviewerEndpoint[]>("reviewers.json") is { Length: > 0 } custom
            ? [.. custom.Where(r => r.Endpoint.Scheme == Uri.UriSchemeHttps)] // never send text over plain HTTP
            : Defaults;
}
