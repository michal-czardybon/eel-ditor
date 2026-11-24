using System.Text;
using System.Text.RegularExpressions;

namespace EelDitor.Markdown;

/// <summary>
/// Writing a cell's Markdown, as edited in the cell editor, back into its table row without ever breaking the
/// table: bare | are escaped; backticks that would open a code span swallowing the following cells are escaped;
/// a trailing backslash that would escape the closing pipe is doubled. The new row must have the same cells with
/// the other cells unchanged, otherwise nothing is written and the reason is returned.
/// </summary>
public static partial class TableCellEdit
{
    public enum Problem { None, CellCount, OtherCells }

    /// <summary>A line break of the cell editor's text where styles were closed before it and opened again after it.</summary>
    public readonly record struct SplitBreak(int Newline, string Close, string Open);

    private const MdStyle SplittableAtBreak = MdStyle.Bold | MdStyle.Italic | MdStyle.Strike | MdStyle.Highlight | MdStyle.Subscript | MdStyle.Superscript;

    /// <summary>
    /// The cell's text for the cell editor: each <c>&lt;br&gt;</c> becomes a line break. In the row a style may run
    /// over a <c>&lt;br&gt;</c> (<c>==one&lt;br&gt;two==</c>), but in the editor a style cannot run over a line break and
    /// its markers would show; such styles are closed before the break and opened again after it
    /// (<c>==one==\n==two==</c>), and <see cref="JoinSplitStyles"/> undoes that before the text is written back.
    /// </summary>
    public static (string Text, List<SplitBreak> Splits) CellEditorText(string line, TableCell cell, LineRender render)
    {
        int cs = cell.ContentStart(line), ce = cell.ContentEnd(line);
        var splits = new List<SplitBreak>();
        if (ce <= cs) return ("", splits);
        var sb = new StringBuilder();
        int at = cs;
        foreach (var br in render.Breaks.Where(b => b.Start >= cs && b.End <= ce).OrderBy(b => b.Start))
        {
            sb.Append(line, at, br.Start - at);
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            // The styles running over the break, outermost first, with their markers as written.
            var open = render.Styles
                .Where(s => s.Start <= br.Start && s.End >= br.End && (s.Style & SplittableAtBreak) != 0 && (s.Style & (MdStyle.Code | MdStyle.Link)) == 0)
                .Select(s => (Span: s,
                    Opening: render.Hidden.FirstOrDefault(h => h.End == s.Start && h.Length > 0),
                    Closing: render.Hidden.FirstOrDefault(h => h.Start == s.End && h.Length > 0)))
                .Where(x => x.Opening.Length > 0 && x.Closing.Length > 0)
                .OrderBy(x => x.Span.Start).ThenByDescending(x => x.Span.End)
                .ToList();
            string close = string.Concat(Enumerable.Reverse(open).Select(x => line.Substring(x.Closing.Start, x.Closing.Length)));
            string reopen = string.Concat(open.Select(x => line.Substring(x.Opening.Start, x.Opening.Length)));
            sb.Append(close);
            int newline = sb.Length;
            sb.Append('\n').Append(reopen);
            if (open.Count > 0) splits.Add(new SplitBreak(newline, close, reopen));
            at = br.End;
            while (at < ce && line[at] == ' ') at++;
        }
        sb.Append(line, at, ce - at);
        return (sb.ToString(), splits);
    }

    /// <summary>
    /// Joins the styles <see cref="CellEditorText"/> split at line breaks again (<c>==one==\n==two==</c> →
    /// <c>==one\ntwo==</c>), where the markers around the break (at <paramref name="newlines"/>' offsets now) are
    /// still exactly as they were made.
    /// </summary>
    public static string JoinSplitStyles(string text, IEnumerable<SplitBreak> newlines)
    {
        foreach (var s in newlines.OrderByDescending(s => s.Newline))
        {
            int n = s.Newline;
            if (n < s.Close.Length || n >= text.Length || text[n] != '\n') continue;
            if (string.CompareOrdinal(text, n - s.Close.Length, s.Close, 0, s.Close.Length) != 0) continue;
            if (n + 1 + s.Open.Length > text.Length || string.CompareOrdinal(text, n + 1, s.Open, 0, s.Open.Length) != 0) continue;
            text = text[..(n - s.Close.Length)] + "\n" + text[(n + 1 + s.Open.Length)..];
        }
        return text;
    }

    public sealed record Result(string? NewLine, Problem Problem)
    {
        public bool Ok => Problem == Problem.None;
    }

