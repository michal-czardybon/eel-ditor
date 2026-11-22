using Avalonia.Input;
using Avalonia.Input.Platform;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

/// <summary>
/// Copy, cut and paste of table cells and rows. Copy puts Markdown rows (plain text) and an HTML table (Word, Excel)
/// on the clipboard; paste inserts rows above the current row (from Markdown rows, an HTML table from Word or a web
/// page, or tab-separated text from Excel), and outside a table turns a pasted HTML table into a Markdown table.
/// </summary>
public partial class MainWindow
{
    /// <summary>The clipboard format of HTML on this system (Windows: CF_HTML, as bytes with a header).</summary>
    private static DataFormat<byte[]>? WindowsHtmlFormat => OperatingSystem.IsWindows() ? DataFormat.CreateBytesPlatformFormat("HTML Format") : null;

    private static DataFormat<string> OtherHtmlFormat => DataFormat.CreateStringPlatformFormat(OperatingSystem.IsMacOS() ? "public.html" : "text/html");

    /// <summary>Selected table cells (grid selection, or Select column / row in a table shown as text).</summary>
    private (List<int> Lines, int FirstCell, int LastCell)? SelectedCellBlock()
    {
        if (_markdown is null) return null;
        if (_markdown.TablesAsGrid) return _markdown.GridSelection.CellBlock;
        if (_tableSelection is not { Ranges.Count: > 0 } sel || _markdown.Model.TableAtLine(sel.TableLine) is not { } table) return null;
        if (sel.Column >= 0)
            return (table.Rows.Where(r => r.Kind != MdLineKind.TableSeparator).Select(r => r.LineIndex + 1).ToList(), sel.Column, sel.Column);
        return ([sel.RowLine + 1], 0, Math.Max(0, table.ColumnCount - 1));
    }

    private async Task CopyCellsAsync((List<int> Lines, int FirstCell, int LastCell) block)
    {
        if (_markdown is null || Clipboard is not { } clipboard || block.Lines.Count == 0) return;
        if (_markdown.Model.TableAtLine(block.Lines[0] - 1) is not { } table) return;
        var doc = Editor.Document;
        bool wholeRows = block.FirstCell == 0 && block.LastCell >= table.ColumnCount - 1;
        var markdown = new List<string>();
        var html = new List<IReadOnlyList<string>>();
        bool withHeader = false;
        foreach (int n in block.Lines)
        {
            if (table.RowAtLine(n - 1) is not { } row || row.Kind == MdLineKind.TableSeparator) continue;
            string text = doc.GetText(doc.GetLineByNumber(n));
            var render = _markdown.Model.GetLine(n - 1);
            bool header = row.Kind == MdLineKind.TableHeader;
            withHeader |= header;
            var cells = Enumerable.Range(block.FirstCell, block.LastCell - block.FirstCell + 1)
                .Select(i => i < row.Cells.Count ? row.Cells[i] : null).ToList();
            // Whole rows keep their source exactly; part of a row becomes a row of those cells.
            markdown.Add(wholeRows ? text : TableClipboard.Row(cells.Select(c => c is null ? "" : text[c.ContentStart(text)..c.ContentEnd(text)]).ToList(), cells.Count));
            if (header) markdown.Add(TableClipboard.Separator(cells.Count));
            html.Add(cells.Select(c => c is null ? "" : MarkdownHtml.Inline(text, render, c.ContentStart(text), c.ContentEnd(text), header ? MdStyle.Bold : MdStyle.None)).ToList());
        }
        string nl = NewLineAt(block.Lines[0]);
        string table1 = TableClipboard.HtmlTable(html, withHeader);
        var item = new DataTransferItem();
        item.SetText(string.Join(nl, markdown) + nl);
        if (WindowsHtmlFormat is { } windows) item.Set(windows, TableClipboard.WindowsHtml(table1));
        else item.Set(OtherHtmlFormat, table1);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
        SetStatusMessage(Loc.T("Table.Copied", markdown.Count(l => !l.TrimStart().StartsWith("|-", StringComparison.Ordinal) && !l.Contains("---|", StringComparison.Ordinal))));
    }

    /// <summary>Cut of whole body rows: copied, then removed. Other selections of cells are only copied.</summary>
    private async Task CutRowsAsync((List<int> Lines, int FirstCell, int LastCell) block)
    {
        await CopyCellsAsync(block);
        if (_markdown?.Model.TableAtLine(block.Lines[0] - 1) is not { } table) return;
        bool wholeRows = block.FirstCell == 0 && block.LastCell >= table.ColumnCount - 1;
        var body = block.Lines.Where(n => table.RowAtLine(n - 1)?.Kind == MdLineKind.TableBody).OrderByDescending(n => n).ToList();
        if (!wholeRows || body.Count == 0) return;
        var doc = Editor.Document;
        _markdown.GridSelection.Clear();
        using (doc.RunUpdate())
            foreach (int n in body)
            {
                var line = doc.GetLineByNumber(n);
                int start = line.Offset, end = line.EndOffset + line.DelimiterLength;
                if (line.DelimiterLength == 0 && line.PreviousLine is { } previous) start = previous.EndOffset;
                doc.Remove(start, end - start);
            }
    }

