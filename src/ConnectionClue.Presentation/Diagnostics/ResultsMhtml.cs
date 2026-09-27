using System.Net;
using System.Text;

namespace ConnectionClue.Presentation.Diagnostics;

/// <summary>A multipart/related web archive: UTF-8 HTML and PNG visuals travel together without remote resources.</summary>
public static class ResultsMhtml
{
    public static string Build(ResultsExportData data, IReadOnlyList<ResultsExportImage> images)
    {
        string boundary = "----ConnectionClue-" + Guid.NewGuid().ToString("N");
        var archive = new StringBuilder();
        archive.Append("MIME-Version: 1.0\r\nContent-Type: multipart/related;\r\n")
            .Append(" type=\"text/html\";\r\n start=\"<report@connectionclue>\";\r\n boundary=\"")
            .Append(boundary).Append("\"\r\n\r\n");
        Part("text/html; charset=utf-8", "report@connectionclue", "report.html", Encoding.UTF8.GetBytes(Html(data, images)));
        for (int i = 0; i < images.Count; i++)
            Part("image/png", ImageId(i), $"visual-{i}.png", images[i].PngBytes);
        return archive.Append("--").Append(boundary).Append("--\r\n").ToString();

        void Part(string type, string id, string name, byte[] bytes)
        {
            archive.Append("--").Append(boundary).Append("\r\nContent-Type: ").Append(type)
                .Append("\r\nContent-Transfer-Encoding: base64\r\nContent-ID: <").Append(id)
                .Append(">\r\nContent-Location: https://connectionclue.invalid/").Append(name)
                .Append("\r\n\r\n").Append(Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        }
    }

    private static string Html(ResultsExportData data, IReadOnlyList<ResultsExportImage> images)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"").Append(E(data.Language)).Append("\" dir=\"")
            .Append(data.RightToLeft ? "rtl" : "ltr").Append("\" data-theme=\"dark\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(E(data.Title)).Append("</title>").Append(Theme).Append("</head><body><main><header><h1>")
            .Append(E(data.Title)).Append("</h1><p>").Append(E(data.Subtitle)).Append("</p></header>");
        foreach (var section in data.Sections)
        {
            html.Append("<section><h2>").Append(E(section.Heading)).Append("</h2><ul>");
            foreach (string line in section.Lines) html.Append("<li>").Append(E(line)).Append("</li>");
            html.Append("</ul></section>");
        }
        if (images.Count > 0)
        {
            html.Append("<section><h2>").Append(E(data.VisualsHeading)).Append("</h2>");
            for (int i = 0; i < images.Count; i++)
                html.Append("<figure><img src=\"cid:").Append(ImageId(i)).Append("\" alt=\"")
                    .Append(E(images[i].Caption)).Append("\"><figcaption>").Append(E(images[i].Caption))
                    .Append("</figcaption></figure>");
            html.Append("</section>");
        }
        for (int i = 0; i < data.Tables.Count; i++)
        {
            var table = data.Tables[i];
            html.Append("<section>");
            if (table.Note is { } note) html.Append("<p>").Append(E(note)).Append("</p>");
            html.Append("<div class=\"table-scroll\" role=\"region\" tabindex=\"0\" aria-labelledby=\"table-")
                .Append(i).Append("\"><table><caption id=\"table-").Append(i).Append("\">")
                .Append(E(table.Heading)).Append("</caption><thead><tr>");
            foreach (string column in table.Columns) html.Append("<th scope=\"col\">").Append(E(column)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            foreach (var row in table.Rows)
            {
                html.Append("<tr>");
                foreach (string value in row) html.Append("<td>").Append(E(value)).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table></div></section>");
        }
        return html.Append("<footer>").Append(E(data.Footer)).Append("</footer></main></body></html>").ToString();
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
    private static string ImageId(int index) => $"visual-{index}@connectionclue";

    // MHTML readers may disable scripts; all report content, styles and images also work without them.
    private const string Theme = """
        <script>
          (() => {
            const param = new URLSearchParams(window.location.search).get("scoutTheme");
            const theme =
              param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
            document.documentElement.setAttribute("data-theme", theme);
          })();
        </script>
        <style>
        :root {
          color-scheme: light;
          --cp-bg: #f7f4ef;
          --cp-bg-elevated: #fcfbf8;
          --cp-surface: #ffffff;
          --cp-surface-soft: #f5f5f5;
          --cp-border: #dedede;
          --cp-border-strong: #919191;
          --cp-text: #242424;
          --cp-text-muted: #5c5c5c;
          --cp-text-soft: #6f6f6f;
          --cp-accent: #b11f4b;
          --cp-accent-hover: #9a1a41;
          --cp-accent-soft: rgba(177, 31, 75, 0.08);
          --cp-accent-fg: #ffffff;
          --cp-success: #16a34a;
          --cp-danger: #dc2626;
          --cp-warning: #f59e0b;
          --cp-link: #0078d4;
          --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.12);
          --cp-overlay: rgba(255, 255, 255, 0.8);
          --cp-panel: rgba(255, 255, 255, 0.86);
          --cp-panel-strong: rgba(255, 255, 255, 0.96);
          --cp-sheen: rgba(255, 255, 255, 0.55);
          --cp-highlight: rgba(177, 31, 75, 0.12);
        }
        html[data-theme="dark"] {
          color-scheme: dark;
          --cp-bg: #3d3b3a;
          --cp-bg-elevated: #343231;
          --cp-surface: #292929;
          --cp-surface-soft: #2e2e2e;
          --cp-border: #474747;
          --cp-border-strong: #5f5f5f;
          --cp-text: #dedede;
          --cp-text-muted: #919191;
          --cp-text-soft: #b0b0b0;
          --cp-accent: #fd8ea1;
          --cp-accent-hover: #fb7b91;
          --cp-accent-soft: rgba(253, 142, 161, 0.14);
          --cp-accent-fg: #1a1a1a;
          --cp-success: #4ade80;
          --cp-danger: #f87171;
          --cp-warning: #fbbf24;
          --cp-link: #4da6ff;
          --cp-shadow: 0 18px 48px rgba(0, 0, 0, 0.32);
          --cp-overlay: rgba(41, 41, 41, 0.88);
          --cp-panel: rgba(41, 41, 41, 0.72);
          --cp-panel-strong: rgba(41, 41, 41, 0.96);
          --cp-sheen: rgba(255, 255, 255, 0.04);
          --cp-highlight: rgba(253, 142, 161, 0.12);
        }
        body { margin: 0; background: var(--cp-bg); color: var(--cp-text);
          font-family: "Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif;
          font-size: 1.125rem; font-weight: 400; font-style: normal; font-stretch: normal;
          line-height: 1.75; letter-spacing: normal; }
        main { max-width: 72rem; margin: 0 auto; padding: 1.5rem; }
        header, section { margin-bottom: 1rem; padding: 1.5rem; border-radius: 16px;
          background: var(--cp-surface); border: 1px solid var(--cp-border); }
        h1, h2, caption { color: var(--cp-accent); line-height: 1.4; font-weight: 600; overflow-wrap: anywhere; }
        h1 { font-size: 2rem; margin: 0 0 0.75rem; }
        h2, caption { font-size: 1.375rem; margin: 0 0 1rem; }
        p, ul { margin: 0.75rem 0; max-inline-size: 72ch; }
        ul { padding-inline-start: 1.5rem; }
        li { margin-block: 0.5rem; }
        li, td { white-space: pre-wrap; overflow-wrap: anywhere; }
        figure { margin: 1rem 0; }
        img { display: block; max-width: 100%; height: auto; border-radius: 0.625rem; }
        figcaption, footer { color: var(--cp-text-soft); margin-top: 0.75rem; max-inline-size: 72ch; }
        .table-scroll { overflow-x: auto; }
        .table-scroll:focus-visible { outline: 3px solid var(--cp-accent); outline-offset: 4px; }
        table { width: 100%; border-collapse: collapse; font: inherit; font-variant-numeric: tabular-nums; }
        caption { text-align: start; font-weight: 600; }
        th, td { border: 1px solid var(--cp-border-strong); padding: 0.75rem 1rem; text-align: start; vertical-align: top; }
        th { background: var(--cp-surface-soft); }
        code { font-family: Consolas, "Courier New", Courier, monospace; }
        @media print {
          main { padding: 0; }
          section, figure { break-inside: avoid-page; }
          .table-scroll { overflow: visible; }
          thead { display: table-header-group; }
          tr { break-inside: avoid; }
        }
        </style>
        """;
}
