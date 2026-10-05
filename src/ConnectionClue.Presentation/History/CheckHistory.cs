using System.Globalization;
using ConnectionClue.Analysis;

namespace ConnectionClue.Presentation.History;

/// <summary>A small, identity-free summary of one check. Sample gaps are not treated as observed uptime.
/// DownloadAtLeastMbps and UploadAtLeastMbps are lower bounds from light background samples that spent their byte budget
/// before the speed settled: shown with "≥", never averaged. LightSample: the speeds come from a background check's light
/// sample (one connection), not a quick check's full test.</summary>
public sealed record CheckHistoryEntry(
    DateTimeOffset CheckedAtUtc,
    HealthLevel Level,
    double? DownloadMbps,
    double? UploadMbps,
    double? MedianLatencyMs,
    double? VariationMs,
    BufferbloatGrade? BufferbloatGrade,
    double? BufferbloatIncreaseMs,
    double? DownloadAtLeastMbps = null,
    double? UploadAtLeastMbps = null,
    bool LightSample = false);

public interface ICheckHistoryStore
{
    IReadOnlyList<CheckHistoryEntry> Load();
    void Save(IReadOnlyList<CheckHistoryEntry> entries);
}

/// <summary>QualityScore (0–100) averages the conclusive checks: 100 with no issue, 50 when slower than the user's limits,
/// 0 with a connection problem. Inconclusive checks and the unobserved time between checks do not count.</summary>
public sealed record DailyTrend(
    DateOnly Day,
    int Checks,
    int ConclusiveChecks,
    int HealthyChecks,
    double? NoIssueCheckRate,
    double? AverageDownloadMbps,
    double? AverageUploadMbps,
    double? AverageDownloadPercentOfPlan,
    double? AverageUploadPercentOfPlan,
    double? QualityScore = null);

public sealed record HourlyTrend(int Hour, int Checks, int ProblemChecks);

public static class CheckHistoryAnalytics
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int MaxEntries = 5000;

    /// <summary>Sorts, removes expired data, and caps storage without storing endpoint or device identifiers.</summary>
    public static IReadOnlyList<CheckHistoryEntry> Retain(IEnumerable<CheckHistoryEntry> entries, DateTimeOffset nowUtc) =>
        [.. entries.Where(e => e.CheckedAtUtc <= nowUtc && nowUtc - e.CheckedAtUtc <= Retention)
            .OrderByDescending(e => e.CheckedAtUtc).Take(MaxEntries)];

    public static IReadOnlyList<DailyTrend> Daily(
        IEnumerable<CheckHistoryEntry> entries, double? downloadPlanMbps, double? uploadPlanMbps, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return [.. entries.GroupBy(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.CheckedAtUtc, zone).DateTime))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var rows = g.ToArray();
                int conclusive = rows.Count(e => e.Level != HealthLevel.Inconclusive);
                var downloads = rows.Where(e => e.DownloadMbps is > 0).Select(e => e.DownloadMbps!.Value).ToArray();
                var uploads = rows.Where(e => e.UploadMbps is > 0).Select(e => e.UploadMbps!.Value).ToArray();
                return new DailyTrend(g.Key, rows.Length, conclusive, rows.Count(e => e.Level == HealthLevel.NoIssue),
                    conclusive == 0 ? null : 100.0 * rows.Count(e => e.Level == HealthLevel.NoIssue) / conclusive,
                    Average(downloads), Average(uploads), Ratio(downloads, downloadPlanMbps), Ratio(uploads, uploadPlanMbps),
                    conclusive == 0 ? null : rows.Where(e => e.Level != HealthLevel.Inconclusive).Average(e => e.Level switch
                    {
                        HealthLevel.NoIssue => 100.0,
                        HealthLevel.Degraded => 50.0,
                        _ => 0.0,
                    }));
            })];
    }

    public static IReadOnlyList<HourlyTrend> WorstHours(IEnumerable<CheckHistoryEntry> entries, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return [.. entries.GroupBy(e => TimeZoneInfo.ConvertTime(e.CheckedAtUtc, zone).Hour)
            .Select(g => new HourlyTrend(g.Key, g.Count(), g.Count(e => e.Level is HealthLevel.Degraded or HealthLevel.Unhealthy)))
            .Where(g => g.ProblemChecks > 0)
            .OrderByDescending(g => g.ProblemChecks)
            .ThenByDescending(g => g.Checks)
            .ThenBy(g => g.Hour)];
    }

    private static double? Average(double[] values) => values.Length == 0 ? null : values.Average();

    private static double? Ratio(double[] values, double? plan) =>
        values.Length == 0 || plan is not > 0 ? null : 100 * values.Average() / plan.Value;
}
