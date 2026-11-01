using ConnectionClue.Analysis;
using ConnectionClue.Windows;

namespace ConnectionClue.App;

/// <summary>Turns what the inspector read into the facts the advisor judges.</summary>
internal static class SystemFactsMapper
{
    public static SystemFacts ToFacts(SystemSnapshot s, CheckContext context)
    {
        string? dns = s.DnsServers is { Count: > 0 } servers ? servers[0] : null;
        return new(
            s.Medium is null ? null : new AdapterFacts(context.Medium, s.LinkMbps, s.DriverDate, s.PowerOffAllowed, s.EnergyEfficientEthernet,
                s.SignalBars, s.IsUsb, s.UsbSelectiveSuspend, s.AdapterName, s.DriverVersion, s.DriverProvider, s.SignalDbm, s.WifiChannel,
                s.WifiRetryPercent, s.WifiFrames, s.WifiEncryption, Settings(s.DriverSettings), s.Mtu, s.PacketErrors, s.Packets, s.InterfaceAlias),
            context.Metered, s.ProxyConfigured, context.TunnelSuspected, s.Ipv6Disabled, s.TcpAutoTuningLimited, s.OnBattery,
            s.PowerSaverActive, s.WifiPowerSavingOnBattery, s.WifiPowerSavingOnAc,
            NetworkName: s.NetworkName, DnsServer: dns, DnsIsRouter: dns is not null && dns == context.Gateway, ProxyAddress: s.ProxyAddress,
            EnergySaverOn: s.EnergySaverOn, BestEfficiency: s.BestEfficiencyMode, TcpAutoTuningLevel: s.TcpAutoTuningLevel,
            Ipv6DisabledComponents: s.Ipv6DisabledComponents, Ipv6UnboundOnAdapter: s.Ipv6UnboundOnAdapter, TunnelName: s.TunnelName,
            TcpTweaks: s.TcpTweaks, Configuration: s.Configuration);
    }

    private static IReadOnlyList<DriverSetting>? Settings(IReadOnlyList<DriverSettingValue>? values) =>
        values is null ? null : [.. values.Select(v => new DriverSetting(v.Keyword, v.Label, v.Value, v.Text, v.DefaultValue, v.DefaultText,
            [.. v.Options.Select(o => new DriverOption(o.Value, o.Text))]))];
}
