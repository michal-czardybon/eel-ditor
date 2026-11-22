using System.Text.RegularExpressions;

namespace EelDitor.Markdown;

/// <summary>
/// Display model of a Markdown document: per-line hidden markup, styles and virtual table padding.
/// The source text is never modified by this model.
/// </summary>
public sealed partial class MarkdownDocument
{
    private readonly LineRender[] _lines;

    public IReadOnlyList<MarkdownTable> Tables { get; }

    private MarkdownDocument(LineRender[] lines, List<MarkdownTable> tables)
    {
        _lines = lines;
        Tables = tables;
    }

    public int LineCount => _lines.Length;

    public LineRender GetLine(int index) => index >= 0 && index < _lines.Length ? _lines[index] : LineRender.Plain;

    public MarkdownTable? TableAtLine(int line)
    {
        var r = GetLine(line);
        return r.TableIndex >= 0 ? Tables[r.TableIndex] : null;
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+|$)")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*(?:>\s*)*(?:[-*+]|\d{1,9}[.)])\s+)\[([ xX])\](?=\s|$)")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex SeparatorRegex();

    public static MarkdownDocument Parse(string text) => Parse(new StringLineSource(text));

    public static MarkdownDocument Parse(ILineSource source)
    {
        int n = source.LineCount;
        var lines = new LineRender[n];
        var tables = new List<MarkdownTable>();
        string? fence = null;

        // YAML front matter (--- … --- or ...): data, shown as it is.
        int frontMatter = -1;
        if (n > 1 && source.GetLine(0).TrimEnd() == "---")
            for (int k = 1; k < Math.Min(n, 500); k++)
                if (source.GetLine(k).TrimEnd() is "---" or "...") { frontMatter = k; break; }
        for (int k = 0; k <= frontMatter; k++) lines[k] = LineRender.Plain;

        for (int i = frontMatter + 1; i < n; i++)
        {
            string line = source.GetLine(i);

            if (fence is not null)
            {
                var m = FenceRegex().Match(line);
                if (m.Success && m.Groups[1].Value[0] == fence[0] && m.Groups[1].Length >= fence.Length && line.Trim().Trim(fence[0]).Length == 0)
                {
                    fence = null;
                    lines[i] = new LineRender { Kind = MdLineKind.FenceDelimiter };
                }
                else lines[i] = new LineRender { Kind = MdLineKind.Code };
                continue;
            }

            var fm = FenceRegex().Match(line);
            if (fm.Success)
            {
                fence = fm.Groups[1].Value;
                lines[i] = new LineRender { Kind = MdLineKind.FenceDelimiter };
                continue;
            }

            if (i + 1 < n && line.Contains('|') && TryParseTable(source, i, tables.Count, out var table))
            {
                tables.Add(table);
                foreach (var row in table.Rows)
                {
                    string rowText = source.GetLine(row.LineIndex);
                    var render = new LineRender { Kind = row.Kind, TableIndex = tables.Count - 1 };
                    if (row.Kind != MdLineKind.TableSeparator)
                    {
                        foreach (var cell in row.Cells)
                        {
                            // A header cell is bold as a whole (as the grid draws it), not only its styled parts.
                            if (row.Kind == MdLineKind.TableHeader && cell.ContentEnd(rowText) > cell.ContentStart(rowText))
                                render.Styles.Add(new StyledRange(cell.ContentStart(rowText), cell.ContentEnd(rowText), MdStyle.Bold));
                            InlineParser.Parse(rowText, cell.Start, cell.End, render,
                                row.Kind == MdLineKind.TableHeader ? MdStyle.Bold : MdStyle.None);
                        }
                    }
                    lines[row.LineIndex] = render;
                }
                TableLayout.ComputePaddings(table, source, lines);
                i = table.LastLine;
                continue;
            }

            // A horizontal rule; "---" right under a paragraph line is a setext heading underline instead (left as text).
            if (RuleRegex().IsMatch(line) && !(line.TrimStart()[0] == '-' && i > 0 && source.GetLine(i - 1).Trim().Length > 0
                    && lines[i - 1] is { Kind: MdLineKind.Normal, TableIndex: < 0 } && ListEdit.Parse(source.GetLine(i - 1)) is null))
            {
                lines[i] = new LineRender { Kind = MdLineKind.Rule };
                continue;
            }

            var hm = HeadingRegex().Match(line);
            if (hm.Success)
            {
                var render = new LineRender { Kind = MdLineKind.Heading, HeadingLevel = hm.Groups[1].Length };
                render.Hidden.Add(new HiddenRange(0, hm.Length));
                int contentEnd = line.Length;
                // Optional closing sequence of #'s.
                int t = line.TrimEnd().Length;
                int h = t;
                while (h > hm.Length && line[h - 1] == '#') h--;
                if (h < t && h > hm.Length && line[h - 1] == ' ')
                {
                    render.Hidden.Add(new HiddenRange(h - 1, line.Length - (h - 1)));
                    contentEnd = h - 1;
                }
                render.Styles.Add(new StyledRange(hm.Length, contentEnd, MdStyle.Heading | MdStyle.Bold));
                InlineParser.Parse(line, hm.Length, contentEnd, render, MdStyle.Heading | MdStyle.Bold);
                lines[i] = render;
                continue;
            }

            var task = line.Length > 4 ? TaskRegex().Match(line) : Match.Empty;
            if (task.Success)
            {
                int box = task.Groups[1].Length;
                bool done = task.Groups[2].Value != " ";
                int bullet = line.AsSpan(0, box).TrimEnd().Length - 1;
                int start = line[bullet] is '-' or '*' or '+' ? bullet : box;
                var tr = new LineRender { Kind = MdLineKind.Normal, TaskColumn = box, TaskStart = start, TaskChecked = done };
                InlineParser.Parse(line, box + 3, line.Length, tr);
                if (done) tr.Styles.Add(new StyledRange(box + 3, line.Length, MdStyle.TaskDone));
                lines[i] = tr;
                continue;
            }

            if (line.Length == 0 || !HasInlineCandidates(line)) { lines[i] = LineRender.Plain; continue; }
            string? footnote = null;
            if (line.StartsWith("[^", StringComparison.Ordinal))
            {
                int close = line.IndexOf("]:", StringComparison.Ordinal);
                if (close > 2 && line.AsSpan(2, close - 2).IndexOfAny(" \t[]") < 0) footnote = line[2..close];
            }
            var r = new LineRender { Kind = MdLineKind.Normal, FootnoteDefinition = footnote };
            InlineParser.Parse(line, 0, line.Length, r);
            lines[i] = r.Hidden.Count == 0 && r.Styles.Count == 0 && footnote is null ? LineRender.Plain : r;
        }

        return new MarkdownDocument(lines, tables);
    }

    private static bool HasInlineCandidates(string line) =>
        line.AsSpan().IndexOfAny("*_~`[=\\<^") >= 0
        // Bare web addresses (UrlFinder): "https://", "http://", "mailto:", "www."
        || line.Contains("://", StringComparison.Ordinal) || line.Contains("mailto:", StringComparison.OrdinalIgnoreCase)
        || line.Contains("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Line index of the definition <c>[^label]: …</c>, or -1.</summary>
    public int FootnoteDefinitionLine(string label)
    {
        for (int i = 0; i < _lines.Length; i++)
            if (_lines[i].FootnoteDefinition == label) return i;
        return -1;
    }

    private static bool TryParseTable(ILineSource source, int headerLine, int tableIndex, out MarkdownTable table)
    {
        table = null!;
        string header = source.GetLine(headerLine);
        string sep = source.GetLine(headerLine + 1);
        if (!sep.Contains('-') || !SeparatorRegex().IsMatch(sep)) return false;

        var headerRow = SplitRow(header, headerLine, MdLineKind.TableHeader);
        var sepRow = SplitRow(sep, headerLine + 1, MdLineKind.TableSeparator);
        if (headerRow.Cells.Count != sepRow.Cells.Count || headerRow.Cells.Count == 0) return false;

        table = new MarkdownTable { FirstLine = headerLine };
        table.Rows.Add(headerRow);
        table.Rows.Add(sepRow);
        foreach (var cell in sepRow.Cells)
        {
            var s = sep[cell.Start..cell.End].Trim();
            bool left = s.StartsWith(':'), right = s.EndsWith(':');
            table.Alignments.Add(left && right ? ColumnAlignment.Center : right ? ColumnAlignment.Right : left ? ColumnAlignment.Left : ColumnAlignment.None);
        }

        for (int i = headerLine + 2; i < source.LineCount; i++)
        {
            string line = source.GetLine(i);
            if (line.Trim().Length == 0 || !line.Contains('|')) break;
            if (FenceRegex().IsMatch(line)) break;
            table.Rows.Add(SplitRow(line, i, MdLineKind.TableBody));
        }
        return true;
    }

    /// <summary>Splits a table row on unescaped pipes that are not inside code spans.</summary>
    public static TableRow SplitRow(string line, int lineIndex, MdLineKind kind)
    {
        var pipes = new List<int>();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\') { i++; continue; }
            if (c == '`')
            {
                int run = 0;
                while (i + run < line.Length && line[i + run] == '`') run++;
                int close = line.IndexOf(new string('`', run), i + run, StringComparison.Ordinal);
                if (close > 0) { i = close + run - 1; continue; }
                i += run - 1;
                continue;
            }
            if (c == '|') pipes.Add(i);
        }

        int firstNonSpace = 0;
        while (firstNonSpace < line.Length && line[firstNonSpace] is ' ' or '\t') firstNonSpace++;
        bool leading = pipes.Count > 0 && pipes[0] == firstNonSpace;
        var row = new TableRow
        {
            LineIndex = lineIndex,
            Kind = kind,
            HasLeadingPipe = leading,
            LeadingPipe = leading ? pipes[0] : -1,
        };

        int cellStart = leading ? pipes[0] + 1 : 0;
        for (int p = leading ? 1 : 0; p < pipes.Count; p++)
        {
            row.Cells.Add(new TableCell(cellStart, pipes[p], pipes[p]));
            cellStart = pipes[p] + 1;
        }
        // Trailing cell without a closing pipe (only if it has content).
        if (cellStart < line.Length && line.AsSpan(cellStart).Trim().Length > 0)
            row.Cells.Add(new TableCell(cellStart, line.Length, -1));
        return row;
    }
}
