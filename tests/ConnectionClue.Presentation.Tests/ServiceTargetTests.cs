using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class ServiceTargetTests
{
    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("192.0.2.10:3074", "192.0.2.10", 3074)]
    [InlineData("[2001:db8::5]:443", "2001:db8::5", 443)]
    [InlineData("bücher.example:443", "xn--bcher-kva.example", 443)]
    public void Parses_user_provided_host_and_port(string input, string expectedHost, int expectedPort)
    {
        Assert.True(ServiceTargetParser.TryParse(input, out var target));
        Assert.Equal(new ServiceTarget(expectedHost, expectedPort), target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:443;whoami")]
    [InlineData("bad host:443")]
    [InlineData("[not-ip]:443")]
    [InlineData("2001:db8::1:443")]
    [InlineData("example.com:٤٤٣")]
    public void Rejects_ambiguous_or_malformed_targets(string input) =>
        Assert.False(ServiceTargetParser.TryParse(input, out _), input);

    [Fact]
    public void Every_symptom_has_real_services()
    {
        Assert.Equal(["Xbox network", "Steam", "Epic Games"], SymptomServices.For(Symptom.Gaming).Select(s => s.Name));
        Assert.Equal(["Microsoft Teams", "Zoom", "Google Meet"], SymptomServices.For(Symptom.Calls).Select(s => s.Name));
        Assert.Equal(["Netflix", "YouTube", "Twitch"], SymptomServices.For(Symptom.Video).Select(s => s.Name));
        Assert.All(Enum.GetValues<Symptom>(), symptom => Assert.All(SymptomServices.For(symptom), s =>
            Assert.True(ServiceTargetParser.TryParse($"{s.Target.Host}:{s.Target.Port}", out _), s.Name)));
    }

    [Fact]
    public async Task Service_test_takes_the_median_and_stops_retrying_failed_lookups()
    {
        var probe = new ScriptedProbe(new()
        {
            ["fast.example"] = [30, 10, 20],
            ["flaky.example"] = [null, 50, null],
        });
        var results = await SymptomServices.TestAsync(probe, [new("Fast", new("fast.example", 443)), new("Flaky", new("flaky.example", 443)),
            new("Missing", new("missing.example", 443))], TestContext.Current.CancellationToken);

        Assert.Equal((ServiceTargetStatus.Connected, 20.0), (results[0].Status, results[0].MedianMilliseconds!.Value));
        Assert.Equal((ServiceTargetStatus.Connected, 50.0), (results[1].Status, results[1].MedianMilliseconds!.Value));
        Assert.Equal(ServiceTargetStatus.NameLookupFailed, results[2].Status);
        Assert.Equal(1, probe.Calls["missing.example"]);
    }

    [Fact]
    public async Task Service_test_uses_the_chosen_symptom_and_notes_an_invalid_saved_target()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        var time = new FakeTimeProvider();
        var probe = new ScriptedProbe([]) { Default = 25 };
        using var vm = new MainViewModel(Localizer.Default, en, time, (_, _) => throw new NotSupportedException(), new(),
            new SettingsViewModel(Localizer.Default, en, backgroundEnabled: false, callsTarget: "not a target"), new NoStore(),
            serviceTargetProbe: probe);
        vm.SelectedSymptom = vm.Symptoms.Single(s => s.Key == "Symptom_Calls");
        Assert.Equal("Microsoft Teams, Zoom, Google Meet", vm.ServiceNames);

        await vm.TestServicesCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.ServiceResults.Count);
        Assert.Equal("Microsoft Teams (worldaz.tr.teams.microsoft.com:443): 25 ms to connect", vm.ServiceResults[0].AccessibleText);
        Assert.StartsWith("Choppy calls: 3 of 3 services connected. Slowest: Microsoft Teams (25 ms to connect).", vm.ServiceSummary, StringComparison.Ordinal);
        Assert.EndsWith("Enter a valid host:port, such as teams.microsoft.com:443.", vm.ServiceSummary, StringComparison.Ordinal);
    }

    private sealed class ScriptedProbe(Dictionary<string, double?[]> script) : IServiceTargetProbe
    {
        public Dictionary<string, int> Calls { get; } = [];
        public double? Default { get; init; }

        public Task<ServiceTargetResult> ProbeAsync(ServiceTarget target, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                int call = Calls[target.Host] = Calls.GetValueOrDefault(target.Host) + 1;
                double? ms = script.TryGetValue(target.Host, out var times) ? times[call - 1] : Default;
                var status = ms is not null ? ServiceTargetStatus.Connected
                    : script.ContainsKey(target.Host) ? ServiceTargetStatus.TimedOut : ServiceTargetStatus.NameLookupFailed;
                return Task.FromResult(new ServiceTargetResult(target, status, ms));
            }
        }
    }

    private sealed class NoStore : IResultStore
    {
        public SavedResult? Load() => null;
        public void Save(SavedResult result) { }
        public void Clear() { }
    }
}