    private async Task<(string? Html, string? Text)> ReadClipboardAsync()
    {
        if (Clipboard is not { } clipboard) return (null, null);
        string? html = null;
        try
        {
            if (WindowsHtmlFormat is { } windows)
            {
                if (await clipboard.TryGetValueAsync(windows) is { Length: > 0 } bytes) html = TableClipboard.HtmlFromClipboard(bytes);
            }
            else html = await clipboard.TryGetValueAsync(OtherHtmlFormat);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { html = null; } // a format another program wrote badly
        return (html, await clipboard.TryGetTextAsync());
    }

    private async Task PasteAsync()
    {
        if (_markdown is null || Editor.IsReadOnly) { Editor.Paste(); return; }
        var (html, text) = await ReadClipboardAsync();
        var lines = MarkdownCommands.TableAtCaret(Editor, _markdown) is var (table, _, _) ? RowsToPaste(html, text, table.ColumnCount) : null;
        if (lines is { Count: > 0 } && MarkdownCommands.TableAtCaret(Editor, _markdown) is var (t, row, _))
        {
            PasteRows(t, row, lines);
            return;
        }
        // Outside a table: a table from Word or a web page becomes a Markdown table (plain text pastes as usual).
        if (html is not null && TableClipboard.FromHtml(html) is { Count: > 0 } rows && TableClipboard.MarkdownRows(text ?? "") is null)
        {
            InsertNewTable(rows, TableClipboard.HasHeader(html));
            return;
        }
        Editor.Paste();
    }

    /// <summary>The Markdown rows to paste into a table of <paramref name="columns"/> columns, or null when the clipboard has none.</summary>
    private static List<string>? RowsToPaste(string? html, string? text, int columns)
    {
        if (text is not null && TableClipboard.MarkdownRows(text) is { Count: > 0 } markdown)
            return markdown.Select(l =>
            {
                var r = MarkdownDocument.SplitRow(l, 0, MdLineKind.TableBody);
                // The same number of columns: the row as written; otherwise its cells fitted to this table.
                return r.Cells.Count == columns ? l.Trim() : TableClipboard.Row(r.Cells.Select(c => l[c.ContentStart(l)..c.ContentEnd(l)]).ToList(), columns);
            }).ToList();
        if (html is not null && TableClipboard.FromHtml(html) is { Count: > 0 } fromHtml)
            return fromHtml.Select(r => TableClipboard.Row(r, columns)).ToList();
        if (text is not null && TableClipboard.FromTsv(text) is { Count: > 0 } tsv)
            return tsv.Select(r => TableClipboard.Row(r, columns)).ToList();
        return null;
    }

    /// <summary>Inserts rows above the current row (below the separator when the current row is the header).</summary>
    private void PasteRows(MarkdownTable table, int rowIndex, List<string> lines)
    {
        var doc = Editor.Document;
        // The current row: in a grid the one with a selection or the current cell; else the caret's.
        int target = _markdown?.TablesAsGrid == true && _markdown.GridSelection.CellBlock is { } block ? block.Lines.Min() - 1 : table.FirstLine + rowIndex;
        if (target <= table.FirstLine + 1) target = table.FirstLine + 2;
        string nl = NewLineAt(table.FirstLine + 1);
        _markdown?.GridSelection.Clear();
        using (doc.RunUpdate())
        {
            if (target <= table.LastLine)
                doc.Insert(doc.GetLineByNumber(target + 1).Offset, string.Join(nl, lines) + nl);
            else
                doc.Insert(doc.GetLineByNumber(table.LastLine + 1).EndOffset, nl + string.Join(nl, lines));
        }
        var first = doc.GetLineByNumber(Math.Min(target + 1, doc.LineCount));
        Editor.CaretOffset = first.Offset + Math.Min(2, first.Length);
        SetStatusMessage(Loc.T("Table.Pasted", lines.Count));
    }

    /// <summary>
    /// A new Markdown table at the caret (on lines of its own) from pasted rows. The first row becomes the header only
    /// when the source marked it as one; otherwise the header is empty and every pasted row is a body row (a Markdown
    /// table must have a header).
    /// </summary>
    private void InsertNewTable(List<List<string>> rows, bool header)
    {
        var doc = Editor.Document;
        int columns = rows.Max(r => r.Count);
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        string nl = NewLineAt(line.LineNumber);
        var table = new List<string> { TableClipboard.Row(header ? rows[0] : [], columns), TableClipboard.Separator(columns) };
        table.AddRange(rows.Skip(header ? 1 : 0).Select(r => TableClipboard.Row(r, columns)));
        int caret = Editor.CaretOffset;
        bool atLineStart = caret == line.Offset;
        bool blankBefore = line.LineNumber == 1 || (atLineStart && doc.GetText(line.PreviousLine!).Trim().Length == 0);
        string text = (atLineStart ? "" : nl) + (blankBefore ? "" : nl) + string.Join(nl, table) + nl
            + (caret < line.EndOffset || line.NextLine is { } next && doc.GetText(next).Trim().Length > 0 ? nl : "");
        doc.Insert(caret, text);
        SetStatusMessage(Loc.T("Table.PastedTable", rows.Count, columns));
    }
}
