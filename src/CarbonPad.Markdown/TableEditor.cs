using System.Text;

namespace CarbonPad.Markdown;

public enum TableEditKind
{
    /// <summary>Replace <c>Length</c> characters at <c>Line</c>/<c>Column</c> with <c>Text</c>.</summary>
    Replace,

    /// <summary>Insert a new line with <c>Text</c> after <c>Line</c>.</summary>
    InsertLineAfter,

    /// <summary>Insert a new line with <c>Text</c> before <c>Line</c>.</summary>
    InsertLineBefore,

    /// <summary>Delete <c>Line</c> including its terminator.</summary>
    DeleteLine,
}

public sealed record TableEdit(TableEditKind Kind, int Line, int Column = 0, int Length = 0, string Text = "")
{
    public static TableEdit Replace(int line, int column, int length, string text) => new(TableEditKind.Replace, line, column, length, text);
    public static TableEdit Insert(int line, int column, string text) => new(TableEditKind.Replace, line, column, 0, text);
}

/// <summary>A cell range inside a source line, used for column/row selection.</summary>
public readonly record struct CellRange(int Line, int Start, int End);

/// <summary>
/// Structural table edits that touch as few characters as possible: existing cells are never re-padded,
/// so a git diff shows only the logical change.
/// </summary>
public static class TableEditor
{
    private const string NewBodyCell = "   |";
    private const string NewSeparatorCell = " --- |";

    /// <summary>Index of the cell that contains <paramref name="column"/> (a cell owns its closing pipe).</summary>
    public static int CellIndexAt(TableRow row, int column)
    {
        if (row.Cells.Count == 0) return 0;
        if (row.HasLeadingPipe && column <= row.LeadingPipe) return 0;
        for (int i = 0; i < row.Cells.Count; i++)
        {
            var c = row.Cells[i];
            int end = c.HasClosingPipe ? c.ClosingPipe : c.End;
            if (column <= end) return i;
        }
        return row.Cells.Count - 1;
    }

    public static IReadOnlyList<TableEdit> InsertColumn(MarkdownTable table, ILineSource src, int column, bool right)
    {
        var edits = new List<TableEdit>();
        foreach (var row in table.Rows)
        {
            string line = src.GetLine(row.LineIndex);
            bool sep = row.Kind == MdLineKind.TableSeparator;
            string cellText = sep ? NewSeparatorCell : NewBodyCell;

            if (column < row.Cells.Count)
            {
                var cell = row.Cells[column];
                if (!right)
                {
                    if (cell.Start == 0 && !row.HasLeadingPipe)
                        edits.Add(TableEdit.Insert(row.LineIndex, 0, (sep ? "---" : "  ") + " |"));
                    else
                        edits.Add(TableEdit.Insert(row.LineIndex, cell.Start, cellText));
                }
                else if (cell.HasClosingPipe)
                {
                    edits.Add(TableEdit.Insert(row.LineIndex, cell.ClosingPipe + 1, cellText));
                }
                else
                {
                    edits.Add(TableEdit.Insert(row.LineIndex, line.Length, sep ? " | ---" : " |  "));
                }
            }
            else
            {
                // Short row: append a cell at the end.
                bool trailingPipe = line.TrimEnd().EndsWith('|');
                edits.Add(TableEdit.Insert(row.LineIndex, line.TrimEnd().Length, trailingPipe ? cellText : " |" + cellText));
            }
        }
        return edits;
    }

    public static IReadOnlyList<TableEdit> DeleteColumn(MarkdownTable table, int column)
    {
        if (table.ColumnCount <= 1) return [];
        var edits = new List<TableEdit>();
        foreach (var row in table.Rows)
        {
            if (column >= row.Cells.Count) continue;
            var cell = row.Cells[column];
            if (cell.HasClosingPipe)
            {
                int start = cell.Start;
                // First cell of a row without leading pipe: also no pipe before it, just remove "cell|".
                edits.Add(TableEdit.Replace(row.LineIndex, start, cell.ClosingPipe + 1 - start, ""));
            }
            else
            {
                // Last cell without trailing pipe: remove the pipe before it as well.
                int start = Math.Max(0, cell.Start - 1);
                edits.Add(TableEdit.Replace(row.LineIndex, start, cell.End - start, ""));
            }
        }
        return edits;
    }

    /// <summary>Builds an empty row matching the header's pipe style.</summary>
    public static string NewRow(MarkdownTable table, ILineSource src)
    {
        var header = table.Header;
        string headerText = src.GetLine(header.LineIndex);
        var sb = new StringBuilder();
        if (header.HasLeadingPipe) sb.Append(headerText, 0, header.LeadingPipe + 1);
        foreach (var cell in header.Cells) sb.Append(cell.HasClosingPipe ? NewBodyCell : "   ");
        return sb.ToString();
    }

    public static IReadOnlyList<TableEdit> InsertRow(MarkdownTable table, ILineSource src, int rowIndex, bool below)
    {
        string row = NewRow(table, src);
        // Rows cannot go above the header or between header and separator.
        if (rowIndex <= 1) return [new TableEdit(TableEditKind.InsertLineAfter, table.Separator.LineIndex, Text: row)];
        int line = table.Rows[rowIndex].LineIndex;
        return [new TableEdit(below ? TableEditKind.InsertLineAfter : TableEditKind.InsertLineBefore, line, Text: row)];
    }

    public static IReadOnlyList<TableEdit> DeleteRow(MarkdownTable table, int rowIndex)
    {
        if (rowIndex < 2 || rowIndex >= table.Rows.Count) return [];
        return [new TableEdit(TableEditKind.DeleteLine, table.Rows[rowIndex].LineIndex)];
    }

