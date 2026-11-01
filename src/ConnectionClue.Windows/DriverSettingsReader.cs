using System.Globalization;
using Microsoft.Win32;

namespace ConnectionClue.Windows;

/// <summary>One choice of an advanced driver setting, with the driver's own wording.</summary>
public sealed record DriverOptionValue(string Value, string Text);

/// <summary>One advanced driver setting as the adapter's driver describes it: the registry keyword, the label and value text
/// Device Manager's Advanced tab shows, and the default the driver declares.</summary>
public sealed record DriverSettingValue(string Keyword, string Label, string Value, string Text, string? DefaultValue,
    string? DefaultText, IReadOnlyList<DriverOptionValue> Options);

/// <summary>The few registry reads the settings reader needs, so its parsing can be tested without a registry.</summary>
public interface IRegistryNode : IDisposable
{
    IReadOnlyList<string> SubKeyNames { get; }

    IReadOnlyList<string> ValueNames { get; }

    IRegistryNode? OpenSubKey(string path);

    object? GetValue(string name);
}

public sealed class RegistryNode(RegistryKey key) : IRegistryNode
{
    public IReadOnlyList<string> SubKeyNames => key.GetSubKeyNames();

    public IReadOnlyList<string> ValueNames => key.GetValueNames();

    public IRegistryNode? OpenSubKey(string path) => key.OpenSubKey(path) is { } sub ? new RegistryNode(sub) : null;

    public object? GetValue(string name) => key.GetValue(name);

    public void Dispose() => key.Dispose();
}

/// <summary>
/// Reads every advanced setting a network driver declares under its adapter's Ndi\params key, read-only and as a standard
/// user. Settings without a label are internal and do not appear in Device Manager, so they are left out. A setting the
/// driver never stored a value for is at its default, exactly as Device Manager shows it.
/// </summary>
public static class DriverSettingsReader
{
    public static IReadOnlyList<DriverSettingValue> Read(IRegistryNode adapter)
    {
        using var parameters = adapter.OpenSubKey(@"Ndi\params");
        if (parameters is null) return [];
        var settings = new List<DriverSettingValue>();
        foreach (var keyword in parameters.SubKeyNames)
        {
            using var node = Open(parameters, keyword);
            if (node?.GetValue("ParamDesc") is not string { Length: > 0 } rawLabel) continue;
            string? defaultValue = Text(node.GetValue("default"));
            string? value = Text(adapter.GetValue(keyword)) ?? defaultValue;
            if (value is null) continue;
            var options = Options(node);
            string Display(string v) => options.FirstOrDefault(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase))?.Text ?? v;
            settings.Add(new(keyword, rawLabel.Trim(), value, Display(value), defaultValue, defaultValue is null ? null : Display(defaultValue), options));
        }
        return settings;
    }

    // A subkey a standard user may not open is simply not a setting.
    private static IRegistryNode? Open(IRegistryNode parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static IReadOnlyList<DriverOptionValue> Options(IRegistryNode setting)
    {
        using var choices = Open(setting, "enum");
        if (choices is null) return [];
        return [.. choices.ValueNames.Where(n => n.Length > 0).Select(n => new DriverOptionValue(n.Trim(), Text(choices.GetValue(n)) ?? n))];
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        byte[] or string[] => null,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
