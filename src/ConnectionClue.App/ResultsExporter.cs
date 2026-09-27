using System.Globalization;
using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConnectionClue.Presentation.Diagnostics;
using ConnectionClue.Presentation.Localization;
using Microsoft.Win32;

namespace ConnectionClue.App;

/// <summary>
/// PDF uses Microsoft Print to PDF. MHTML saves the same results and captured visuals in a self-contained web archive.
/// Results and tables stay real text (selectable, searchable, readable by assistive technology, in every UI language and
/// script); the Check page and its chart follow as images, each with a caption as its text alternative.
/// </summary>
internal sealed class ResultsExporter(Func<IReadOnlyList<FrameworkElement>> visuals) : IResultsExporter
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private const double PageMargin = 48, SnapshotScale = 2;
    private static readonly Size A4 = new(793.7, 1122.5);

    public Task<ExportOutcome> ExportPdfAsync(ResultsExportData data, CancellationToken cancellationToken)
    {
        PrintQueue queue;
        try { queue = new LocalPrintServer().GetPrintQueue(PdfPrinter); }
        catch (PrintSystemException) { return Task.FromResult(ExportOutcome.NoPdfPrinter); }
        try
        {
            var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
            var page = ticket.PageMediaSize is { Width: { } width, Height: { } height } ? new Size(width, height) : A4;
            var document = Build(data, page, visuals());
            queue.CurrentJobSettings.Description = data.FileName; // the name Windows suggests in its save dialog
            PrintQueue.CreateXpsDocumentWriter(queue).Write(((IDocumentPaginatorSource)document).DocumentPaginator, ticket);
            return Task.FromResult(ExportOutcome.Sent);
        }
        catch (PrintingCanceledException)
        {
            return Task.FromResult(ExportOutcome.Cancelled);
        }
        catch (PrintSystemException)
        {
            return Task.FromResult(ExportOutcome.Failed);
        }
    }

    public async Task<ExportOutcome> ExportMhtmlAsync(ResultsExportData data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture before the modal file picker can process updates to the displayed check.
        var images = CaptureImages(data, cancellationToken);
        var ui = CultureInfo.GetCultureInfo(data.Language);
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".mhtml",
            FileName = data.FileName + ".mhtml",
            Filter = Localizer.Default.Get("Export_MhtmlFilter", ui),
            Title = Localizer.Default.Get("Export_MhtmlButton", ui),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return ExportOutcome.Cancelled;
        await WriteMhtmlAsync(data, images, dialog.FileName, cancellationToken);
        return ExportOutcome.Saved;
    }

    internal async Task SaveMhtmlAsync(ResultsExportData data, string path, CancellationToken cancellationToken) =>
        await WriteMhtmlAsync(data, CaptureImages(data, cancellationToken), path, cancellationToken);

    private List<ResultsExportImage> CaptureImages(ResultsExportData data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shown = visuals();
        if (shown.Count == 0) throw new InvalidOperationException("The check visuals are not available for export.");
        var images = new List<ResultsExportImage>(shown.Count);
        for (int i = 0; i < shown.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = Snapshot(shown[i]) ?? throw new InvalidOperationException("A check visual could not be captured.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            images.Add(new(i < data.VisualCaptions.Count ? data.VisualCaptions[i] : data.VisualsHeading, stream.ToArray()));
        }
        return images;
    }

    private static async Task WriteMhtmlAsync(ResultsExportData data, IReadOnlyList<ResultsExportImage> images, string path,
        CancellationToken cancellationToken)
    {
        string archive = await Task.Run(() => ResultsMhtml.Build(data, images), cancellationToken);
        string temp = Path.ChangeExtension(path, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temp, archive, Encoding.ASCII, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Text sections first, then each visual on its own page with its caption, then the measurement tables.</summary>
    internal static FlowDocument Build(ResultsExportData data, Size page, IReadOnlyList<FrameworkElement> shown)
    {
        double width = page.Width - 2 * PageMargin, height = page.Height - 2 * PageMargin;
        var document = new FlowDocument
        {
            PageWidth = page.Width,
            PageHeight = page.Height,
            PagePadding = new Thickness(PageMargin),
            ColumnWidth = page.Width,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            FlowDirection = data.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Language = XmlLanguage.GetLanguage(data.Language),
        };
        document.Blocks.Add(new Paragraph(new Run(data.Title)) { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        document.Blocks.Add(new Paragraph(new Run(data.Subtitle)) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var section in data.Sections)
        {
            document.Blocks.Add(Heading(section.Heading));
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(18, 0, 0, 0) };
            foreach (string line in section.Lines) list.ListItems.Add(new ListItem(new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 2) }));
            document.Blocks.Add(list);
        }

        for (int i = 0; i < shown.Count; i++)
        {
            if (Snapshot(shown[i]) is not { } image) continue;
            var heading = Heading(data.VisualsHeading);
            heading.BreakPageBefore = true;
            document.Blocks.Add(heading);
            if (i < data.VisualCaptions.Count) document.Blocks.Add(new Paragraph(new Run(data.VisualCaptions[i])) { Margin = new Thickness(0, 0, 0, 6) });
            double scale = Math.Min(width / image.Width, (height - 80) / image.Height);
            document.Blocks.Add(new BlockUIContainer(new Image
            {
                Source = image,
                Width = image.Width * scale,
                Height = image.Height * scale,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                FlowDirection = FlowDirection.LeftToRight, // a screenshot is never mirrored, even in a right-to-left document
            }));
        }

        foreach (var table in data.Tables)
        {
            var heading = Heading(table.Heading);
            heading.BreakPageBefore = true;
            document.Blocks.Add(heading);
            if (table.Note is { } note) document.Blocks.Add(new Paragraph(new Run(note)) { Foreground = Brushes.DimGray });
            document.Blocks.Add(Table(table));
        }
        document.Blocks.Add(new Paragraph(new Run(data.Footer)) { FontSize = 9, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) });
        return document;
    }

    private static Paragraph Heading(string text) =>
        new(new Run(text)) { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), KeepWithNext = true };

    private static Table Table(ResultsTable data)
    {
        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
        foreach (string _ in data.Columns) table.Columns.Add(new TableColumn());
        var rows = new TableRowGroup();
        var header = new TableRow { Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)), FontWeight = FontWeights.SemiBold };
        foreach (string column in data.Columns) header.Cells.Add(Cell(column));
        rows.Rows.Add(header);
        foreach (var values in data.Rows)
        {
            var row = new TableRow();
            foreach (string value in values) row.Cells.Add(Cell(value));
            rows.Rows.Add(row);
        }
        table.RowGroups.Add(rows);
        return table;
    }

    private static TableCell Cell(string text) => new(new Paragraph(new Run(text)) { Margin = new Thickness(0) })
    {
        Padding = new Thickness(4, 1, 4, 1),
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(0, 0, 0, 0.5),
    };

    /// <summary>The element as shown, at print resolution, on the app's own background so dark themes stay readable.</summary>
    private static BitmapSource? Snapshot(FrameworkElement element)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1 || !element.IsVisible) return null;
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)(bounds.Width * SnapshotScale), (int)(bounds.Height * SnapshotScale),
            96 * SnapshotScale, 96 * SnapshotScale, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var background = Application.Current?.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
            context.DrawRectangle(background, null, bounds);
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, bounds);
        }
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }
}
