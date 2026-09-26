namespace ConnectionClue.Analysis;

public enum BufferbloatGrade { A, B, C, D, E, F, NotMeasured }

public sealed record BufferbloatResult(BufferbloatGrade Grade, double IncreaseMs, double IdleMedianMs, double LoadedMedianMs);

/// <summary>Grades the increase in median internet latency while downloading or uploading, not the absolute latency.</summary>
public static class BufferbloatEvaluator
{
    public static BufferbloatResult? Evaluate(double? idleMedianMs, double? loadedDownloadMs, double? loadedUploadMs)
    {
        if (idleMedianMs is not { } idle || idle < 0) return null;
        var loaded = new[] { loadedDownloadMs, loadedUploadMs }.Where(x => x is >= 0).Select(x => x!.Value).ToArray();
        if (loaded.Length == 0) return null;
        double measured = loaded.Max();
        double increase = Math.Max(0, measured - idle);
        var grade = increase switch
        {
            <= 5 => BufferbloatGrade.A,
            <= 30 => BufferbloatGrade.B,
            <= 60 => BufferbloatGrade.C,
            <= 100 => BufferbloatGrade.D,
            <= 200 => BufferbloatGrade.E,
            _ => BufferbloatGrade.F,
        };
        return new(grade, increase, idle, measured);
    }
}
