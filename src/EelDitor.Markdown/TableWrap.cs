using System.Text;

namespace EelDitor.Markdown;

/// <summary>
/// Layout of a table drawn as a grid whose cells wrap their text (used when the editor wraps lines):
/// column widths that fit the available width, and each cell's text broken into lines of that width.
/// Widths are in monospace cells. The grid of a row with columns w0, w1… is
/// <c>│ w0 │ w1 │</c>: each column takes its width plus 3 (two spaces and a border), plus one final border.
/// </summary>
public static class TableWrap
{
    public const int MinColumnWidth = 4;

    /// <summary>The text a cell shows: its content without the hidden markup; a <c>&lt;br&gt;</c> is a new line.</summary>
    public static string CellText(string line, TableCell cell, LineRender render)
    {
        int start = cell.ContentStart(line), end = cell.ContentEnd(line);
        if (end <= start) return "";
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
        {
            if (render.BreakAt(i) is { } br) { TrimEndSpaces(sb); sb.Append('\n'); i = br.End - 1; SkipSpaces(line, ref i, end); continue; }
            if (render.IsHidden(i)) continue;
            if (line[i] == '\\' && i + 1 < end && line[i + 1] == '|') continue; // \| shows as |
            sb.Append(line[i]);
        }
        return sb.ToString();
    }

    /// <summary>Total grid width for the given column widths.</summary>
    public static int GridWidth(IReadOnlyList<int> widths) => widths.Sum(w => w + 3) + 1;

    /// <summary>Character position of the border before column <paramref name="column"/> (and after the last one).</summary>
    public static int BorderAt(IReadOnlyList<int> widths, int column)
    {
        int x = 0;
        for (int c = 0; c < column; c++) x += widths[c] + 3;
        return x;
    }

    /// <summary>
    /// Column widths that fit <paramref name="available"/> cells: the natural widths when they fit; otherwise
    /// narrow columns keep their width and the wide ones share the rest equally (as wide as possible).
    /// </summary>
    public static int[] ColumnWidths(IReadOnlyList<int> natural, int available)
    {
        int n = natural.Count;
        var widths = natural.Select(w => Math.Max(1, w)).ToArray();
        int content = available - (n * 3 + 1);
        if (n == 0 || widths.Sum() <= content) return widths;
        var open = Enumerable.Range(0, n).ToList();
        int remaining = Math.Max(content, n * MinColumnWidth);
        bool changed = true;
        while (changed && open.Count > 0)
        {
            changed = false;
            int share = remaining / open.Count;
            foreach (int c in open.ToList())
            {
                if (widths[c] > share) continue;
                remaining -= widths[c];
                open.Remove(c);
                changed = true;
            }
        }
        if (open.Count == 0) return widths;
        int each = Math.Max(MinColumnWidth, remaining / open.Count);
        int extra = Math.Max(0, remaining - each * open.Count);
        foreach (int c in open) widths[c] = each + (extra-- > 0 ? 1 : 0);
        return widths;
    }

    /// <summary>Word wrap to <paramref name="width"/> cells; a word longer than the width is cut.</summary>
    public static List<string> WrapCell(string text, int width) =>
        WrapRanges(text, width).Select(r => text.Substring(r.Start, r.Length)).ToList();