    /// <summary>
    /// Writes <paramref name="newSource"/> (the cell's Markdown, as edited in the cell editor) into the cell.
    /// Line breaks become <c>&lt;br&gt;</c> and bare | are escaped; when backticks would open a code span that swallows the
    /// following pipes, the unmatched ones are escaped. The result must keep the same cells with the other cells
    /// unchanged, otherwise nothing is written.
    /// </summary>
    public static Result ApplySource(string line, int cellIndex, string newSource, string headerLine, string separatorLine, bool isHeader)
    {
        var (row, _) = ParseRow(line, headerLine, separatorLine, isHeader);
        if (row is null || cellIndex >= row.Cells.Count) return new Result(null, Problem.CellCount);
        var cell = row.Cells[cellIndex];
        // A table row is one line: a line break in the cell is written as <br>.
        string source = EscapeBarePipes(string.Join("<br>", newSource.Replace("\r\n", "\n").Replace('\r', '\n').Trim()
            .Split('\n').Select(part => part.Trim())));
        var problem = Problem.CellCount;
        string safe = EscapeUnmatchedBackticks(source);
        foreach (string body in new[] { source, safe, DoubleTrailingBackslash(safe) })
        {
            int contentStart = cell.ContentStart(line), contentEnd = cell.ContentEnd(line);
            string candidate = contentEnd > contentStart
                ? line[..contentStart] + body + line[contentEnd..]
                : line[..cell.Start] + (body.Length > 0 ? " " + body + " " : " ") + line[cell.End..];
            if (candidate == line) return new Result(line, Problem.None);
            problem = CheckStructure(candidate, row, cellIndex, line, headerLine, separatorLine, isHeader);
            if (problem == Problem.None) return new Result(candidate, Problem.None);
        }
        return new Result(null, problem);
    }

    /// <summary>Escapes every | that is not already escaped (preceded by an odd number of backslashes).</summary>
    public static string EscapeBarePipes(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        int backslashes = 0;
        foreach (char c in text)
        {
            if (c == '|' && backslashes % 2 == 0) sb.Append('\\');
            sb.Append(c);
            backslashes = c == '\\' ? backslashes + 1 : 0;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Backtick runs that do not open a closed code span are escaped. The scan reads like Markdown does: outside
    /// code a backslash escapes the next character; a run opens a code span closed by the next run of the same
    /// length, and inside it backslashes mean nothing.
    /// </summary>
    public static string EscapeUnmatchedBackticks(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == BS && i + 1 < text.Length) { sb.Append(c).Append(text[i + 1]); i += 2; continue; }
            if (c != '`') { sb.Append(c); i++; continue; }
            int n = 1;
            while (i + n < text.Length && text[i + n] == '`') n++;
            int close = FindRun(text, i + n, n);
            if (close >= 0)
            {
                sb.Append(text, i, close + n - i); // the whole code span, as it is
                i = close + n;
                continue;
            }
            for (int k = 0; k < n; k++) sb.Append(BS).Append('`');
            i += n;
        }
        return sb.ToString();
    }

    private const char BS = (char)92; // backslash

    private static int FindRun(string text, int from, int length)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] != '`') { i++; continue; }
            int n = 1;
            while (i + n < text.Length && text[i + n] == '`') n++;
            if (n == length) return i;
            i += n;
        }
        return -1;
    }

    /// <summary>"a\\" at the end of a cell would escape the following pipe: the backslash is doubled.</summary>
    private static string DoubleTrailingBackslash(string text)
    {
        int n = 0;
        while (n < text.Length && text[^(n + 1)] == '\\') n++;
        return n % 2 == 1 ? text + "\\" : text;
    }

    private static Problem CheckStructure(string candidate, TableRow oldRow, int cellIndex, string oldLine,
        string headerLine, string separatorLine, bool isHeader)
    {
        var (row, _) = ParseRow(candidate, headerLine, separatorLine, isHeader);
        if (row is null || row.Cells.Count != oldRow.Cells.Count) return Problem.CellCount;
        for (int c = 0; c < row.Cells.Count; c++)
        {
            if (c == cellIndex) continue;
            if (oldLine[oldRow.Cells[c].Start..oldRow.Cells[c].End] != candidate[row.Cells[c].Start..row.Cells[c].End]) return Problem.OtherCells;
        }
        return Problem.None;
    }

    private static (TableRow? Row, LineRender Render) ParseRow(string line, string headerLine, string separatorLine, bool isHeader)
    {
        string text = isHeader ? line + "\n" + separatorLine : headerLine + "\n" + separatorLine + "\n" + line;
        var doc = MarkdownDocument.Parse(text);
        if (doc.Tables.Count == 0) return (null, LineRender.Plain);
        var table = doc.Tables[0];
        int index = isHeader ? 0 : 2;
        if (table.Rows.Count <= index) return (null, LineRender.Plain);
        return (table.Rows[index], doc.GetLine(index));
    }
}
