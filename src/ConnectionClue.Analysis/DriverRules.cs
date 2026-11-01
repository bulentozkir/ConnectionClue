using System.Globalization;
using System.Text.RegularExpressions;

namespace ConnectionClue.Analysis;

/// <summary>The Wi-Fi band in use, taken from the channel number. Channels 1-13 and the upper 5 GHz channels share numbers with
/// 6 GHz, so a 6 GHz link can read as 2.4 or 5 GHz; the advice that uses the band only fires for slow links, which 6 GHz never is.</summary>
public enum WifiBand { Unknown, Ghz24, Ghz5, Ghz6 }

/// <summary>One choice of a driver setting that lists its values, as the driver words it.</summary>
public sealed record DriverOption(string Value, string Text);

/// <summary>
/// One advanced driver setting exactly as Device Manager's Advanced tab shows it. Keyword is the registry name (the same in
/// every language); Label, Text and DefaultText are the driver's own wording, so advice can name what the user sees.
/// </summary>
public sealed record DriverSetting(string Keyword, string Label, string Value, string Text, string? DefaultValue = null,
    string? DefaultText = null, IReadOnlyList<DriverOption>? Options = null)
{
    public int? Number => ToNumber(Value);

    public int? DefaultNumber => ToNumber(DefaultValue);

    /// <summary>The driver's text for the option with this value.</summary>
    public string? OptionText(string value) =>
        Options?.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))?.Text;

    /// <summary>The driver's text for the option with the lowest number, for example "1. Lowest".</summary>
    public string? LowestOptionText => Options?.Select(o => (Number: ToNumber(o.Value), o.Text))
        .Where(o => o.Number is not null).OrderBy(o => o.Number).Select(o => o.Text).FirstOrDefault();

    private static int? ToNumber(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}

public enum DriverFindingKind { WifiMode, WifiWidth, WifiMimo, WifiTxPower, WifiPreferredBand, Roaming, SpeedDuplex, Jumbo, GreenEthernet }

/// <summary>A driver setting that differs from what works well. Recommended is the driver's own text for the value to go
/// back to; Alternative is a stronger choice (for roaming, the lowest level).</summary>
public sealed record DriverFinding(DriverFindingKind Kind, DriverSetting Setting, string Recommended, string? Variant = null,
    string? Alternative = null);

/// <summary>
/// Reads the advanced driver settings for known causes of slow or unstable links. A setting is judged against the default
/// the driver itself declares, so a value an OEM or the user chose on purpose is only called out when it is the known
/// cause; keywords the rules do not know are ignored. Wi-Fi rules cover the Intel keywords, which are the same across Intel's
/// adapters; wired rules use the standardized keywords every vendor shares.
/// </summary>
public static partial class DriverRules
{
    public const int StandardFrame = 1514;

    private static readonly string[] GreenKeywords = ["EnableGreenEthernet", "GreenEthernet", "AdvancedEEE", "ULPMode", "PowerSavingMode"];

    public static WifiBand BandOf(int? channel) => channel switch
    {
        > 0 and <= 14 => WifiBand.Ghz24,
        >= 32 and <= 177 => WifiBand.Ghz5,
        > 177 and <= 233 => WifiBand.Ghz6,
        _ => WifiBand.Unknown,
    };

    public static string? BandLabel(WifiBand band) => band switch
    {
        WifiBand.Ghz24 => "2.4 GHz",
        WifiBand.Ghz5 => "5 GHz",
        WifiBand.Ghz6 => "6 GHz",
        _ => null,
    };

