using System.Globalization;
using System.Net;
using System.Text;

namespace ConnectionClue.Presentation.Diagnostics;

public sealed record SupportReportPoint(double Seconds, double? Milliseconds);
public sealed record SupportReportSeries(string Name, IReadOnlyList<SupportReportPoint> Points, int OmittedSamples);

public sealed record SupportReportData(
    string Language,
    string Title,
    string CheckedAt,
    string Verdict,
    string Summary,
    string FindingsHeading,
    string TimelineHeading,
    string StepHeader,
    string TimeHeader,
    string ObservationHeader,
    string NoResponse,
    string OmittedSamplesNote,
    string PrivacyNote,
    IReadOnlyList<string> Findings,
    IReadOnlyList<SupportReportSeries> Series,
    DateTimeOffset? StartedAt = null);

public interface ISupportReportExporter
{
    Task<bool> ExportHtmlAsync(SupportReportData report, CancellationToken cancellationToken);
    bool PrintPdf(SupportReportData report);
}

public static class SupportReportHtml
{
    public static string Build(SupportReportData report)
    {
        var html = new StringBuilder(16_384);
        html.Append("<!doctype html><html lang=\"").Append(Encode(report.Language)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(Encode(report.Title)).Append("</title><style>")
            .Append("body{font:16px/1.5 Segoe UI,Arial,sans-serif;color:#111;background:#fff;max-width:1100px;margin:2rem auto;padding:0 1rem}")
            .Append("h1,h2{line-height:1.2}table{border-collapse:collapse;width:100%;margin:1rem 0}")
            .Append("th,td{border:1px solid #555;padding:.35rem .5rem;text-align:left;vertical-align:top}")
            .Append("th{background:#eee}caption{text-align:left;font-weight:600;padding:.4rem 0}")
            .Append("@media print{body{max-width:none;margin:0}.no-print{display:none}}")
            .Append("</style></head><body><main><h1>").Append(Encode(report.Title)).Append("</h1><dl>")
            .Append("<dt><strong>").Append(Encode(report.CheckedAt)).Append("</strong></dt><dd>")
            .Append(Encode(report.Verdict)).Append("</dd></dl><p>").Append(Encode(report.Summary)).Append("</p>")
            .Append("<h2>").Append(Encode(report.FindingsHeading)).Append("</h2>");

        if (report.Findings.Count > 0)
        {
            html.Append("<ul>");
            foreach (string finding in report.Findings) html.Append("<li>").Append(Encode(finding)).Append("</li>");
            html.Append("</ul>");
        }

        html.Append("<h2>").Append(Encode(report.TimelineHeading)).Append("</h2>")
            .Append("<table><caption>").Append(Encode(report.TimelineHeading)).Append("</caption><thead><tr>")
            .Append("<th scope=\"col\">").Append(Encode(report.StepHeader)).Append("</th><th scope=\"col\">")
            .Append(Encode(report.TimeHeader)).Append("</th><th scope=\"col\">")
            .Append(Encode(report.ObservationHeader)).Append("</th></tr></thead><tbody>");

        int rows = 0;
        foreach (var series in report.Series)
        {
            foreach (var point in series.Points)
            {
                html.Append("<tr><th scope=\"row\">").Append(Encode(series.Name)).Append("</th><td>")
                    .Append(Encode(TimeText(report, point.Seconds))).Append("</td><td>")
                    .Append(point.Milliseconds is { } ms
                        ? ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms"
                        : Encode(report.NoResponse))
                    .Append("</td></tr>");
                rows++;
            }
            if (series.OmittedSamples > 0)
                html.Append("<tr><td colspan=\"3\">").Append(Encode(string.Format(CultureInfo.CurrentCulture,
                    report.OmittedSamplesNote, series.Name, series.OmittedSamples))).Append("</td></tr>");
        }
        if (rows == 0) html.Append("<tr><td colspan=\"3\">").Append(Encode(report.NoResponse)).Append("</td></tr>");

        html.Append("</tbody></table><p>").Append(Encode(report.PrivacyNote)).Append("</p></main></body></html>");
        return html.ToString();
    }

    /// <summary>Local clock time when the start is known (what a provider compares with its logs), plus the offset.</summary>
    public static string TimeText(SupportReportData report, double seconds)
    {
        string offset = seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        return report.StartedAt is { } start
            ? start.AddSeconds(seconds).ToString("T", CultureInfo.CurrentCulture) + " (" + offset + ")"
            : offset;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
