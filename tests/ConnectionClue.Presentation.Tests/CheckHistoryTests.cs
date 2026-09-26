using ConnectionClue.Analysis;
using ConnectionClue.Presentation.History;

namespace ConnectionClue.Presentation.Tests;

public sealed class CheckHistoryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Retention_is_sorted_bounded_and_excludes_future_entries()
    {
        var now = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
        var entries = Enumerable.Range(0, CheckHistoryAnalytics.MaxEntries + 5)
            .Select(i => new CheckHistoryEntry(now.AddMinutes(-i), HealthLevel.NoIssue, null, null, 5, null, null, null))
            .Append(new(now.AddDays(-31), HealthLevel.NoIssue, null, null, 5, null, null, null))
            .Append(new(now.AddMinutes(1), HealthLevel.NoIssue, null, null, 5, null, null, null));

        var retained = CheckHistoryAnalytics.Retain(entries, now);

        Assert.Equal(CheckHistoryAnalytics.MaxEntries, retained.Count);
        Assert.Equal(retained.OrderByDescending(e => e.CheckedAtUtc), retained);
        Assert.All(retained, e => Assert.InRange(now - e.CheckedAtUtc, TimeSpan.Zero, CheckHistoryAnalytics.Retention));
    }

    [Fact]
    public void Daily_trends_use_conclusive_sample_rate_and_explicit_plan_inputs()
    {
        var day = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
        CheckHistoryEntry Entry(int hour, HealthLevel level, double? down, double? up) =>
            new(day.AddHours(hour - 10), level, down, up, 20, 3, null, null);
        CheckHistoryEntry[] rows =
        [
            Entry(10, HealthLevel.NoIssue, 80, 20),
            Entry(11, HealthLevel.Degraded, 40, null),
            Entry(12, HealthLevel.Inconclusive, null, null),
        ];

        var trend = Assert.Single(CheckHistoryAnalytics.Daily(rows, 100, 50, Utc));

        Assert.Equal(new DateOnly(2026, 9, 26), trend.Day);
        Assert.Equal((3, 2, 1), (trend.Checks, trend.ConclusiveChecks, trend.HealthyChecks));
        Assert.Equal(50, trend.NoIssueCheckRate);
        Assert.Equal(75, trend.QualityScore); // (100 + 50) / 2; the inconclusive check does not count
        Assert.Equal(60, trend.AverageDownloadMbps);
        Assert.Equal(20, trend.AverageUploadMbps);
        Assert.Equal(60, trend.AverageDownloadPercentOfPlan);
        Assert.Equal(40, trend.AverageUploadPercentOfPlan);
    }

    [Fact]
    public void Worst_hours_are_local_time_and_only_rank_observed_problem_checks()
    {
        var time = DateTimeOffset.Parse("2026-09-26T15:00:00Z");
        var rows = new[]
        {
            new CheckHistoryEntry(time, HealthLevel.Degraded, null, null, 120, null, null, null),
            new CheckHistoryEntry(time.AddMinutes(10), HealthLevel.Unhealthy, null, null, null, null, null, null),
            new CheckHistoryEntry(time.AddHours(1), HealthLevel.NoIssue, null, null, 20, null, null, null),
            new CheckHistoryEntry(time.AddHours(2), HealthLevel.Inconclusive, null, null, null, null, null, null),
        };

        Assert.Equal(new HourlyTrend(15, 2, 2), Assert.Single(CheckHistoryAnalytics.WorstHours(rows, Utc)));
    }

    [Fact]
    public void No_issues_or_no_speed_samples_does_not_invent_trend_data()
    {
        var row = new CheckHistoryEntry(DateTimeOffset.UtcNow, HealthLevel.Inconclusive, null, null, null, null, null, null);
        var daily = Assert.Single(CheckHistoryAnalytics.Daily([row], 300, 30, Utc));
        Assert.Null(daily.NoIssueCheckRate);
        Assert.Null(daily.QualityScore);
        Assert.Null(daily.AverageDownloadPercentOfPlan);
        Assert.Null(daily.AverageUploadPercentOfPlan);
        Assert.Empty(CheckHistoryAnalytics.WorstHours([row], Utc));
    }
}
