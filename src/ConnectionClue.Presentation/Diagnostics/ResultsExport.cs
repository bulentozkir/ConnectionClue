namespace ConnectionClue.Presentation.Diagnostics;

/// <summary>A titled list of plain sentences (results, findings, measurements…).</summary>
public sealed record ResultsSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>A titled table; Note explains omitted rows, if any.</summary>
public sealed record ResultsTable(string Heading, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? Note = null);

/// <summary>
/// One check's results for PDF and MHTML: text sections, then images of what the Check page showed (each with a
/// caption, the images' text alternative), then the measurement tables. FileName is the suggested name, without extension.
/// </summary>
public sealed record ResultsExportData(
    string Language, bool RightToLeft, string Title, string Subtitle, string FileName,
    IReadOnlyList<ResultsSection> Sections, string VisualsHeading, IReadOnlyList<string> VisualCaptions,
    IReadOnlyList<ResultsTable> Tables, string Footer);

/// <summary>A captured visual with its text alternative, encoded as PNG for a self-contained export.</summary>
public sealed record ResultsExportImage(string Caption, byte[] PngBytes);

/// <summary>Sent: handed to the PDF printer; Saved: an archive was written; Cancelled: the user stopped it.</summary>
public enum ExportOutcome { Sent, Cancelled, NoPdfPrinter, Failed, Saved }

public interface IResultsExporter
{
    Task<ExportOutcome> ExportPdfAsync(ResultsExportData data, CancellationToken cancellationToken);
    Task<ExportOutcome> ExportMhtmlAsync(ResultsExportData data, CancellationToken cancellationToken);
}