    public static IReadOnlyList<DriverFinding> Find(AdapterFacts? adapter)
    {
        if (adapter?.Settings is not { Count: > 0 } settings) return [];
        DriverSetting? Get(string keyword) => settings.FirstOrDefault(s => s.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        var found = new List<DriverFinding>();
        void Add(DriverFindingKind kind, DriverSetting setting, string? recommended, string? variant = null, string? alternative = null) =>
            found.Add(new(kind, setting, recommended ?? "", variant, alternative));

        if (adapter.Medium == ConnectionMedium.WiFi)
        {
            var band = BandOf(adapter.WifiChannel);
            if (Get("IEEE11nMode") is { Number: { } mode, DefaultNumber: { } modeDefault } m && mode < modeDefault)
                Add(DriverFindingKind.WifiMode, m, m.DefaultText, "Mode");
            // A 20 MHz cap loses speed on 5 and 6 GHz; on the crowded 2.4 GHz band it is often the right choice.
            string? widthKeyword = band switch { WifiBand.Ghz5 => "ChannelWidth52", WifiBand.Ghz6 => "ChannelWidth6", _ => null };
            if (widthKeyword is not null && Get(widthKeyword) is { Number: 0, DefaultNumber: > 0 } width)
                Add(DriverFindingKind.WifiWidth, width, width.DefaultText, "Width");
            // Static and dynamic spatial-multiplexing power save keep the adapter on fewer antennas.
            if (Get("MIMOPowerSaveMode") is { Number: 1 or 2, DefaultNumber: 0 } mimo)
                Add(DriverFindingKind.WifiMimo, mimo, mimo.DefaultText, "Mimo");
            if (Get("IbssTxPower") is { Number: { } power, DefaultNumber: { } powerDefault } tx && power < powerDefault)
                Add(DriverFindingKind.WifiTxPower, tx, tx.DefaultText, "TxPower");
            if (band != WifiBand.Ghz5 && band != WifiBand.Ghz6 && Get("RoamingPreferredBandType") is { Number: 1 } preferred && preferred.DefaultNumber != 1)
                Add(DriverFindingKind.WifiPreferredBand, preferred, preferred.DefaultText, "Band");
            if (Get("RoamAggressiveness") is { Number: { } roam, DefaultNumber: { } roamDefault } r && roam > roamDefault)
                Add(DriverFindingKind.Roaming, r, r.DefaultText, alternative: r.LowestOptionText);
        }
        else if (adapter.Medium == ConnectionMedium.Ethernet)
        {
            // 0 is Auto Negotiation in every driver; anything else is a speed or duplex typed in by hand.
            if (Get("*SpeedDuplex") is { Number: not null and not 0, DefaultNumber: 0 } speed)
                Add(DriverFindingKind.SpeedDuplex, speed, speed.DefaultText, IsHalfDuplex(speed) ? "Half" : null);
            if (Get("*JumboPacket") is { Number: > StandardFrame } jumbo)
                Add(DriverFindingKind.Jumbo, jumbo, jumbo.DefaultText ?? jumbo.OptionText(StandardFrame.ToString(CultureInfo.InvariantCulture)));
            foreach (var keyword in GreenKeywords)
                if (Get(keyword) is { Number: > 0 } green)
                {
                    Add(DriverFindingKind.GreenEthernet, green, green.OptionText("0") ?? "0");
                    break;
                }
        }
        return found;
    }

    /// <summary>Half duplex is 1 (10 Mbps) or 3 (100 Mbps) in the standardized *SpeedDuplex values; the text covers drivers that add their own.</summary>
    public static bool IsHalfDuplex(DriverSetting speedDuplex) =>
        speedDuplex.Number is 1 or 3 || speedDuplex.Text.Contains("half", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a forced *SpeedDuplex value names 100 Mbps or less (values 1-4, or a speed in the text).</summary>
    public static bool IsForcedSlow(DriverSetting speedDuplex) =>
        speedDuplex.Number is >= 1 and <= 4 || SpeedInText(speedDuplex.Text) is <= 100;

    private static double? SpeedInText(string text)
    {
        var m = SpeedPattern().Match(text);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        return m.Groups[2].Value.StartsWith('G') ? n * 1000 : n;
    }

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(Mbps|Mb/s|Gbps|Gb/s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedPattern();
}