    /// <summary>Swaps a body row with its neighbour. Returns no edits if the move is not possible.</summary>
    public static IReadOnlyList<TableEdit> MoveRow(MarkdownTable table, ILineSource src, int rowIndex, bool down)
    {
        int other = down ? rowIndex + 1 : rowIndex - 1;
        if (rowIndex < 2 || other < 2 || other >= table.Rows.Count) return [];
        int a = table.Rows[rowIndex].LineIndex, b = table.Rows[other].LineIndex;
        string ta = src.GetLine(a), tb = src.GetLine(b);
        return
        [
            TableEdit.Replace(a, 0, ta.Length, tb),
            TableEdit.Replace(b, 0, tb.Length, ta),
        ];
    }

    /// <summary>Swaps column <paramref name="column"/> with its neighbour in every row.</summary>
    public static IReadOnlyList<TableEdit> MoveColumn(MarkdownTable table, ILineSource src, int column, bool right)
    {
        int left = right ? column : column - 1;
        if (left < 0 || left + 1 >= table.ColumnCount) return [];
        var edits = new List<TableEdit>();
        foreach (var row in table.Rows)
        {
            if (left + 1 >= row.Cells.Count) continue;
            var c1 = row.Cells[left];
            var c2 = row.Cells[left + 1];
            if (!c1.HasClosingPipe) continue;
            string line = src.GetLine(row.LineIndex);
            string s1 = line[c1.Start..c1.End], s2 = line[c2.Start..c2.End];
            edits.Add(TableEdit.Replace(row.LineIndex, c1.Start, c2.End - c1.Start, s2 + "|" + s1));
        }
        return edits;
    }

    /// <summary>
    /// Physically pads every cell so the source is aligned. This is the only operation that rewrites
    /// existing cells and is only run on explicit request.
    /// </summary>
    public static IReadOnlyList<TableEdit> FormatInSource(MarkdownTable table, ILineSource src, MarkdownDocument doc)
    {
        var edits = new List<TableEdit>();
        foreach (var row in table.Rows)
        {
            string line = src.GetLine(row.LineIndex);
            string formatted = ApplyPaddings(line, doc.GetLine(row.LineIndex));
            if (formatted != line) edits.Add(TableEdit.Replace(row.LineIndex, 0, line.Length, formatted));
        }
        return edits;
    }

    /// <summary>Inserts the virtual paddings into the source text (markup stays in place).</summary>
    public static string ApplyPaddings(string line, LineRender render)
    {
        if (render.Paddings.Count == 0) return line;
        var sb = new StringBuilder(line.Length + 32);
        int pos = 0;
        foreach (var p in render.Paddings.OrderBy(p => p.Offset))
        {
            sb.Append(line, pos, p.Offset - pos);
            sb.Append(p.Fill, p.Width);
            pos = p.Offset;
        }
        sb.Append(line, pos, line.Length - pos);
        return sb.ToString();
    }

    /// <summary>Caret target for Tab/Shift+Tab: the end of the next/previous cell's content, or null at the table edge.</summary>
    public static (int Line, int Column)? AdjacentCell(MarkdownTable table, ILineSource src, int line, int column, bool forward)
    {
        var cells = new List<(int Row, int Cell)>();
        for (int r = 0; r < table.Rows.Count; r++)
        {
            if (table.Rows[r].Kind == MdLineKind.TableSeparator) continue;
            for (int c = 0; c < table.Rows[r].Cells.Count; c++) cells.Add((r, c));
        }
        int curRow = line - table.FirstLine;
        if (curRow == 1) curRow = forward ? 0 : 2;
        int cur = cells.FindIndex(x => x.Row == curRow && x.Cell == CellIndexAt(table.Rows[curRow], column));
        int next = cur + (forward ? 1 : -1);
        if (cur < 0 || next < 0 || next >= cells.Count) return null;
        var (nr, nc) = cells[next];
        var row = table.Rows[nr];
        var cell = row.Cells[nc];
        string text = src.GetLine(row.LineIndex);
        int start = cell.ContentStart(text), end = cell.ContentEnd(text);
        return (row.LineIndex, start < end ? end : Math.Min(cell.Start + 1, cell.End));
    }

    public static IReadOnlyList<CellRange> ColumnRanges(MarkdownTable table, int column) =>
        table.Rows.Where(r => column < r.Cells.Count)
            .Select(r => new CellRange(r.LineIndex, r.Cells[column].Start, r.Cells[column].End)).ToList();

    public static IReadOnlyList<CellRange> RowRanges(MarkdownTable table, int rowIndex) =>
        table.Rows[rowIndex].Cells.Select(c => new CellRange(table.Rows[rowIndex].LineIndex, c.Start, c.End)).ToList();

    /// <summary>Tab separated text of the given column (cell contents trimmed), one row per line.</summary>
    public static string ColumnAsTsv(MarkdownTable table, ILineSource src, int column, string newLine) =>
        string.Join(newLine, table.Rows.Where(r => r.Kind != MdLineKind.TableSeparator)
            .Select(r => column < r.Cells.Count ? src.GetLine(r.LineIndex)[r.Cells[column].Start..r.Cells[column].End].Trim() : ""));

    /// <summary>Template for a new table.</summary>
    public static string NewTable(int columns, int rows, string newLine, string headerPrefix = "Column")
    {
        columns = Math.Max(1, columns);
        var sb = new StringBuilder();
        sb.Append('|');
        for (int c = 1; c <= columns; c++) sb.Append($" {headerPrefix} {c} |");
        sb.Append(newLine).Append('|');
        for (int c = 0; c < columns; c++) sb.Append(" --- |");
        for (int r = 0; r < rows; r++)
        {
            sb.Append(newLine).Append('|');
            for (int c = 0; c < columns; c++) sb.Append(NewBodyCell);
        }
        return sb.ToString();
    }
}
