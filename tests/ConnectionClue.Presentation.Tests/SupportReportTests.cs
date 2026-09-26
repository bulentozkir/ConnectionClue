using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.Presentation.Tests;

public sealed class SupportReportTests
{
    [Fact]
    public void Html_report_is_self_contained_accessible_and_escapes_content()
    {
        var report = new SupportReportData("en", "<script>", "2026-01-01 21:00", "Inconclusive",
            "<script>alert(1)</script>", "Findings", "Timeline", "Step", "Time", "Observation",
            "No response", "{0}: {1} omitted", "Private identifiers omitted",
            ["<img src=x onerror=alert(1)>"],
            [new("Internet", [new(0, null)], 4)]);

        string html = SupportReportHtml.Build(report);

        Assert.Contains("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("scope=\"col\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
        Assert.Contains("4 omitted", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
    }
}
