using Avalonia.Input;
using Avalonia.Input.Platform;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Core.Html;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

/// <summary>
/// Copy, cut and paste of table cells and rows. Copy puts Markdown rows (plain text) and an HTML table (Word, Excel)
/// on the clipboard. Paste overwrites from the top-left selected cell or the current cell, growing the table when
/// needed. Outside a table, pasted rows or HTML can form a new Markdown table.
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
            var cells = Enumerable.Range(block.FirstCell, block.LastCell - block.FirstCell + 1).Select(i =>
            {
                if (i >= row.Cells.Count) return (Start: 0, End: 0);
                var cell = row.Cells[i];
                int start = cell.ContentStart(text);
                return (Start: start, End: Math.Max(start, cell.ContentEnd(text)));
            }).ToList();
            // Give copied fragments outer pipes even when the original table left them implicit.
            bool boundedRow = wholeRows && row.HasLeadingPipe && row.Cells.LastOrDefault()?.HasClosingPipe == true;
            markdown.Add(boundedRow ? text : TableClipboard.Row(cells.Select(c => text[c.Start..c.End]).ToList(), cells.Count));
            if (header) markdown.Add(TableClipboard.Separator(cells.Count));
            html.Add(cells.Select(c => MarkdownHtml.Inline(text, render, c.Start, c.End, header ? MdStyle.Bold : MdStyle.None)).ToList());
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
        ApplyWithCellSelectionUndo(() =>
        {
            _markdown.GridSelection.Clear();
            using var update = doc.RunUpdate();
            foreach (int n in body)
            {
                var line = doc.GetLineByNumber(n);
                int start = line.Offset, end = line.EndOffset + line.DelimiterLength;
                if (line.DelimiterLength == 0 && line.PreviousLine is { } previous) start = previous.EndOffset;
                doc.Remove(start, end - start);
            }
        });
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
        if (CellEditor is { } cell)
        {
            string? cellText = Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
            if (!string.IsNullOrEmpty(cellText) && ReferenceEquals(CellEditor, cell))
            {
                cell.TextArea.Selection.ReplaceSelectionWithText(cellText.Replace("\r\n", "\n").Replace('\r', '\n'));
                cell.TextArea.Caret.BringCaretToView();
            }
            return;
        }
        if (Editor.IsReadOnly) return;
        if (_markdown is null) { await PasteTextAsync(); return; }
        var (html, text) = await ReadClipboardAsync();
        await PasteContentAsync(html, text);
    }

    /// <summary>Pastes what the clipboard holds (its HTML and its text) into a Markdown document.</summary>
    internal async Task PasteContentAsync(string? html, string? text)
    {
        if (_markdown is null || Editor.IsReadOnly) return;
        if (MarkdownCommands.TableAtCaret(Editor, _markdown) is var (table, row, column)
            && CellsToPaste(html, text) is { Count: > 0 } cells)
        {
            var selection = SelectedCellBlock();
            int target = selection is { } block ? block.Lines.Min() - 1 : table.Rows[row].LineIndex;
            if (target == table.Separator.LineIndex) target++;
            ApplyWithCellSelectionUndo(() => PasteCells(table, target, selection?.FirstCell ?? column, cells));
            return;
        }
        // Outside a table: text from Word (Outlook, LibreOffice, Google Docs) keeps its lists, tables, headings and
        // styles as Markdown; a table from a web page becomes a Markdown table (other text pastes as usual).
        if (html is not null && HtmlToMarkdown.IsFromWordProcessor(html) && HtmlToMarkdown.Convert(html) is { Length: > 0 } markdown)
        {
            await PasteTextAsync(OnLinesOfItsOwn(markdown));
            return;
        }
        // Prefer the Markdown source of copied grid cells so that formatting and explicit breaks round-trip.
        if (text is not null && TableClipboard.MarkdownRows(text, requireClosingPipe: true) is { Count: > 0 } sourceRows)
        {
            var model = MarkdownDocument.Parse(text);
            if (model.Tables.Count == 1 && model.Tables[0].FirstLine == 0
                && model.Tables[0].Rows.Count == sourceRows.Count + 1)
                InsertTableBlock(text.TrimEnd('\r', '\n'));
            else
                InsertNewTable(sourceRows.Select(RowContents).ToList(), header: false);
            return;
        }
        if (html is not null && TableClipboard.FromHtml(html) is { Count: > 0 } rows)
        {
            InsertNewTable(rows, TableClipboard.HasHeader(html));
            return;
        }
        await PasteTextAsync();
    }

    /// <summary>
    /// Blocks pasted from Word (several lines: paragraphs, a list, a table) go on lines of their own: text before the
    /// caret on its line is ended with a line break, and text after it moves to the next line. (A table pasted at the
    /// start of a line joined that line into its last row.) A single line of text goes into the line as typed.
    /// </summary>
    private string OnLinesOfItsOwn(string markdown)
    {
        if (!markdown.Contains('\n')) return markdown;
        var doc = Editor.Document;
        int start = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        int end = Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : start;
        var first = doc.GetLineByOffset(start);
        var last = doc.GetLineByOffset(end);
        bool textBefore = doc.GetText(first.Offset, start - first.Offset).Trim().Length > 0;
        bool textAfter = doc.GetText(end, last.EndOffset - end).Trim().Length > 0;
        return (textBefore ? "\n\n" : "") + markdown + (textAfter ? "\n\n" : "");
    }

    /// <summary>AvaloniaEdit's clipboard format for a rectangular (column) selection.</summary>
    private const string RectangularFormat = "AvalonEditRectangularSelection";

    /// <summary>
    /// Pastes the clipboard text as it is, tabs included: AvaloniaEdit's own paste turns every tab into spaces when
    /// "Insert spaces" is on, which changes the pasted text (TSV, Makefiles, aligned columns). Only the line endings
    /// follow the document's, so a paste does not mix them. A rectangular copy still goes through AvaloniaEdit, which
    /// pastes it as a block.
    /// </summary>
    /// <param name="converted">Text to paste instead of the clipboard's (the Markdown made from Word's HTML).</param>
    private async Task PasteTextAsync(string? converted = null)
    {
        string? text = converted;
        if (text is null)
        {
            if (Clipboard is not { } clipboard) { Editor.Paste(); return; }
            try
            {
                if ((await clipboard.GetFormatsAsync()).Contains(RectangularFormat)) { Editor.Paste(); return; }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { } // a clipboard another program filled badly
            text = await clipboard.TryGetTextAsync();
        }
        if (string.IsNullOrEmpty(text) || Editor.IsReadOnly) return;
        var area = Editor.TextArea;
        string newLine = AvaloniaEdit.Document.TextUtilities.GetNewLineFromDocument(Editor.Document, area.Caret.Line);
        text = AvaloniaEdit.Document.TextUtilities.NormalizeNewLines(text, newLine);
        area.Selection.ReplaceSelectionWithText(text);
        area.Caret.BringCaretToView();
    }

    private static List<List<string>>? CellsToPaste(string? html, string? text)
    {
        if (text is not null && TableClipboard.MarkdownRows(text) is { Count: > 0 } markdown)
            return markdown.Select(RowContents).Where(row => row.Count > 0).ToList();
        var rows = html is not null ? TableClipboard.FromHtml(html) : null;
        rows ??= text is not null ? TableClipboard.FromTsv(text) : null;
        return rows?.Select(row => row.Select(TableClipboard.Escape).ToList()).ToList();
    }

    private static List<string> RowContents(string line) =>
        MarkdownDocument.SplitRow(line, 0, MdLineKind.TableBody).Cells.Select(cell =>
        {
            int start = cell.ContentStart(line);
            return line[start..Math.Max(start, cell.ContentEnd(line))];
        }).ToList();

    private void PasteCells(MarkdownTable table, int target, int column, List<List<string>> cells)
    {
        var view = _markdown!;
        var doc = Editor.Document;
        view.GridSelection.Clear();
        if (_tableSelection is { Ranges.Count: > 0 } selection)
        {
            selection.Ranges = [];
            Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        }
        Editor.TextArea.ClearSelection();
        MarkdownCommands.Apply(doc, TableEditor.PasteCells(table, view.Source, target, column, cells));
        var line = doc.GetLineByNumber(target + 1);
        if (view.Model.TableAtLine(target)?.RowAtLine(target) is { } row && column < row.Cells.Count)
            Editor.CaretOffset = line.Offset + row.Cells[column].ContentStart(doc.GetText(line));
        Editor.TextArea.Caret.BringCaretToView();
        SetStatusMessage(Loc.T("Table.Pasted", cells.Count));
    }

    /// <summary>
    /// A new Markdown table at the caret (on lines of its own) from pasted rows. The first row becomes the header only
    /// when the source marked it as one; otherwise the header is empty and every pasted row is a body row (a Markdown
    /// table must have a header).
    /// </summary>
    private void InsertNewTable(List<List<string>> rows, bool header)
    {
        int columns = rows.Max(r => r.Count);
        var table = new List<string> { TableClipboard.Row(header ? rows[0] : [], columns), TableClipboard.Separator(columns) };
        table.AddRange(rows.Skip(header ? 1 : 0).Select(r => TableClipboard.Row(r, columns)));
        InsertTableBlock(string.Join('\n', table));
        SetStatusMessage(Loc.T("Table.PastedTable", rows.Count, columns));
    }

    private void InsertTableBlock(string table)
    {
        var doc = Editor.Document;
        var area = Editor.TextArea;
        int start = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        int end = start + Editor.SelectionLength;
        var first = doc.GetLineByOffset(start);
        var last = doc.GetLineByOffset(end);
        string nl = NewLineAt(first.LineNumber);
        bool before = !string.IsNullOrWhiteSpace(doc.GetText(first.Offset, start - first.Offset));
        bool after = !string.IsNullOrWhiteSpace(doc.GetText(end, last.EndOffset - end));
        string prefix = before ? nl + nl
            : start > first.Offset || first.PreviousLine is { } previous && !string.IsNullOrWhiteSpace(doc.GetText(previous)) ? nl : "";
        string suffix = after ? nl + nl
            : last.NextLine is { } next && !string.IsNullOrWhiteSpace(doc.GetText(next)) ? nl
            : last.DelimiterLength == 0 ? nl : "";
        // A newly inserted table needs a blank boundary on both sides of neighboring text.
        area.Selection.ReplaceSelectionWithText(prefix
            + AvaloniaEdit.Document.TextUtilities.NormalizeNewLines(table, nl) + suffix);
        area.Caret.BringCaretToView();
    }
}
