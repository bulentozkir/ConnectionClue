using ConnectionClue.Core;
using static ConnectionClue.Windows.IntegrationTests.Fx;

namespace ConnectionClue.Windows.IntegrationTests;

internal sealed class FakeNode(Dictionary<string, object?>? values = null, Dictionary<string, FakeNode>? children = null, string[]? denied = null) : IRegistryNode
{
    private readonly Dictionary<string, object?> _values = values ?? [];
    private readonly Dictionary<string, FakeNode> _children = children ?? [];

    public IReadOnlyList<string> SubKeyNames => [.. _children.Keys];

    public IReadOnlyList<string> ValueNames => [.. _values.Keys];

    public IRegistryNode? OpenSubKey(string path)
    {
        FakeNode? node = this;
        foreach (var part in path.Split('\\'))
        {
            if (node is null) return null;
            if (denied?.Contains(part) == true) throw new UnauthorizedAccessException(part);
            if (!node._children.TryGetValue(part, out node)) return null;
        }
        return node;
    }

    public object? GetValue(string name) => _values.GetValueOrDefault(name);

    public void Dispose()
    {
    }
}

public class DriverSettingsReaderTests
{
    // A setting as an INF installs it: a label, an optional default, and the choices under "enum".
    private static FakeNode Param(string? label, string? defaultValue = null, string type = "enum", params (string Value, string Text)[] options) =>
        new(new() { ["ParamDesc"] = label, ["type"] = type, ["default"] = defaultValue },
            options.Length == 0 ? null : new() { ["enum"] = new(options.ToDictionary(o => o.Value, o => (object?)o.Text)) });

    private static FakeNode Adapter(Dictionary<string, object?> values, Dictionary<string, FakeNode> settings, string[]? denied = null) =>
        new(values, new() { ["Ndi"] = new(children: new() { ["params"] = new(children: settings, denied: denied) }) });

    [Fact]
    public void Each_setting_is_read_the_way_device_manager_shows_it()
    {
        using var adapter = Adapter(
            new() { ["*SpeedDuplex"] = "4", ["RoamAggressiveness"] = 4, ["NetworkAddress"] = "AABBCCDDEEFF" },
            new()
            {
                ["*SpeedDuplex"] = Param("Speed & Duplex", "0", options: [("0", "Auto Negotiation"), ("4", "100 Mbps Full Duplex")]),
                ["RoamAggressiveness"] = Param("Roaming Aggressiveness", "2", options: [("2", "3. Medium"), ("4", "5. Highest")]),
                ["*JumboPacket"] = Param("Jumbo Packet", "1514", options: [("1514", "Disabled"), ("9014", "9014 Bytes")]),
                ["NetworkAddress"] = Param("Network Address", null, "edit"),
            });

        var settings = DriverSettingsReader.Read(adapter).ToDictionary(s => s.Keyword);
        Assert.Equal(4, settings.Count);

        var speed = settings["*SpeedDuplex"];
        Assert.Equal(("Speed & Duplex", "4", "100 Mbps Full Duplex", "0", "Auto Negotiation"), (speed.Label, speed.Value, speed.Text, speed.DefaultValue, speed.DefaultText));
        Assert.Equal(["0", "4"], speed.Options.Select(o => o.Value).Order());

        var roaming = settings["RoamAggressiveness"]; // a REG_DWORD value reads the same as a REG_SZ one
        Assert.Equal(("4", "5. Highest", "2", "3. Medium"), (roaming.Value, roaming.Text, roaming.DefaultValue, roaming.DefaultText));

        var jumbo = settings["*JumboPacket"]; // never stored: the driver default applies, exactly as Device Manager shows it
        Assert.Equal(("1514", "Disabled", "1514"), (jumbo.Value, jumbo.Text, jumbo.DefaultValue));

        var address = settings["NetworkAddress"]; // free text has no choices: the value is its own text
        Assert.Equal(("AABBCCDDEEFF", "AABBCCDDEEFF", (string?)null), (address.Value, address.Text, address.DefaultValue));
        Assert.Empty(address.Options);
    }

    [Fact]
    public void Internal_settings_without_a_label_or_without_any_value_are_left_out()
    {
        using var adapter = Adapter(
            new() { ["Hidden"] = "1", ["Blank"] = "1", ["Binary"] = new byte[] { 1, 2 } },
            new()
            {
                ["Hidden"] = Param(null, "0"),
                ["Blank"] = Param("", "0"),
                ["Nothing"] = Param("Nothing stored, no default"),
                ["Binary"] = Param("Binary value", null, "edit"),
                ["Kept"] = Param("Kept", "1"),
            });
        Assert.Equal(["Kept"], DriverSettingsReader.Read(adapter).Select(s => s.Keyword));
    }

