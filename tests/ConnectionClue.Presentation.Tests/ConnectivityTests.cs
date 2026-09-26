using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class ConnectivityTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private sealed class NoStore : IResultStore
    {
        public int Saves { get; private set; }
        public SavedResult? Load() => null;
        public void Save(SavedResult result) => Saves++;
        public void Clear() { }
    }

    private sealed class FakeNetworkDiagnostics : INetworkDiagnostics
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<TraceHop>> TraceRouteAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<TraceHop>>([new(1, "192.0.2.1", 2, 2, 3, null, NetworkToolStatus.Responded)]);
        }

        public Task<IReadOnlyList<DnsResolverMeasurement>> ComparePublicDnsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DnsResolverMeasurement>>(
                [new("", "192.168.1.1", 40, 3, NetworkToolStatus.Success, IsCurrent: true), new("Cloudflare", "1.1.1.1", 15, 3, NetworkToolStatus.Success)]);
        }

        public List<string?> Switched { get; } = [];

        public Task<DnsSwitchResult> SetDnsAsync(string? provider, CancellationToken cancellationToken)
        {
            Switched.Add(provider);
            return Task.FromResult(provider is null ? DnsSwitchResult.Restored : DnsSwitchResult.Switched);
        }

        public Task<WifiScanResult> ScanWifiAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WifiScanResult(WifiScanStatus.Success, [new("2.4", 1, 2, true)]));
        }

        public void OpenNetworkSettings() => Calls++;
    }

    private static (MainViewModel Vm, NoStore Store, List<Announcement> Said) Create(Func<bool> connected)
    {
        var store = new NoStore();
        // Any attempt to start a session would throw: nothing may run without a network.
        var vm = new MainViewModel(Localizer.Default, En, new FakeTimeProvider(), (_, _) => throw new InvalidOperationException("probes started"),
            new(), new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false), store, isConnected: connected);
        var said = new List<Announcement>();
        vm.Announce += (_, a) => said.Add(a);
        return (vm, store, said);
    }

    [Fact]
    public async Task No_check_starts_without_a_network_and_a_warning_is_shown()
    {
        var (vm, store, said) = Create(() => false);
        using (vm)
        {
            await vm.QuickCheckCommand.ExecuteAsync(null);
            await vm.RunCheckAsync(measureSpeed: false); // background tick
            Assert.True(vm is { IsDisconnected: true, IsRunning: false, Hero: HeroState.Disconnected, HeroTitle: "No network connection" });
            Assert.StartsWith("Connect to Wi-Fi or plug in a network cable.", vm.Summary, StringComparison.Ordinal);
            Assert.Null(vm.LastLevel);
            Assert.Equal(0, store.Saves);
            Assert.Single(said); // the manual attempt is announced; the background tick stays quiet
        }
    }

    [Fact]
    public void Network_changes_switch_the_warning_on_and_off()
    {
        bool online = true;
        var (vm, _, said) = Create(() => online);
        using (vm)
        {
            vm.UpdateConnectivity(false);
            Assert.Equal(HeroState.Disconnected, vm.Hero);
            vm.UpdateConnectivity(false); // repeated events do not repeat the announcement
            Assert.Single(said);
            vm.UpdateConnectivity(true);
            Assert.True(vm is { IsDisconnected: false, Hero: HeroState.Idle, HeroTitle: "Check your connection" });
        }
    }

    [Fact]
    public async Task Network_tools_are_gated_offline_and_callable_when_connected()
    {
        bool connected = false;
        var tools = new FakeNetworkDiagnostics();
        var vm = new MainViewModel(Localizer.Default, En, new FakeTimeProvider(), (_, _) => throw new InvalidOperationException(),
            new(), new SettingsViewModel(Localizer.Default, En, backgroundEnabled: false), new NoStore(),
            isConnected: () => connected, networkDiagnostics: tools);
        using (vm)
        {
            await vm.TraceRouteCommand.ExecuteAsync(null);
            await vm.ComparePublicDnsCommand.ExecuteAsync(null);
            await vm.ScanWifiCommand.ExecuteAsync(null);
            Assert.Equal(0, tools.Calls);
            Assert.True(vm.IsDisconnected);

            connected = true;
            vm.UpdateConnectivity(connected: true);
            await vm.TraceRouteCommand.ExecuteAsync(null);
            await vm.ComparePublicDnsCommand.ExecuteAsync(null);
            await vm.ScanWifiCommand.ExecuteAsync(null);
            Assert.Equal(3, tools.Calls);
            Assert.Contains("Hop 1", vm.TraceRouteText, StringComparison.Ordinal);
            Assert.StartsWith("No hop adds lasting delay", vm.TraceRouteText, StringComparison.Ordinal);
            Assert.StartsWith("Cloudflare answered fastest (15 ms), clearly faster than your current DNS.", vm.DnsComparisonText, StringComparison.Ordinal);
            Assert.Contains("Your current DNS (192.168.1.1): 40 ms median", vm.DnsComparisonText, StringComparison.Ordinal);
            Assert.Contains("2.4", vm.WifiAnalysisText, StringComparison.Ordinal);

            Assert.Equal("Use Cloudflare DNS", vm.SwitchDnsLabel);
            await vm.SwitchToFastestDnsCommand.ExecuteAsync(null);
            Assert.StartsWith("This PC now uses Cloudflare DNS", vm.DnsStatus, StringComparison.Ordinal);
            Assert.Equal("Use fastest DNS", vm.SwitchDnsLabel); // the old comparison no longer applies
            Assert.False(vm.SwitchToFastestDnsCommand.CanExecute(null));
            await vm.RestoreAutomaticDnsCommand.ExecuteAsync(null);
            Assert.Equal(["Cloudflare", null], tools.Switched);
            Assert.StartsWith("DNS is automatic again", vm.DnsStatus, StringComparison.Ordinal);
        }
    }
}