    /// <summary>
    /// Word wrap as ranges of <paramref name="text"/> (so styles can follow the text): lines break at spaces,
    /// the spaces at a break are dropped, a word longer than the width is cut; '\n' (a <c>&lt;br&gt;</c>) always breaks.
    /// </summary>
    public static List<(int Start, int Length)> WrapRanges(string text, int width)
    {
        if (text.Contains('\n'))
        {
            var all = new List<(int, int)>();
            int from = 0;
            while (true)
            {
                int nl = text.IndexOf('\n', from);
                int to = nl < 0 ? text.Length : nl;
                foreach (var (s0, l0) in WrapRanges(text[from..to], width)) all.Add((from + s0, l0));
                if (nl < 0) break;
                from = nl + 1;
            }
            return all;
        }

        width = Math.Max(1, width);
        var lines = new List<(int, int)>();
        int lineStart = -1, lineEnd = -1, lineWidth = 0, i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ') { i++; continue; }
            int ws = i;
            while (i < text.Length && text[i] != ' ') i++;
            int we = i;
            int ww = TextWidth.Of(text.AsSpan(ws, we - ws));
            if (lineStart >= 0)
            {
                int gap = TextWidth.Of(text.AsSpan(lineEnd, ws - lineEnd));
                if (lineWidth + gap + ww <= width) { lineEnd = we; lineWidth += gap + ww; continue; }
                lines.Add((lineStart, lineEnd - lineStart));
                lineStart = -1;
            }
            while (ww > width) // a long word (URL): cut it
            {
                int take = 0, taken = 0;
                while (ws + take < we && taken + TextWidth.Of(text.AsSpan(ws + take, 1)) <= width) taken += TextWidth.Of(text.AsSpan(ws + take++, 1));
                take = Math.Max(1, take);
                lines.Add((ws, take));
                ws += take;
                ww = TextWidth.Of(text.AsSpan(ws, we - ws));
            }
            if (we > ws) { lineStart = ws; lineEnd = we; lineWidth = ww; }
        }
        if (lineStart >= 0) lines.Add((lineStart, lineEnd - lineStart));
        if (lines.Count == 0) lines.Add((0, 0));
        return lines;
    }

    /// <summary>The text a cell shows and the Markdown style of every shown character.</summary>
    public static (string Text, MdStyle[] Styles) StyledCellText(string line, TableCell cell, LineRender render)
    {
        int start = cell.ContentStart(line), end = cell.ContentEnd(line);
        var sb = new StringBuilder();
        var styles = new List<MdStyle>();
        for (int i = start; i < end; i++)
        {
            if (render.BreakAt(i) is { } br)
            {
                while (sb.Length > 0 && sb[^1] == ' ') { sb.Length--; styles.RemoveAt(styles.Count - 1); }
                sb.Append('\n');
                styles.Add(MdStyle.None);
                i = br.End - 1;
                SkipSpaces(line, ref i, end);
                continue;
            }
            if (render.IsHidden(i)) continue;
            if (line[i] == '\\' && i + 1 < end && line[i + 1] == '|') continue;
            var style = MdStyle.None;
            foreach (var s in render.Styles)
                if (i >= s.Start && i < s.End) style |= s.Style;
            sb.Append(line[i]);
            styles.Add(style);
        }
        return (sb.ToString(), styles.ToArray());
    }

    /// <summary>
    /// For every character of <see cref="StyledCellText"/>, the column in the source line it comes from (a
    /// <c>&lt;br&gt;</c>'s new line maps to the tag), so a range of the shown text can be turned into a source range.
    /// </summary>
    public static List<int> ShownToSource(string line, TableCell cell, LineRender render)
    {
        int start = cell.ContentStart(line), end = cell.ContentEnd(line);
        var map = new List<int>();
        var shown = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (render.BreakAt(i) is { } br)
            {
                while (shown.Length > 0 && shown[^1] == ' ') { shown.Length--; map.RemoveAt(map.Count - 1); }
                shown.Append('\n');
                map.Add(i);
                i = br.End - 1;
                SkipSpaces(line, ref i, end);
                continue;
            }
            if (render.IsHidden(i)) continue;
            if (line[i] == '\\' && i + 1 < end && line[i + 1] == '|') continue;
            shown.Append(line[i]);
            map.Add(i);
        }
        return map;
    }

    private static void TrimEndSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    /// <summary>Moves <paramref name="i"/> (the last character handled) over the spaces that follow it.</summary>
    private static void SkipSpaces(string line, ref int i, int end)
    {
        while (i + 1 < end && line[i + 1] == ' ') i++;
    }

    /// <summary>The widest line of a cell's text (its lines are separated by '\n').</summary>
    public static int WidestLine(string text)
    {
        int widest = 0;
        foreach (var part in text.Split('\n')) widest = Math.Max(widest, TextWidth.Of(part));
        return widest;
    }

    /// <summary>The natural (unwrapped) width of every column of a table.</summary>
    public static int[] NaturalWidths(MarkdownTable table, ILineSource source, Func<int, LineRender> render)
    {
        var widths = new int[table.ColumnCount];
        foreach (var row in table.Rows)
        {
            if (row.Kind == MdLineKind.TableSeparator) continue;
            string line = source.GetLine(row.LineIndex);
            var r = render(row.LineIndex);
            for (int c = 0; c < Math.Min(row.Cells.Count, widths.Length); c++)
                widths[c] = Math.Max(widths[c], WidestLine(CellText(line, row.Cells[c], r)));
        }
        return widths;
    }
}
