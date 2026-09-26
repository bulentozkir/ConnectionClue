using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using ConnectionClue.Presentation.Diagnostics;

namespace ConnectionClue.App;

internal sealed class SupportReportExporter : ISupportReportExporter
{
    public async Task<bool> ExportHtmlAsync(SupportReportData report, CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".html",
            FileName = $"ConnectionClue-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
            Filter = "HTML report (*.html)|*.html",
            OverwritePrompt = true,
            Title = "Export ConnectionClue report",
        };
        if (dialog.ShowDialog() != true) return false;
        await File.WriteAllTextAsync(dialog.FileName, SupportReportHtml.Build(report), new UTF8Encoding(false), cancellationToken);
        return true;
    }

    public bool PrintPdf(SupportReportData report)
    {
        var document = CreateDocument(report);
        var dialog = new PrintDialog();
        SelectPdfPrinter(dialog);
        if (dialog.ShowDialog() != true) return false;
        try
        {
            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, report.Title);
            return true;
        }
        catch (PrintSystemException e)
        {
            throw new IOException("The report could not be sent to the selected printer.", e);
        }
    }

    /// <summary>Preselects Windows' built-in PDF printer, so Print saves a PDF; another printer can still be chosen.</summary>
    private static void SelectPdfPrinter(PrintDialog dialog)
    {
        try { dialog.PrintQueue = new LocalPrintServer().GetPrintQueue("Microsoft Print to PDF"); }
        catch (PrintSystemException) { } // not installed: keep the default printer
    }

    private static FlowDocument CreateDocument(SupportReportData report)
    {
        var document = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 10,
            PagePadding = new Thickness(36),
            ColumnWidth = double.PositiveInfinity,
        };
        document.Blocks.Add(new Paragraph(new Run(report.Title)) { FontSize = 20, FontWeight = FontWeights.Bold });
        document.Blocks.Add(new Paragraph(new Run($"{report.CheckedAt} · {report.Verdict}")) { FontWeight = FontWeights.SemiBold });
        document.Blocks.Add(new Paragraph(new Run(report.Summary)));
        document.Blocks.Add(new Paragraph(new Run(report.FindingsHeading)) { FontSize = 15, FontWeight = FontWeights.Bold });
        foreach (string finding in report.Findings)
            document.Blocks.Add(new Paragraph(new Run("- " + finding)));
        document.Blocks.Add(new Paragraph(new Run(report.TimelineHeading)) { FontSize = 15, FontWeight = FontWeights.Bold });
        foreach (var series in report.Series)
        {
            document.Blocks.Add(new Paragraph(new Run(series.Name)) { FontWeight = FontWeights.SemiBold });
            foreach (var point in series.Points)
            {
                string value = point.Milliseconds is { } ms
                    ? ms.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms"
                    : report.NoResponse;
                document.Blocks.Add(new Paragraph(new Run($"{SupportReportHtml.TimeText(report, point.Seconds)} · {value}")));
            }
            if (series.OmittedSamples > 0)
                document.Blocks.Add(new Paragraph(new Run(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    report.OmittedSamplesNote, series.Name, series.OmittedSamples))));
        }
        document.Blocks.Add(new Paragraph(new Run(report.PrivacyNote)));
        return document;
    }
}
