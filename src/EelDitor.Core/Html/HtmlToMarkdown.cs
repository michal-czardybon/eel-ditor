using System.Text;
using System.Text.RegularExpressions;

namespace EelDitor.Core.Html;

/// <summary>
/// Formatted text from a word processor (Word, Outlook, LibreOffice, Google Docs), as HTML on the clipboard, turned into
/// Markdown as far as Markdown goes: paragraphs, headings, bulleted and numbered lists (nested too, also Word's lists,
/// which are paragraphs with <c>mso-list</c> styles and the number typed in), tables, bold, italic, strikethrough,
/// code, highlight, sub- and superscript, links, quotes, code blocks and rules. Everything else (fonts, colours, sizes,
/// images, footnotes, comments) is left out; the text stays.
/// </summary>
public static partial class HtmlToMarkdown
{
    /// <summary>Whether the HTML comes from a word processor (not a web page): then a paste keeps its formatting.</summary>
    public static bool IsFromWordProcessor(string html) =>
        html.Contains("urn:schemas-microsoft-com:office", StringComparison.OrdinalIgnoreCase)
        || WordGenerator().IsMatch(html)
        || html.Contains("class=Mso", StringComparison.Ordinal) || html.Contains("class=\"Mso", StringComparison.Ordinal)
        || html.Contains("docs-internal-guid", StringComparison.Ordinal);

    [GeneratedRegex("<meta[^>]+name=\"?Generator\"?[^>]+content=\"?(Microsoft Word|LibreOffice|OpenOffice)", RegexOptions.IgnoreCase)]
    private static partial Regex WordGenerator();

