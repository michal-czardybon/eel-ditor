using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EelDitor.Markdown;

/// <summary>
/// Markdown → a standalone HTML page, for export (PDF is printed from it). It uses the editor's own parser, so the
/// export shows what the editor shows: the same emphasis rules, tables, task lists, footnotes, ==highlight==,
/// sub/superscript and &lt;br&gt;. Blocks: headings, paragraphs, lists (nested by indentation), block quotes,
/// fenced code, tables, horizontal rules. Other raw HTML is shown as text. Images refer to local files only:
/// the page forbids loading anything from the network.
/// </summary>
public static partial class MarkdownHtml
{
    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])(\s+|$)")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^ {0,3}> ?")]
    private static partial Regex QuoteRegex();

    /// <summary>The script of a page meant for printing: the browser's print dialog opens once the page has loaded.</summary>
    public const string PrintScript = "<script>window.addEventListener('load', function () { window.print(); });</script>\n";

    /// <summary>
    /// A whole page: <paramref name="body"/> from <see cref="Body"/> with the style sheet. <paramref name="print"/>:
    /// the page opens the browser's print dialog when loaded (its one inline script is allowed, nothing else).
    /// </summary>
    public static string Page(string title, string body, string? baseUri = null, bool print = false)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">\n");
        // Only local files and inline styles: nothing is fetched from the network.
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src file: data:; style-src 'unsafe-inline'")
          .Append(print ? "; script-src 'unsafe-inline'" : "").Append("\">\n");
        if (baseUri is not null) sb.Append("<base href=\"").Append(WebUtility.HtmlEncode(baseUri)).Append("\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head><body>\n");
        sb.Append(body);
        if (print) sb.Append(PrintScript);
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    /// <summary>Plain text as a page: one preformatted block.</summary>
    public static string PlainTextBody(string text) => "<pre class=\"plain\">" + WebUtility.HtmlEncode(text) + "</pre>\n";

    private const string Css = """
        @page { size: A4; margin: 18mm 16mm; }
        body { font-family: "Segoe UI", "Helvetica Neue", Arial, sans-serif; font-size: 11pt; line-height: 1.45; color: #1a1a1a; max-width: 52em; margin: 0 auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.2; margin: 1.2em 0 0.5em; page-break-after: avoid; }
        h1 { font-size: 1.9em; border-bottom: 1px solid #ccc; padding-bottom: 0.2em; } h2 { font-size: 1.5em; border-bottom: 1px solid #e0e0e0; padding-bottom: 0.15em; }
        h3 { font-size: 1.25em; } h4 { font-size: 1.1em; } h5 { font-size: 1em; } h6 { font-size: 0.95em; color: #555; }
        code, pre { font-family: Consolas, "DejaVu Sans Mono", monospace; font-size: 0.92em; }
        code { background: #f2f2f2; padding: 0.05em 0.3em; border-radius: 3px; }
        pre { background: #f6f6f6; padding: 0.7em 0.9em; border-radius: 4px; white-space: pre-wrap; word-wrap: break-word; }
        pre code { background: none; padding: 0; } pre.plain { background: none; padding: 0; }
        table { border-collapse: collapse; margin: 0.8em 0; page-break-inside: auto; } tr { page-break-inside: avoid; }
        th, td { border: 1px solid #bbb; padding: 0.3em 0.6em; vertical-align: top; } th { background: #f0f0f0; }
        blockquote { margin: 0.8em 0; padding: 0 1em; color: #444; border-left: 4px solid #ccc; }
        mark { background: #fff3a0; } hr { border: none; border-top: 1px solid #bbb; margin: 1.5em 0; }
        img { max-width: 100%; } a { color: #0b5cad; }
        li.task { list-style: none; } li.task > .box { display: inline-block; width: 1.3em; margin-left: -1.3em; } li.done { color: #777; }
        .footnotes { font-size: 0.9em; border-top: 1px solid #ccc; margin-top: 2em; }
        """;

    /// <summary>The HTML of a Markdown text (without the page around it).</summary>
    public static string Body(string markdown, bool trackSourceLines = false)
    {
        var source = new StringLineSource(markdown);
        var model = MarkdownDocument.Parse(source);
        var lines = Enumerable.Range(0, source.LineCount).Select(source.GetLine).ToArray();
        var footnotes = new List<(string Label, string Html)>();
        var sb = new StringBuilder();
        Blocks(lines, model, sb, footnotes, trackSourceLines ? 1 : null);
        if (footnotes.Count > 0)
        {
            sb.Append("<section class=\"footnotes\"><ol>\n");
            foreach (var (label, html) in footnotes)
                sb.Append("<li id=\"fn-").Append(Attr(label)).Append("\">").Append(html).Append("</li>\n");
            sb.Append("</ol></section>\n");
        }
        return sb.ToString();
    }

    private static string SourceLine(int? line) => line is int n ? $" data-source-line=\"{n}\"" : "";

    private static void Blocks(string[] lines, MarkdownDocument model, StringBuilder sb, List<(string, string)> footnotes,
        int? firstSourceLine = null)
    {
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            var r = model.GetLine(i);
            if (line.Trim().Length == 0) { i++; continue; }

            if (r.Kind == MdLineKind.FenceDelimiter)
            {
                string lang = line.Trim().TrimStart('`', '~').Trim();
                sb.Append("<pre").Append(SourceLine(firstSourceLine + i)).Append("><code")
                    .Append(lang.Length > 0 ? $" class=\"language-{Attr(lang.Split(' ')[0])}\"" : "").Append('>');
                i++;
                while (i < lines.Length && model.GetLine(i).Kind == MdLineKind.Code) sb.Append(WebUtility.HtmlEncode(lines[i++])).Append('\n');
                if (i < lines.Length && model.GetLine(i).Kind == MdLineKind.FenceDelimiter) i++;
                sb.Append("</code></pre>\n");
                continue;
            }
            if (r.TableIndex >= 0)
            {
                var table = model.Tables[r.TableIndex];
                Table(lines, model, table, sb, firstSourceLine);
                i = table.LastLine + 1;
                continue;
            }
            if (r.Kind == MdLineKind.Heading)
            {
                sb.Append("<h").Append(r.HeadingLevel).Append(SourceLine(firstSourceLine + i)).Append('>')
                    .Append(Inline(line, r, 0, line.Length, MdStyle.Bold)).Append("</h").Append(r.HeadingLevel).Append(">\n");
                i++;
                continue;
            }
            if (RuleRegex().IsMatch(line)) { sb.Append("<hr").Append(SourceLine(firstSourceLine + i)).Append(">\n"); i++; continue; }
            if (QuoteRegex().IsMatch(line))
            {
                int? quoteStart = firstSourceLine + i;
                var inner = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0)
                {
                    var q = QuoteRegex().Match(lines[i]);
                    if (q.Success) inner.Add(lines[i][q.Length..]);
                    else if (inner.Count > 0 && Continues(lines[i])) inner.Add(lines[i]); // a lazy continuation
                    else break;
                    i++;
                }
                var quoted = inner.ToArray();
                sb.Append("<blockquote").Append(SourceLine(quoteStart)).Append(">\n");
                Blocks(quoted, MarkdownDocument.Parse(string.Join("\n", quoted)), sb, footnotes, quoteStart);
                sb.Append("</blockquote>\n");
                continue;
            }
            if (r.FootnoteDefinition is { } label)
            {
                int colon = line.IndexOf("]:", StringComparison.Ordinal) + 2;
                footnotes.Add((label, Inline(line, r, colon, line.Length).Trim()));
                i++;
                continue;
            }
            if (ListItemRegex().IsMatch(line))
            {
                i = List(lines, model, i, sb, footnotes, firstSourceLine);
                continue;
            }

            // A paragraph: lines up to a blank line or the start of another block.
            sb.Append("<p").Append(SourceLine(firstSourceLine + i)).Append('>');
            bool first = true;
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                var lr = model.GetLine(i);
                if (!first && (lr.Kind != MdLineKind.Normal || lr.TableIndex >= 0 || RuleRegex().IsMatch(lines[i]) || QuoteRegex().IsMatch(lines[i])
                    || ListItemRegex().IsMatch(lines[i]) || lr.FootnoteDefinition is not null)) break;
                if (!first) sb.Append('\n');
                string text = lines[i];
                int start = text.Length - text.TrimStart().Length;
                bool hardBreak = text.EndsWith("  ", StringComparison.Ordinal) || (text.EndsWith('\\') && !text.EndsWith("\\\\", StringComparison.Ordinal));
                int end = text.EndsWith('\\') && hardBreak ? text.Length - 1 : text.TrimEnd().Length;
                sb.Append(Inline(text, lr, start, Math.Max(start, end)));
                if (hardBreak && i + 1 < lines.Length && lines[i + 1].Trim().Length > 0) sb.Append("<br>");
                first = false;
                i++;
            }
            sb.Append("</p>\n");
        }
    }

    /// <summary>A lazy continuation line of a quote or list item (not the start of another block).</summary>
    private static bool Continues(string line) => !RuleRegex().IsMatch(line) && !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private sealed class Item
    {
        public required int Indent;
        public required bool Ordered;
        public string? Start;
        public int? SourceLine;
        public List<string> Lines { get; } = [];
        public List<Item> Children { get; } = [];
    }

    /// <summary>A list (with nested lists, by indentation) starting at line <paramref name="i"/>; returns the line after it.</summary>
    private static int List(string[] lines, MarkdownDocument model, int i, StringBuilder sb, List<(string, string)> footnotes, int? firstSourceLine)
    {
        var roots = new List<Item>();
        var stack = new List<Item>();
        bool blank = false;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) { blank = true; i++; continue; }
            var m = ListItemRegex().Match(line);
            int indent = line.Length - line.TrimStart().Length;
            if (m.Success && !RuleRegex().IsMatch(line))
            {
                var item = new Item
                {
                    Indent = indent, Ordered = char.IsDigit(m.Groups[2].Value[0]),
                    Start = char.IsDigit(m.Groups[2].Value[0]) ? m.Groups[2].Value[..^1] : null,
                    SourceLine = firstSourceLine + i,
                };
                item.Lines.Add(line[m.Length..]);
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) roots.Add(item); else stack[^1].Children.Add(item);
                stack.Add(item);
                blank = false;
                i++;
                continue;
            }
            // Continuation of the last item: indented, or a lazy line right after it.
            if (stack.Count > 0 && (indent > stack[^1].Indent || !blank && Continues(line) && model.GetLine(i).Kind == MdLineKind.Normal && model.GetLine(i).TableIndex < 0))
            {
                stack[^1].Lines.Add(line.TrimStart());
                blank = false;
                i++;
                continue;
            }
            break;
        }
        Items(roots, sb, footnotes);
        return i;
    }

    private static void Items(List<Item> items, StringBuilder sb, List<(string, string)> footnotes)
    {
        int k = 0;
        while (k < items.Count)
        {
            bool ordered = items[k].Ordered;
            sb.Append(ordered ? "<ol" + (items[k].Start is { } s && s != "1" ? $" start=\"{Attr(s)}\"" : "") + ">\n" : "<ul>\n");
            while (k < items.Count && items[k].Ordered == ordered)
            {
                var item = items[k++];
                string text = string.Join("\n", item.Lines);
                bool task = text.Length >= 3 && text[0] == '[' && text[2] == ']' && text[1] is ' ' or 'x' or 'X' && (text.Length == 3 || char.IsWhiteSpace(text[3]));
                bool done = task && text[1] != ' ';
                if (task) text = text[3..].TrimStart();
                sb.Append("<li").Append(SourceLine(item.SourceLine));
                sb.Append(task ? done ? " class=\"task done\"><span class=\"box\">☑</span>" : " class=\"task\"><span class=\"box\">☐</span>" : ">");
                // The item's text: inline content of its lines (a nested block such as a table is rendered as blocks).
                var inner = text.Split('\n');
                var innerModel = MarkdownDocument.Parse(text);
                bool simple = true;
                for (int n = 0; n < inner.Length && simple; n++)
                    simple = innerModel.GetLine(n) is { Kind: MdLineKind.Normal, TableIndex: < 0 } && !ListItemRegex().IsMatch(inner[n]) && !QuoteRegex().IsMatch(inner[n]);
                if (simple)
                    for (int n = 0; n < inner.Length; n++)
                    {
                        if (n > 0) sb.Append('\n');
                        sb.Append(Inline(inner[n], innerModel.GetLine(n), 0, inner[n].TrimEnd().Length));
                    }
                else Blocks(inner, innerModel, sb, footnotes);
                if (item.Children.Count > 0) { sb.Append('\n'); Items(item.Children, sb, footnotes); }
                sb.Append("</li>\n");
            }
            sb.Append(ordered ? "</ol>\n" : "</ul>\n");
        }
    }

    private static void Table(string[] lines, MarkdownDocument model, MarkdownTable table, StringBuilder sb, int? firstSourceLine)
    {
        sb.Append("<table>\n");
        foreach (var row in table.Rows)
        {
            if (row.Kind == MdLineKind.TableSeparator) continue;
            bool header = row.Kind == MdLineKind.TableHeader;
            if (header) sb.Append("<thead>");
            else if (row.LineIndex == table.FirstLine + 2) sb.Append("<tbody>\n");
            sb.Append("<tr").Append(SourceLine(firstSourceLine + row.LineIndex)).Append('>');
            string line = lines[row.LineIndex];
            var r = model.GetLine(row.LineIndex);
            for (int c = 0; c < table.ColumnCount; c++)
            {
                string tag = header ? "th" : "td";
                string align = table.Alignments[c] switch
                {
                    ColumnAlignment.Left => " style=\"text-align:left\"",
                    ColumnAlignment.Center => " style=\"text-align:center\"",
                    ColumnAlignment.Right => " style=\"text-align:right\"",
                    _ => "",
                };
                sb.Append('<').Append(tag).Append(align).Append('>');
                if (c < row.Cells.Count)
                {
                    var cell = row.Cells[c];
                    // Header cells are bold anyway (th); the inline parser marks them bold too.
                    sb.Append(Inline(line, r, cell.ContentStart(line), cell.ContentEnd(line), header ? MdStyle.Bold : MdStyle.None));
                }
                sb.Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>");
            if (header) sb.Append("</thead>");
            sb.Append('\n');
        }
        if (table.Rows.Count > 2) sb.Append("</tbody>\n");
        sb.Append("</table>\n");
    }

    private static readonly (MdStyle Style, string Tag)[] Tags =
    [
        (MdStyle.Bold, "strong"), (MdStyle.Italic, "em"), (MdStyle.Strike, "del"), (MdStyle.Highlight, "mark"),
        (MdStyle.Subscript, "sub"), (MdStyle.Superscript, "sup"), (MdStyle.Code, "code"),
    ];

    /// <summary>HTML of the shown text of [<paramref name="from"/>, <paramref name="to"/>) of a line, with its styles.</summary>
    public static string Inline(string line, LineRender r, int from, int to, MdStyle ignore = MdStyle.None)
    {
        var sb = new StringBuilder();
        var open = new List<string>();
        MdStyle current = MdStyle.None;
        string? link = null;

        void CloseAll()
        {
            for (int k = open.Count - 1; k >= 0; k--) sb.Append("</").Append(open[k]).Append('>');
            open.Clear();
            current = MdStyle.None;
            link = null;
        }

        int i = from;
        while (i < to)
        {
            if (r.BreakAt(i) is { } br) { sb.Append("<br>"); i = br.End; continue; }
            if (r.IsHidden(i)) { i++; continue; }

            // Images and footnote references are written whole.
            if (Range(r, i, MdStyle.Image) is { } image)
            {
                if (image.Start == i)
                {
                    CloseAll();
                    string alt = Visible(line, r, image.Start, image.End);
                    sb.Append("<img src=\"").Append(Attr(image.Url ?? "")).Append("\" alt=\"").Append(Attr(alt)).Append("\">");
                }
                i = Math.Max(i + 1, image.End);
                continue;
            }
            if (Range(r, i, MdStyle.Footnote) is { Url: { } label } note)
            {
                CloseAll();
                sb.Append("<sup><a href=\"#fn-").Append(Attr(label)).Append("\">").Append(WebUtility.HtmlEncode(label)).Append("</a></sup>");
                i = Math.Max(i + 1, note.End);
                continue;
            }

            var style = MdStyle.None;
            string? url = null;
            foreach (var s in r.Styles)
                if (i >= s.Start && i < s.End)
                {
                    style |= s.Style;
                    if (s.Style.HasFlag(MdStyle.Link)) url = s.Url;
                }
            style &= ~(ignore | MdStyle.Heading | MdStyle.TaskDone | MdStyle.Link | MdStyle.Footnote | MdStyle.Image);
            if (style != current || url != link)
            {
                CloseAll();
                if (url is not null) { sb.Append("<a href=\"").Append(Attr(url)).Append("\">"); open.Add("a"); link = url; }
                foreach (var (st, tag) in Tags)
                    if (style.HasFlag(st)) { sb.Append('<').Append(tag).Append('>'); open.Add(tag); }
                current = style;
            }
            sb.Append(WebUtility.HtmlEncode(line[i].ToString()));
            i++;
        }
        CloseAll();
        return sb.ToString();
    }

    private static StyledRange? Range(LineRender r, int i, MdStyle style)
    {
        foreach (var s in r.Styles)
            if (s.Style.HasFlag(style) && i >= s.Start && i < s.End) return s;
        return null;
    }

    private static string Visible(string line, LineRender r, int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < to && i < line.Length; i++)
            if (!r.IsHidden(i)) sb.Append(line[i]);
        return sb.ToString();
    }

    private static string Attr(string s) => WebUtility.HtmlEncode(s);
}
