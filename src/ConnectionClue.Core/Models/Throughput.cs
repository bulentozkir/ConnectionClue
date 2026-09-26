namespace ConnectionClue.Core;

public enum ThroughputDirection { Download, Upload }

/// <summary>One throughput phase. Mbps is null when the phase failed or moved too little data to judge.</summary>
public sealed record ThroughputResult(
    ThroughputDirection Direction, ProbeStatus Status, double? Mbps, long Bytes, TimeSpan Elapsed, string? ErrorCode = null);

/// <summary>Saturating transfer against an approved throughput endpoint; bounded by time and bytes.</summary>
public interface IThroughputProbe
{
    Task<ThroughputResult> MeasureAsync(ThroughputDirection direction, TimeSpan duration, IProgress<double>? liveMbps, CancellationToken ct);
}

public static class ThroughputMath
{
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
}
