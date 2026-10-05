namespace ConnectionClue.Core;

public enum ThroughputDirection { Download, Upload }

/// <summary>One throughput phase. Mbps is null when the phase failed or moved too little data to judge. LowerBound: a light
/// sample spent its byte budget before its rate settled, so the link is at least this fast.</summary>
public sealed record ThroughputResult(
    ThroughputDirection Direction, ProbeStatus Status, double? Mbps, long Bytes, TimeSpan Elapsed, string? ErrorCode = null,
    bool LowerBound = false);

/// <summary>
/// How much one throughput phase may load the link: parallel streams for at most Duration and MaxBytes. With StopWhenSettled
/// the phase ends as soon as its rate is steady (<see cref="ThroughputMath.SettledMbps"/>), so it moves only what the
/// measurement needs.
/// </summary>
public sealed record ThroughputBudget(TimeSpan Duration, int Streams, long MaxBytes, bool StopWhenSettled = false)
{
    public const int FullStreams = 4;
    public static readonly TimeSpan FullPhase = TimeSpan.FromSeconds(8), LightPhase = TimeSpan.FromSeconds(3);

    /// <summary>The manual speed test: 4 streams for 8 s that saturate the link, up to 200 MB down and 100 MB up.</summary>
    public static ThroughputBudget Full(ThroughputDirection direction) =>
        new(FullPhase, FullStreams, direction == ThroughputDirection.Download ? 200_000_000 : 100_000_000);

    /// <summary>The background sample: one connection that stops once its rate settles (usually after a few MB on a home
    /// link), and never runs longer than 3 s or past 20 MB down and 10 MB up.</summary>
    public static ThroughputBudget Light(ThroughputDirection direction) =>
        new(LightPhase, 1, direction == ThroughputDirection.Download ? 20_000_000 : 10_000_000, StopWhenSettled: true);
}

/// <summary>Transfer against an approved throughput endpoint, bounded by the budget's time, streams and bytes.</summary>
public interface IThroughputProbe
{
    Task<ThroughputResult> MeasureAsync(ThroughputDirection direction, ThroughputBudget budget, IProgress<double>? liveMbps, CancellationToken ct);
}

public static class ThroughputMath
{
    /// <summary>Two consecutive windows whose rates differ by at most this fraction count as a settled rate.</summary>
    public const double SettleTolerance = 0.15;

    /// <summary>Every settle window carries at least this much data, so a slow link is judged on more than a burst.</summary>
    public const long MinSettleBytes = 1_000_000;

    /// <summary>Shortest and longest settle window; see <see cref="SettledMbps"/>. Shorter windows let an upload's socket
    /// buffer, which fills at memory speed, pass for the link's rate.</summary>
    public static readonly TimeSpan MinSettleWindow = TimeSpan.FromMilliseconds(200), MaxSettleWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Mbps from cumulative (seconds, bytes) samples, excluding TCP ramp-up: the first second, or the first quarter
    /// of a short transfer (a byte cap can end a fast test early).
    /// </summary>
    public static double? Mbps(IReadOnlyList<(double Seconds, long Bytes)> samples)
    {
        if (samples.Count < 2) return null;
        var last = samples[^1];
        if (last.Seconds <= 0 || last.Bytes <= 0) return null;
        double warmUp = Math.Min(1.0, last.Seconds / 4);
        var start = samples.LastOrDefault(s => s.Seconds <= warmUp);
        double seconds = last.Seconds - start.Seconds;
        return seconds <= 0 ? null : (last.Bytes - start.Bytes) * 8 / 1e6 / seconds;
    }

    /// <summary>
    /// Mbps of the last window of cumulative (seconds, bytes) samples once it agrees with the window before within
    /// <see cref="SettleTolerance"/>; null while the rate still changes. The later window is reported, because the earlier one
    /// may still hold the tail of ramp-up. Both windows start after the first byte. Each lasts at least as long as that first
    /// byte took to arrive (connection setup and request: a few round trips), kept between <see cref="MinSettleWindow"/> and
    /// <see cref="MaxSettleWindow"/>, and carries at least <see cref="MinSettleBytes"/>: TCP ramp-up doubles the rate every
    /// round trip, so it can't pass for a steady rate.
    /// </summary>
    public static double? SettledMbps(IReadOnlyList<(double Seconds, long Bytes)> samples)
    {
        int first = -1;
        for (int i = 0; i < samples.Count && first < 0; i++)
            if (samples[i].Bytes > 0) first = i;
        if (first < 0) return null;
        double window = Math.Clamp(samples[first].Seconds, MinSettleWindow.TotalSeconds, MaxSettleWindow.TotalSeconds);
        var end = samples[^1];
        int mid = WindowStart(samples, samples.Count - 1, window);
        int start = mid < first ? -1 : WindowStart(samples, mid, window);
        if (start < first) return null;
        var (from, middle) = (samples[start], samples[mid]);
        double earlier = (middle.Bytes - from.Bytes) / (middle.Seconds - from.Seconds);
        double later = (end.Bytes - middle.Bytes) / (end.Seconds - middle.Seconds);
        if (earlier <= 0 || later <= 0 || Math.Abs(later - earlier) > SettleTolerance * Math.Max(earlier, later)) return null;
        return later * 8 / 1e6;
    }

    /// <summary>The latest sample at least window seconds and <see cref="MinSettleBytes"/> before samples[end]; -1 if none.</summary>
    private static int WindowStart(IReadOnlyList<(double Seconds, long Bytes)> samples, int end, double window)
    {
        var (seconds, bytes) = samples[end];
        for (int i = end - 1; i >= 0; i--)
            if (samples[i].Seconds <= seconds - window && samples[i].Bytes <= bytes - MinSettleBytes) return i;
        return -1;
    }
}
