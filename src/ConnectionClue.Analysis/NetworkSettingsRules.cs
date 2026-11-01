using ConnectionClue.Core;

namespace ConnectionClue.Analysis;

/// <summary>Only known, reversible tuning tradeoffs produce advice. Inventory values without a rule are not called optimal.</summary>
public static class NetworkSettingsRules
{
    private static readonly string[] OffloadKeywords =
    [
        "*IPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv6",
        "*UDPChecksumOffloadIPv4", "*UDPChecksumOffloadIPv6", "*LsoV2IPv4", "*LsoV2IPv6", "*RscIPv4", "*RscIPv6",
    ];

    public static bool HasDriverTuningOverride(AdapterFacts adapter) => adapter.Settings?.Any(s =>
        s.Keyword.Equals("*RSS", StringComparison.OrdinalIgnoreCase) && s.Number == 0 && s.DefaultNumber == 1
        || OffloadKeywords.Contains(s.Keyword, StringComparer.OrdinalIgnoreCase) && s.Number == 0 && s.DefaultNumber > 0) == true;

    public static IReadOnlyList<Advisory> Evaluate(AdvisorInput input)
    {
        var f = input.Facts;
        var a = f.Adapter;
        var list = new List<Advisory>();
        string? connection = a?.Alias ?? a?.Name;
        IReadOnlyList<string>? connectionArgs = string.IsNullOrWhiteSpace(connection) ? null : [connection];
        bool problems = input.Report.Issues.Count > 0;
        bool localDelay = input.Report.Issues.Any(i => i.Location == IssueLocation.LocalNetwork
            && i.Kind is IssueKind.Delay or IssueKind.Variation);
        bool transfer = input.Symptom == Symptom.Video || problems && input.Symptom is not (Symptom.Gaming or Symptom.Calls);
        var settings = a?.Settings ?? [];
        DriverSetting? Setting(string keyword) => settings.FirstOrDefault(s => s.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));

        // Hardware support must be demonstrated by the driver's declared option. Global RSS=0 alone does not prove support.
        if (!f.TunnelSuspected && transfer && a is { Medium: ConnectionMedium.Ethernet, LinkMbps: >= 1000 }
            && Setting("*RSS") is { Number: 0 or 1, DefaultNumber: 1 } rss && rss.OptionText("1") is { } enabled)
        {
            var global = Global(f.Configuration, "ReceiveSideScaling");
            if (rss.Number == 0 || global == "0")
                list.Add(new(AdvisoryCode.ReceiveScalingDisabled, AdvisoryLevel.Suggestion,
                    Args: [a.Name ?? a.Alias ?? "", rss.Label, rss.Text, enabled], Variant: global == "0" ? "Global" : null));
        }
        if (!f.TunnelSuspected && transfer && a is { Medium: ConnectionMedium.Ethernet })
        {
            bool globalOff = Global(f.Configuration, "TaskOffload") == "0";
            bool supported = settings.Any(s => OffloadKeywords.Contains(s.Keyword, StringComparer.OrdinalIgnoreCase)
                && s.DefaultNumber > 0 && s.DefaultValue is { } value && s.OptionText(value) is not null);
            var disabled = settings.Where(s => OffloadKeywords.Contains(s.Keyword, StringComparer.OrdinalIgnoreCase)
                && s.Number == 0 && s.DefaultNumber > 0 && s.DefaultValue is { } value && s.OptionText(value) is not null).ToList();
            // One card for related offloads, rather than nine near-identical cards or a blanket reset.
            if ((disabled.Count > 0 || globalOff && supported)
                && f.TcpTweaks?.Any(t => t.StartsWith("DisableTaskOffload=", StringComparison.OrdinalIgnoreCase)) != true)
                list.Add(new(AdvisoryCode.AdapterOffloadDisabled, AdvisoryLevel.Suggestion,
                    Args: [a.Name ?? a.Alias ?? "", string.Join("; ", disabled.Select(s => $"{s.Label}: {s.Text} -> {s.DefaultText}"))],
                    Variant: globalOff ? "Global" : null));
        }
        if (!f.TunnelSuspected && localDelay && input.Symptom is Symptom.Gaming or Symptom.Calls
            && a?.Medium == ConnectionMedium.Ethernet && Setting("*InterruptModeration") is { Number: 1 } moderation
            && moderation.OptionText("0") is { } off)
            list.Add(new(AdvisoryCode.InterruptModeration, AdvisoryLevel.Suggestion,
                Args: [a.Name ?? a.Alias ?? "", moderation.Label, moderation.Text, off]));

        if (f.Configuration is { } config)
        {
            if (!f.TunnelSuspected && config is { DhcpEnabled: true, LinkLocalAddressOnly: true })
                list.Add(new(AdvisoryCode.LinkLocalAddress, AdvisoryLevel.Important, Args: connectionArgs));
            else if (!f.TunnelSuspected && problems && (config.DhcpEnabled == false
                || config.StaticDns == true && input.DnsMedianMs > ConfigurationAdvisor.SlowDnsMs))
                list.Add(new(AdvisoryCode.StaticNetworkConfiguration, AdvisoryLevel.Suggestion,
                    Args: connectionArgs, Variant: connectionArgs is null ? null : config.DhcpEnabled == false ? "Address" : "Dns"));
            if (config.Unavailable is { Count: > 0 } unavailable)
                list.Add(new(AdvisoryCode.NetworkSettingsUnavailable, AdvisoryLevel.Suggestion,
                    Args: [string.Join(", ", unavailable)]));
        }
        return list;
    }

    private static string? Global(NetworkConfiguration? configuration, string property) =>
        configuration?.Sources.FirstOrDefault(s => s.Name == "MSFT_NetOffloadGlobalSetting" && s.Error is null)
            ?.Rows.FirstOrDefault()?.GetValueOrDefault(property);
}
