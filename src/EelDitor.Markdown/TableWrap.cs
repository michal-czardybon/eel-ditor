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

    /// <summary>What a column holds, for sharing the width: the widths of the words of every cell, row by row.</summary>
    public sealed class ColumnText
    {
        /// <summary>For each row, the cell's lines (a <c>&lt;br&gt;</c> starts a line), each as the widths of its words.</summary>
        public List<int[][]> Cells { get; } = [];

        /// <summary>The widest line (the width without wrapping).</summary>
        public int Natural { get; set; }

        /// <summary>The longest word: narrower than this, a word is cut.</summary>
        public int LongestWord { get; set; }

        /// <summary>All the text, in columns (for sharing the width in proportion to it).</summary>
        public int Amount { get; set; }
    }

    /// <summary>The text of every column of a table (header and body rows), for <see cref="ColumnWidths(IReadOnlyList{ColumnText}, int)"/>.</summary>
    public static ColumnText[] ColumnTexts(MarkdownTable table, ILineSource source, Func<int, LineRender> render)
    {
        var columns = Enumerable.Range(0, table.ColumnCount).Select(_ => new ColumnText()).ToArray();
        foreach (var row in table.Rows)
        {
            if (row.Kind == MdLineKind.TableSeparator) continue;
            string line = source.GetLine(row.LineIndex);
            var r = render(row.LineIndex);
            for (int c = 0; c < columns.Length; c++)
            {
                string text = c < row.Cells.Count ? CellText(line, row.Cells[c], r) : "";
                var lines = text.Split('\n').Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => TextWidth.Of(w)).ToArray()).ToArray();
                var column = columns[c];
                column.Cells.Add(lines);
                foreach (var words in lines)
                {
                    int width = words.Sum() + Math.Max(0, words.Length - 1);
                    column.Natural = Math.Max(column.Natural, width);
                    column.Amount += width;
                    if (words.Length > 0) column.LongestWord = Math.Max(column.LongestWord, words.Max());
                }
            }
        }
        return columns;
    }

    /// <summary>
    /// Column widths that fit <paramref name="available"/> cells, sharing the space by how much text each column has:
    /// <list type="number">
    /// <item>The natural widths when they fit.</item>
    /// <item>Otherwise every column gets at least its longest word (up to an equal share of the width, so a long URL
    /// does not take it all) and at most its natural width; the rest is shared in proportion to the amount of text,
    /// so a column of long descriptions gets more room than a column of short notes (equal shares made the long
    /// texts tall and left the short ones half empty).</item>
    /// <item>Then single columns of width move between columns while that makes the table shorter (the tallest cell
    /// of each row, wrapped as the grid wraps it), which helps rows with one long cell.</item>
    /// <item>The old equal shares are kept when they still give a shorter table.</item>
    /// </list>
    /// </summary>
    public static int[] ColumnWidths(IReadOnlyList<ColumnText> columns, int available)
    {
        int n = columns.Count;
        var natural = columns.Select(c => Math.Max(1, c.Natural)).ToArray();
        int content = available - (n * 3 + 1);
        if (n == 0 || natural.Sum() <= content) return natural;
        var equal = ColumnWidths((IReadOnlyList<int>)natural, available);
        content = Math.Max(content, n * MinColumnWidth);
        int fair = Math.Max(MinColumnWidth, content / n);
        var min = Enumerable.Range(0, n)
            .Select(c => Math.Min(natural[c], Math.Max(MinColumnWidth, Math.Min(columns[c].LongestWord, fair)))).ToArray();
        if (min.Sum() > content) return equal; // very narrow
        var amount = columns.Select(c => Math.Max(1.0, c.Amount)).ToArray();

        // Water filling: width = amount × k, kept between the minimum and the natural width; k so that they fill the space.
        double Total(double k) => Enumerable.Range(0, n).Sum(c => Math.Clamp(amount[c] * k, min[c], natural[c]));
        double lo = 0, hi = natural.Max() / amount.Min() + 1;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            if (Total(mid) > content) hi = mid; else lo = mid;
        }
        var exact = Enumerable.Range(0, n).Select(c => Math.Clamp(amount[c] * lo, min[c], natural[c])).ToArray();
        var widths = exact.Select(w => (int)Math.Floor(w)).ToArray();
        // The cells left by rounding down: to the columns that lost the most, while they are below their natural width.
        int left = content - widths.Sum();
        foreach (int c in Enumerable.Range(0, n).OrderByDescending(c => exact[c] - widths[c]))
        {
            if (left <= 0) break;
            if (widths[c] < natural[c]) { widths[c]++; left--; }
        }

        Improve(columns, widths, min, natural);
        return Height(columns, equal) < Height(columns, widths) ? equal : widths;
    }

    /// <summary>How many lines a cell's line takes, wrapped at <paramref name="width"/> as <see cref="WrapRanges"/> does.</summary>
    private static int WrappedLines(int[] words, int width)
    {
        int lines = 1, column = 0;
        bool empty = true;
        foreach (int w in words)
        {
            if (!empty && column + 1 + w <= width) { column += 1 + w; continue; }
            if (!empty) lines++;
            int rest = w;
            while (rest > width) { lines++; rest -= width; } // a long word is cut
            column = rest;
            empty = false;
        }
        return lines;
    }

    /// <summary>The height of the table in drawn lines: the tallest cell of each row.</summary>
    private static long Height(IReadOnlyList<ColumnText> columns, int[] widths)
    {
        long total = 0;
        int rows = columns.Count == 0 ? 0 : columns[0].Cells.Count;
        for (int r = 0; r < rows; r++)
        {
            int tallest = 1;
            for (int c = 0; c < widths.Length; c++)
            {
                int lines = 0;
                foreach (var words in columns[c].Cells[r]) lines += WrappedLines(words, Math.Max(1, widths[c]));
                tallest = Math.Max(tallest, lines);
            }
            total += tallest;
        }
        return total;
    }

    /// <summary>Moves single columns of width from one column to another while the table gets shorter.</summary>
    private static void Improve(IReadOnlyList<ColumnText> columns, int[] widths, int[] min, int[] natural)
    {
        int n = widths.Length;
        long words = columns.Sum(c => c.Cells.Sum(cell => cell.Sum(l => l.Length + 1)));
        if (n < 2 || words == 0) return;
        // Bounded work (about a million words wrapped), so typing in a large table stays quick.
        int passes = (int)Math.Clamp(1_000_000 / Math.Max(1, words * n * n), 0, 40);
        long best = Height(columns, widths);
        for (int pass = 0; pass < passes; pass++)
        {
            bool improved = false;
            for (int from = 0; from < n; from++)
                for (int to = 0; to < n; to++)
                {
                    if (from == to || widths[from] <= min[from] || widths[to] >= natural[to]) continue;
                    widths[from]--; widths[to]++;
                    long h = Height(columns, widths);
                    if (h < best) { best = h; improved = true; }
                    else { widths[from]++; widths[to]--; }
                }
            if (!improved) break;
        }
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