    [Fact]
    public void Choices_match_the_stored_value_whatever_its_case()
    {
        using var adapter = Adapter(new() { ["Mode"] = "0x1a" }, new() { ["Mode"] = Param("Mode", "0X0F", options: [("0x1A", "Fast"), ("0X0f", "Safe")]) });
        var mode = Assert.Single(DriverSettingsReader.Read(adapter));
        Assert.Equal(("Fast", "Safe"), (mode.Text, mode.DefaultText));
    }

    [Fact]
    public void An_adapter_without_settings_has_none()
    {
        using var plain = new FakeNode(new() { ["DriverDesc"] = "x" });
        Assert.Empty(DriverSettingsReader.Read(plain));
    }

    [Fact]
    public void A_subkey_a_standard_user_may_not_open_is_not_a_setting()
    {
        using var adapter = Adapter(new() { ["Open"] = "1" }, new() { ["Open"] = Param("Open", "1"), ["Locked"] = new() }, denied: ["Locked"]);
        Assert.Equal(["Open"], DriverSettingsReader.Read(adapter).Select(s => s.Keyword));
    }
    [Fact]
    public void The_registry_wrapper_reads_this_pcs_adapter_class_the_same_way()
    {
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        Assert.NotNull(root);
        using var node = new RegistryNode(root);
        Assert.NotEmpty(node.SubKeyNames);
        Assert.Null(node.OpenSubKey("no\\such\\key"));
    }
}

public class WlanStatisticsTests
{
    // WLAN_STATISTICS as the driver on a real PC returned it: 1088 bytes, six PHY blocks, the counters in the first one.
    private static byte[] Stats(params (ulong Sent, ulong Failed, ulong Retried)[] phys)
    {
        var data = new byte[224 + 144 * phys.Length];
        BitConverter.TryWriteBytes(data.AsSpan(216), (uint)phys.Length);
        for (int i = 0; i < phys.Length; i++)
        {
            int block = 224 + 144 * i;
            BitConverter.TryWriteBytes(data.AsSpan(block), phys[i].Sent);
            BitConverter.TryWriteBytes(data.AsSpan(block + 16), phys[i].Failed);
            BitConverter.TryWriteBytes(data.AsSpan(block + 24), phys[i].Retried);
        }
        return data;
    }

    [Fact]
    public void Counts_are_summed_over_every_phy_the_driver_reports()
    {
        var real = Stats((611_260, 0, 42_316), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0));
        Assert.Equal(1088, real.Length);
        Assert.Equal(new WifiFrameCounts(611_260, 42_316, 0), WlanInfo.ParseStatistics(real));
        Assert.Equal(new WifiFrameCounts(1500, 150, 4), WlanInfo.ParseStatistics(Stats((1000, 3, 100), (500, 1, 50))));
    }

    [Fact]
    public void A_short_or_empty_report_never_throws_and_incomplete_blocks_are_ignored()
    {
        Assert.Null(WlanInfo.ParseStatistics([]));
        Assert.Null(WlanInfo.ParseStatistics(new byte[100]));
        Assert.Equal(new WifiFrameCounts(0, 0, 0), WlanInfo.ParseStatistics(Stats()));
        var cut = Stats((1000, 3, 100), (500, 1, 50))[..(224 + 144 + 100)]; // the second block is cut off
        Assert.Equal(new WifiFrameCounts(1000, 100, 3), WlanInfo.ParseStatistics(cut));
        var lying = Stats((7, 0, 1));
        BitConverter.TryWriteBytes(lying.AsSpan(216), 1000u); // claims more PHYs than it holds
        Assert.Equal(new WifiFrameCounts(7, 1, 0), WlanInfo.ParseStatistics(lying));
    }

    [Fact]
    public void A_counter_beyond_the_range_of_a_signed_number_is_clamped()
    {
        Assert.Equal(long.MaxValue, WlanInfo.ParseStatistics(Stats((ulong.MaxValue, 0, 0)))!.Sent);
    }
}

public class TcpTweakTests
{
    private static IReadOnlyList<string> Find(params (string Name, object? Value)[] values) =>
        SystemInspector.FindTcpTweaks(name => values.FirstOrDefault(v => v.Name == name).Value);

