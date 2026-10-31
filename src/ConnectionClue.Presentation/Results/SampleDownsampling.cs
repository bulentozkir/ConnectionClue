using ConnectionClue.Analysis;

namespace ConnectionClue.Presentation.Results;

/// <summary>Bounds display/export points without changing evidence. Every bucket retains an unanswered check if present.</summary>
public static class SampleDownsampling
{
    public static IReadOnlyList<Sample> Reduce(IReadOnlyList<Sample> samples, int maximum, bool preservePeaks = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (samples.Count <= maximum) return samples;
        int stride = (int)Math.Ceiling(samples.Count / (double)maximum);
        var points = new List<Sample>(maximum);
        for (int start = 0; start < samples.Count; start += stride)
        {
            int end = Math.Min(samples.Count, start + stride);
            var selected = samples[end - 1];
            for (int i = start; i < end; i++)
            {
                if (!samples[i].Answered)
                {
                    selected = samples[i];
                    break;
                }
                if (preservePeaks && samples[i].Milliseconds > selected.Milliseconds) selected = samples[i];
            }
            points.Add(selected);
        }
        return points;
    }
}