    [GeneratedRegex(@"mso-list\s*:\s*l\d+\s+level(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MsoListLevel();

    [GeneratedRegex(@"^\s*(\d+|[a-zA-Z]|[ivxlcdmIVXLCDM]+)[.)]")]
    private static partial Regex OrderedMarker();

    [Flags]
    private enum Style { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8, Mark = 16, Sub = 32, Sup = 64 }

    /// <summary>A piece of inline text with one style; Text "\n" is a line break.</summary>
    private readonly record struct Span(string Text, Style Style, string? Link);

    private sealed record MdBlock(string Kind, string Text, int Level = 0, bool Ordered = false, int Number = 0);

    /// <summary>The Markdown for the HTML; lines separated by "\n".</summary>
    public static string Convert(string html)
    {
        var root = HtmlParser.Parse(html);
        var body = root.Find("body") ?? root;
        var blocks = new List<MdBlock>();
        Blocks(body, blocks, listLevel: 0);
        return Write(blocks);
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        { "head", "style", "script", "title", "xml", "meta", "link", "img", "svg", "object", "iframe", "video", "audio", "canvas", "button", "input", "select", "textarea" };

    private static readonly HashSet<string> BlockNames = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "table", "blockquote", "pre", "hr", "section",
        "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption", "dl", "dt", "dd", "address", "center",
    };

    private static bool IsBlock(HtmlNode n) => n is HtmlElement e && BlockNames.Contains(e.Name);

    /// <summary>The blocks of an element's children; inline content between blocks makes a paragraph.</summary>
    private static void Blocks(HtmlElement parent, List<MdBlock> blocks, int listLevel)
    {
        var inline = new List<HtmlNode>();
        void FlushInline()
        {
            if (inline.Count == 0) return;
            string text = Inline(inline);
            inline.Clear();
            if (text.Trim().Length > 0) blocks.Add(new MdBlock("p", EscapeLineStart(text.Trim())));
        }
        foreach (var node in parent.Children)
        {
            if (node is HtmlElement { Name: var skip } && Skipped.Contains(skip)) continue;
            if (!IsBlock(node))
            {
                // An inline element around blocks (Google Docs wraps the whole text in <b style="font-weight:normal">)
                // is only a container.
                if (node is HtmlElement wrapper && wrapper.Descendants().Any(IsBlock))
                {
                    FlushInline();
                    Blocks(wrapper, blocks, listLevel);
                }
                else inline.Add(node);
                continue;
            }
            FlushInline();
            Block((HtmlElement)node, blocks, listLevel);
        }
        FlushInline();
    }

    private static void Block(HtmlElement e, List<MdBlock> blocks, int listLevel)
    {
        switch (e.Name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                string heading = Inline(e.Children, Style.Bold).Trim().Replace("\n", " ");
                if (heading.Length > 0) blocks.Add(new MdBlock("h", heading, e.Name[1] - '0'));
                return;
            case "p" or "div" when MsoListLevel().Match(e.Attr("style") ?? "") is { Success: true } m:
                WordListItem(e, int.Parse(m.Groups[1].Value), blocks);
                return;
            case "p":
                string text = Inline(e.Children).Trim();
                if (text.Length > 0) blocks.Add(new MdBlock("p", EscapeLineStart(text)));
                return;
            case "ul" or "ol":
            {
                int number = int.TryParse(e.Attr("start"), out int start) ? start : 1;
                foreach (var li in e.Elements.Where(c => c.Name == "li"))
                {
                    var (own, nested) = (li.Children.Where(c => c is not HtmlElement { Name: "ul" or "ol" }).ToList(),
                        li.Elements.Where(c => c.Name is "ul" or "ol").ToList());
                    // The item's own text (its paragraphs joined), then its nested lists one level deeper.
                    var parts = new List<MdBlock>();
                    var holder = new HtmlElement("div");
                    foreach (var c in own) holder.Children.Add(c);
                    Blocks(holder, parts, listLevel + 1);
                    string item = string.Join(" ", parts.Select(p => p.Text.Replace("\n", " "))).Trim();
                    blocks.Add(new MdBlock("li", item, listLevel + 1, e.Name == "ol", number++));
                    foreach (var list in nested) Block(list, blocks, listLevel + 1);
                }
                return;
            }
            case "li":
                blocks.Add(new MdBlock("li", Inline(e.Children).Trim(), listLevel + 1));
                return;
            case "table":
                if (Table(e) is { } table) blocks.Add(table);
                return;
            case "blockquote":
            {
                var inner = new List<MdBlock>();
                Blocks(e, inner, listLevel);
                string quote = Write(inner);
                if (quote.Length > 0) blocks.Add(new MdBlock("raw", string.Join("\n", quote.Split('\n').Select(l => l.Length > 0 ? "> " + l : ">"))));
                return;
            }
            case "pre":
                string code = e.InnerText.Replace("\r\n", "\n").Trim('\n');
                if (code.Length > 0) blocks.Add(new MdBlock("raw", "```\n" + code + "\n```"));
                return;
            case "hr":
                blocks.Add(new MdBlock("raw", "---"));
                return;
            default:
                Blocks(e, blocks, listLevel);
                return;
        }
    }

    /// <summary>
    /// A paragraph Word marks as a list item: the number or bullet Word typed in (in a span with
    /// <c>mso-list:Ignore</c>) tells a numbered list from a bulleted one and is left out of the text.
    /// </summary>
    private static void WordListItem(HtmlElement p, int level, List<MdBlock> blocks)
    {
        string? marker = null;
        var ignore = p.Descendants().FirstOrDefault(d => (d.Attr("style") ?? "").Replace(" ", "").Contains("mso-list:Ignore", StringComparison.OrdinalIgnoreCase));
        if (ignore is not null)
        {
            marker = ignore.InnerText.Replace('\u00A0', ' ').Trim();
            ignore.Parent?.Children.Remove(ignore);
        }
        var ordered = marker is not null && OrderedMarker().Match(marker) is { Success: true } om ? om : null;
        int number = ordered is not null && int.TryParse(ordered.Groups[1].Value, out int n) ? n : 0;
        blocks.Add(new MdBlock("li", Inline(p.Children).Trim(), Math.Max(1, level), ordered is not null, number));
    }

    private static MdBlock? Table(HtmlElement table)
    {
        var rows = new List<(List<string> Cells, bool Header, bool AllBold)>();
        foreach (var tr in table.Descendants().Where(d => d.Name == "tr" && ReferenceEquals(Closest(d, "table"), table)))
        {
            var cells = new List<string>();
            bool header = tr.Parent?.Name == "thead", allBold = true, any = false;
            foreach (var td in tr.Elements.Where(c => c.Name is "td" or "th"))
            {
                header |= td.Name == "th";
                // A cell's paragraphs and list items are lines of the cell (<br> in Markdown).
                var parts = new List<MdBlock>();
                Blocks(td, parts, 0);
                string text = string.Join("\n", parts.Select(b => b.Kind == "li" ? (b.Ordered ? b.Number + ". " : "• ") + b.Text : b.Text.Replace("\n", " ")));
                if (parts.Count == 0) text = Inline(td.Children).Trim();
                if (text.Length > 0) { any = true; allBold &= IsAllBold(text); }
                cells.Add(text);
            }
            if (cells.Count > 0) rows.Add((cells, header, any && allBold));
        }
        if (rows.Count == 0) return null;
        int columns = rows.Max(r => r.Cells.Count);
        // The first row is the header when marked as one, or when it is all bold (Word's header rows usually are).
        bool hasHeader = rows[0].Header || rows.Count > 1 && rows[0].AllBold;
        var lines = new List<string>();
        var head = hasHeader ? rows[0].Cells.Select(c => rows[0].AllBold && !rows[0].Header ? Unbold(c) : c).ToList() : [];
        lines.Add(Row(head, columns));
        lines.Add("|" + string.Concat(Enumerable.Repeat("---|", columns)));
        foreach (var r in rows.Skip(hasHeader ? 1 : 0)) lines.Add(Row(r.Cells, columns));
        return new MdBlock("raw", string.Join("\n", lines));
    }

    private static HtmlElement? Closest(HtmlElement e, string name)
    {
        for (var p = e.Parent; p is not null; p = p.Parent) if (p.Name == name) return p;
        return null;
    }

    private static bool IsAllBold(string text) => text.Split('\n').All(l => l.Length > 4 && l.StartsWith("**", StringComparison.Ordinal) && l.EndsWith("**", StringComparison.Ordinal));

    private static string Unbold(string text) => string.Join("\n", text.Split('\n').Select(l => l[2..^2]));

    private static string Row(List<string> cells, int columns)
    {
        var list = cells.Select(c => c.Replace("|", "\\|").Replace("\n", "<br>").Trim()).ToList();
        while (list.Count < columns) list.Add("");
        return "| " + string.Join(" | ", list) + " |";
    }

    /// <summary>The blocks as Markdown: blank lines between blocks, list items of one list together.</summary>
    private static string Write(List<MdBlock> blocks)
    {
        var sb = new StringBuilder();
        var counters = new Dictionary<int, int>();
        var indents = new List<int>(); // the text indent of each open list level
        MdBlock? previous = null;
        foreach (var b in blocks)
        {
            // Items of one list go together; a list of the other kind (bullets after numbers) is a new list.
            bool listContinues = b.Kind == "li" && previous?.Kind == "li"
                && !(b.Level == 1 && previous.Level == 1 && b.Ordered != previous.Ordered);
            if (sb.Length > 0) sb.Append(listContinues ? "\n" : "\n\n");
            if (b.Kind != "li" || !listContinues) { counters.Clear(); indents.Clear(); }
            switch (b.Kind)
            {
                case "h":
                    sb.Append(new string('#', Math.Clamp(b.Level, 1, 6))).Append(' ').Append(b.Text);
                    break;
                case "li":
                {
                    int level = Math.Max(1, b.Level);
                    foreach (var deeper in counters.Keys.Where(k => k > level).ToList()) counters.Remove(deeper);
                    // A nested item starts under the text of the nearest item above it of a lower level.
                    if (indents.Count > level - 1) indents.RemoveRange(level - 1, indents.Count - (level - 1));
                    int indent = level == 1 || indents.Count == 0 ? 0 : indents[^1];
                    string marker;
                    if (b.Ordered)
                    {
                        int number = b.Number > 0 && !counters.ContainsKey(level) ? b.Number
                            : counters.TryGetValue(level, out int last) ? last + 1 : Math.Max(1, b.Number);
                        counters[level] = number;
                        marker = number + ". ";
                    }
                    else { counters.Remove(level); marker = "- "; }
                    indents.Add(indent + marker.Length);
                    string pad = new(' ', indent);
                    sb.Append(pad).Append(marker).Append(b.Text.Replace("\n", "\n" + new string(' ', indent + marker.Length)));
                    break;
                }
                default:
                    sb.Append(b.Text);
                    break;
            }
            previous = b;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- inline content

    private static string Inline(IEnumerable<HtmlNode> nodes, Style hidden = Style.None)
    {
        var spans = new List<Span>();
        foreach (var n in nodes) Collect(n, Style.None, null, spans);
        return Serialize(spans, hidden);
    }

    private static void Collect(HtmlNode node, Style style, string? link, List<Span> spans)
    {
        if (node is HtmlText t)
        {
            string text = Regex.Replace(t.Text.Replace('\u00A0', ' '), @"\s+", " ");
            if (text.Length > 0) spans.Add(new Span(text, style, link));
            return;
        }
        if (node is not HtmlElement e || Skipped.Contains(e.Name)) return;
        string css = (e.Attr("style") ?? "").Replace(" ", "").ToLowerInvariant();
        if (css.Contains("mso-list:ignore") || css.Contains("display:none")) return;
        switch (e.Name)
        {
            case "br": spans.Add(new Span("\n", style, link)); return;
            case "b" or "strong": style |= Style.Bold; break;
            case "i" or "em" or "cite" or "dfn": style |= Style.Italic; break;
            case "s" or "strike" or "del": style |= Style.Strike; break;
            case "code" or "kbd" or "samp" or "tt": style |= Style.Code; break;
            case "mark": style |= Style.Mark; break;
            case "sub": style |= Style.Sub; break;
            case "sup": style |= Style.Sup; break;
            case "a" when e.Attr("href") is { Length: > 0 } href && !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase):
                link = href;
                break;
        }
        // Styles given by CSS (Google Docs, LibreOffice): bold by weight, italic, strikethrough; "normal" undoes them.
        if (css.Contains("font-weight:bold") || css.Contains("font-weight:700") || css.Contains("font-weight:600")) style |= Style.Bold;
        else if (css.Contains("font-weight:normal") || css.Contains("font-weight:400")) style &= ~Style.Bold;
        if (css.Contains("font-style:italic")) style |= Style.Italic;
        else if (css.Contains("font-style:normal")) style &= ~Style.Italic;
        if (css.Contains("line-through")) style |= Style.Strike;
        if (css.Contains("vertical-align:super")) style |= Style.Sup;
        if (css.Contains("vertical-align:sub")) style |= Style.Sub;
        foreach (var c in e.Children) Collect(c, style, link, spans);
    }

    private static readonly (Style Style, string Marker)[] Markers =
        [(Style.Bold, "**"), (Style.Italic, "*"), (Style.Strike, "~~"), (Style.Mark, "=="), (Style.Sub, "~"), (Style.Sup, "^")];

    /// <summary>
    /// The spans as Markdown: neighbouring spans of the same style and link are one run; markers hug the text (spaces
    /// go outside them); styles in <paramref name="hidden"/> are not written (a heading is bold by itself).
    /// </summary>
    private static string Serialize(List<Span> spans, Style hidden)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < spans.Count)
        {
            var (style, link) = (spans[i].Style & ~hidden, spans[i].Link);
            var run = new StringBuilder();
            while (i < spans.Count && (spans[i].Style & ~hidden) == style && spans[i].Link == link)
            {
                run.Append(style.HasFlag(Style.Code) ? spans[i].Text : Escape(spans[i].Text));
                i++;
            }
            string text = run.ToString();
            string trimmed = text.Trim(' ');
            if (trimmed.Length == 0 || trimmed == "\n") { sb.Append(text); continue; }
            string lead = text[..(text.Length - text.TrimStart(' ').Length)], trail = text[text.TrimEnd(' ').Length..];
            string body = trimmed;
            if (style.HasFlag(Style.Code)) body = "`" + body + "`";
            foreach (var (s, marker) in Markers)
                if (style.HasFlag(s)) body = marker + body + marker;
            if (link is not null) body = "[" + body + "](" + link.Replace(" ", "%20").Replace(")", "%29") + ")";
            sb.Append(lead).Append(body).Append(trail);
        }
        // Line breaks: no spaces around them.
        return Regex.Replace(sb.ToString(), @" *\n *", "\n");
    }

    /// <summary>Characters Markdown would read as markup, in plain text.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '*' or '_' or '`' or '\\' or '[' or ']') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A paragraph starting like a heading, a quote or a list item gets a backslash, so it stays a paragraph.</summary>
    private static string EscapeLineStart(string text) =>
        Regex.IsMatch(text, @"^(#{1,6}\s|>|[-+]\s|\d+[.)]\s)") ? (char.IsDigit(text[0]) ? Regex.Replace(text, @"^(\d+)([.)])", "$1\\$2") : "\\" + text) : text;
}
