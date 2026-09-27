using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using ConnectionClue.Presentation.ViewModels;

namespace ConnectionClue.App;

public partial class HelpWindow : Window
{
    public HelpWindow(MainViewModel viewModel, CultureInfo ui)
    {
        InitializeComponent();
        DataContext = viewModel;
        Language = XmlLanguage.GetLanguage(ui.IetfLanguageTag);
        FlowDirection = ui.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        HelpDocument.Blocks.Clear();
        foreach (var block in ParseMarkdown(ReadHelpText()))
            HelpDocument.Blocks.Add(block);
    }

    private static string ReadHelpText()
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream("ConnectionClue.App.helpme.md")
            ?? throw new InvalidOperationException("The embedded helpme.md resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<Block> ParseMarkdown(string markdown)
    {
        var blocks = new List<Block>();
        var paragraph = new StringBuilder();
        System.Windows.Documents.List? list = null;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            block.Inlines.AddRange(Inlines(paragraph.ToString()));
            blocks.Add(block);
            paragraph.Clear();
        }

        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                list = null;
                continue;
            }

            int headingLevel = line.TakeWhile(c => c == '#').Count();
            if (headingLevel is >= 1 and <= 3 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                FlushParagraph();
                list = null;
                var heading = new Paragraph(new Run(line[(headingLevel + 1)..]))
                {
                    FontSize = headingLevel == 1 ? 22 : headingLevel == 2 ? 17 : 14,
                    FontWeight = FontWeights.SemiBold,
                    Margin = headingLevel == 1 ? new Thickness(0, 0, 0, 12) : new Thickness(0, 16, 0, 6),
                };
                heading.SetResourceReference(TextElement.ForegroundProperty, "HeadingBrush");
                blocks.Add(heading);
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (list is null)
                {
                    list = new System.Windows.Documents.List
                    {
                        MarkerStyle = TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 10),
                        Padding = new Thickness(18, 0, 0, 0),
                    };
                    blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                item.Inlines.AddRange(Inlines(line[2..]));
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            list = null;
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line);
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// **bold** and `code`, the only inline markup the guide uses; anything else stays literal. Bold names a control, so it
    /// takes the label colour, as labels do everywhere in the app.
    /// </summary>
    private static IEnumerable<Inline> Inlines(string text)
    {
        foreach (string part in System.Text.RegularExpressions.Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal))
            {
                var name = new Bold(new Run(part[2..^2]));
                name.SetResourceReference(TextElement.ForegroundProperty, "LabelBrush");
                yield return name;
            }
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`')
                yield return new Run(part[1..^1]) { FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
            else
                yield return new Run(part);
        }
    }
}
