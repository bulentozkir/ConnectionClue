using ConnectionClue.Analysis;

namespace ConnectionClue.Presentation.Results;

/// <summary>Finds a sample by time in a series that is kept in time order, without scanning all of it.</summary>
public static class SampleSearch
{
    /// <summary>The sample nearest to <paramref name="seconds"/> (the earlier one when two are equally near); null when empty.</summary>
    public static Sample? Nearest(IReadOnlyList<Sample> samples, double seconds)
    {
        if (samples.Count == 0) return null;
        int low = 0, high = samples.Count; // the first sample at or after the time
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (samples[middle].Seconds < seconds) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return samples[0];
        if (low == samples.Count) return samples[^1];
        var (before, after) = (samples[low - 1], samples[low]);
        return seconds - before.Seconds <= after.Seconds - seconds ? before : after;
    }
}
