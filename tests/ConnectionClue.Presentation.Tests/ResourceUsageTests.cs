using System.Globalization;
using ConnectionClue.Analysis;
using ConnectionClue.Core;
using ConnectionClue.Presentation.Localization;
using ConnectionClue.Presentation.Results;
using ConnectionClue.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class ResourceUsageTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Hidden_charts_retain_evidence_without_rebuilding_presentation()
    {
        var time = new FakeTimeProvider();
        var lane = new LaneViewModel("Lane_Internet", "", ProbeKind.Tcp, Localizer.Default, En, time);
        lane.SetPresentationEnabled(false);
        int version = lane.Version;
        for (int i = 0; i < 2000; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            lane.Record(Observation(), i);
        }
        Assert.Equal(2000, lane.Samples.Count);
        Assert.Equal(version, lane.Version);
        lane.SetPresentationEnabled(true);
        Assert.Equal("2000/2000", lane.CountText);
        Assert.Contains("2000", lane.StatsText, StringComparison.Ordinal);
        Assert.True(lane.Version > version);
    }

    [Fact]
    public void Interactive_chart_updates_are_throttled_but_the_finished_statistics_are_exact()
    {
        var time = new FakeTimeProvider();
        var lane = new LaneViewModel("Lane_Internet", "", ProbeKind.Tcp, Localizer.Default, En, time);
        lane.Record(Observation(), 0);
        int version = lane.Version;
        for (int i = 1; i < 100; i++) lane.Record(Observation(), i / 100.0);
        Assert.Equal(version, lane.Version);
        time.Advance(TimeSpan.FromSeconds(1));
        lane.Record(Observation(), 1);
        Assert.Equal(version + 1, lane.Version);
        lane.Finish(LaneState.Responding);
        Assert.Equal("101/101", lane.CountText);
        Assert.Equal(101, lane.Samples.Count);
        Assert.Equal("20 ms", lane.LatencyText);
    }

    [Fact]
    public void Hidden_finished_checks_still_have_exact_statistics_for_exports()
    {
        var lane = new LaneViewModel("Lane_Internet", "", ProbeKind.Tcp, Localizer.Default, En);
        lane.SetPresentationEnabled(false);
        for (int i = 0; i < 10; i++) lane.Record(Observation(), i);
        lane.Finish(LaneState.Responding);
        Assert.Equal("10/10", lane.CountText);
        Assert.Equal("20 ms", lane.LatencyText);
        Assert.Contains("10", lane.StatsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_sample_recording_has_a_bounded_allocation_budget()
    {
        var lane = new LaneViewModel("Lane_Internet", "", ProbeKind.Tcp, Localizer.Default, En);
        lane.SetPresentationEnabled(false);
        var observation = Observation();
        lane.Record(observation, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 2000; i++) lane.Record(observation, i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 512_000);
        Assert.Equal(2001, lane.Samples.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Downsampling_is_bounded_and_preserves_failures_without_touching_evidence(bool peaks)
    {
        Sample[] samples = [.. Enumerable.Range(0, 10_000).Select(i => new Sample(i, i % 100 == 1 ? null : 20))];
        var points = SampleDownsampling.Reduce(samples, 100, peaks);
        Assert.Equal(100, points.Count);
        Assert.All(points, sample => Assert.False(sample.Answered));
        Assert.Equal(10_000, samples.Length);
    }

    [Fact]
    public void Chart_downsampling_keeps_peaks_and_export_sampling_keeps_the_last_answer()
    {
        Sample[] samples = [new(0, 20), new(1, 500), new(2, 30), new(3, 40)];
        Assert.Equal(new Sample(1, 500), Assert.Single(SampleDownsampling.Reduce(samples, 1, preservePeaks: true)));
        Assert.Equal(new Sample(3, 40), Assert.Single(SampleDownsampling.Reduce(samples, 1)));
        Assert.Same(samples, SampleDownsampling.Reduce(samples, samples.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleDownsampling.Reduce(samples, 0));
    }

    [Fact]
    public void Hovering_a_chart_finds_the_nearest_sample_like_a_full_scan_does()
    {
        Sample[] samples = [.. Enumerable.Range(0, 1000).Select(i => new Sample(i * 0.5 + i % 7 * 0.01, i % 5 == 0 ? null : 20))];
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            double at = random.NextDouble() * 520 - 10;
            Assert.Equal(samples.MinBy(s => Math.Abs(s.Seconds - at)), SampleSearch.Nearest(samples, at));
        }
    }

    [Fact]
    public void Nearest_sample_handles_the_ends_ties_and_an_empty_series()
    {
        Assert.Null(SampleSearch.Nearest([], 5));
        Sample[] samples = [new(1, 10), new(3, 30), new(7, 70)];
        Assert.Equal(samples[0], SampleSearch.Nearest(samples, -4));
        Assert.Equal(samples[2], SampleSearch.Nearest(samples, 100));
        Assert.Equal(samples[0], SampleSearch.Nearest(samples, 2)); // equally near: the earlier one
        Assert.Equal(samples[1], SampleSearch.Nearest(samples, 2.01));
        Assert.Equal(samples[1], SampleSearch.Nearest(samples, 3));
    }

    private static ProbeObservation Observation()
    {
        var request = new ProbeRequest(Guid.NewGuid(), Guid.NewGuid(), 0,
            new StreamKey("web", ProbeKind.Tcp, RequestFamily.IPv4), 0, TimeSpan.FromSeconds(1));
        return new(request, ProbeStatus.Success, Attribution.SocketObserved, IpFamily.IPv4, 0, 20_000,
            20_000, TimingSource.UserMode, null, new EmptyDetail());
    }
}