    [Fact]
    public void Only_values_that_limit_tcp_are_reported()
    {
        Assert.Equal(["EnablePMTUDiscovery=0", "Tcp1323Opts=2", "SackOpts=0", "DisableTaskOffload=1"],
            Find(("EnablePMTUDiscovery", 0), ("Tcp1323Opts", 2), ("SackOpts", 0), ("DisableTaskOffload", 1)));
        Assert.Empty(Find(("EnablePMTUDiscovery", 1), ("Tcp1323Opts", 1), ("SackOpts", 1), ("DisableTaskOffload", 0)));
    }

    [Theory]
    [InlineData(0, true)]   // window scaling and timestamps off
    [InlineData(2, true)]   // timestamps only: window scaling is still off
    [InlineData(1, false)]  // window scaling only: what this PC ships with
    [InlineData(3, false)]
    public void Window_scaling_is_bit_zero_of_tcp1323opts(int value, bool limiting) => Assert.Equal(limiting, Find(("Tcp1323Opts", value)).Count == 1);

    [Fact]
    public void Missing_or_unreadable_values_are_not_a_tweak()
    {
        Assert.Empty(Find());
        Assert.Empty(Find(("SackOpts", null)));
        Assert.Empty(Find(("SackOpts", new byte[] { 0 })));
        Assert.Empty(Find(("SackOpts", "not a number")));
        Assert.Equal(["SackOpts=0"], Find(("SackOpts", "0"))); // a REG_SZ zero counts like a DWORD zero
    }
}

[Collection("SystemInspector")]
public class AdapterReadingTests
{
    [Fact]
    public void The_active_adapters_driver_settings_wifi_link_and_tcp_settings_are_readable_as_a_standard_user()
    {
        var path = RequireDefaultPath();
        var facts = SystemInspector.Inspect(path);
        Assert.NotNull(facts.TcpTweaks); // the Tcpip key is readable on every Windows
        if (path is { IsHardware: true, Medium: InterfaceMedium.Ethernet or InterfaceMedium.WiFi })
        {
            Assert.NotNull(facts.DriverSettings);
            Assert.All(facts.DriverSettings, s =>
            {
                Assert.NotEmpty(s.Keyword);
                Assert.NotEmpty(s.Label);
                Assert.NotEmpty(s.Text);
            });
            Assert.True(facts.Mtu is null or > 0);
            Assert.False(string.IsNullOrWhiteSpace(facts.InterfaceAlias));
        }
        if (path is { IsHardware: true, Medium: InterfaceMedium.WiFi })
        {
            Assert.NotNull(facts.WifiEncryption);
            Assert.True(facts.SignalDbm is null or (>= -120 and <= 0));
            Assert.True(facts.WifiChannel is null or > 0);
            Assert.True(facts.WifiRetryPercent is null or >= 0);
        }
    }

    [Fact]
    public void Scheduled_checks_reuse_the_driver_settings_and_a_check_the_user_starts_reads_them_again()
    {
        var path = RequireDefaultPath();
        if (path is not { IsHardware: true, Medium: InterfaceMedium.Ethernet or InterfaceMedium.WiFi }) Assert.Skip("The default route is not a physical adapter.");
        var fresh = SystemInspector.Inspect(path, reuseSlowFacts: false).DriverSettings;
        Assert.NotNull(fresh);
        Assert.Same(fresh, SystemInspector.Inspect(path, reuseSlowFacts: true).DriverSettings); // held for the next scheduled check
        Assert.NotSame(fresh, SystemInspector.Inspect(path, reuseSlowFacts: false).DriverSettings); // the user's own check reads again
    }

    [Fact]
    public void The_wifi_radio_reports_channel_and_signal_without_the_location_permission()
    {
        var path = RequireDefaultPath();
        if (path is not { IsHardware: true, Medium: InterfaceMedium.WiFi }) Assert.Skip("The default route is not a Wi-Fi adapter.");
        var id = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Select(n => Guid.TryParse(n.Id, out var g) ? g : (Guid?)null)
            .FirstOrDefault(g => g is not null && WlanInfo.ReadLink(g.Value) is not null);
        Assert.NotNull(id);
        var link = WlanInfo.ReadLink(id.Value)!;
        Assert.True(link.Channel is null or > 0);
        Assert.True(link.SignalDbm is null or (>= -120 and <= 0));
    }
}
