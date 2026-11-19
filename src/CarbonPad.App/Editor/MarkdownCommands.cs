using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using CarbonPad.Markdown;

namespace CarbonPad.App.Editor;

/// <summary>Editing commands for Markdown that work on the source text while it is displayed styled.</summary>
public static class MarkdownCommands
{
    /// <summary>Toggles a wrapping marker (e.g. ** for bold) around the selection or the word at the caret.</summary>
    public static void ToggleStyle(TextEditor editor, MarkdownView view, string marker, MdStyle style)
    {
        var doc = editor.Document;
        var area = editor.TextArea;
        int caret = editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var render = view.RenderFor(line);
        int col = caret - line.Offset;

        // Caret (or selection) inside an existing span of that style: remove its markers.
        int selStart = editor.SelectionLength > 0 ? editor.SelectionStart - line.Offset : col;
        int selEnd = editor.SelectionLength > 0 ? editor.SelectionStart + editor.SelectionLength - line.Offset : col;
        foreach (var s in render.Styles)
        {
            if (!s.Style.HasFlag(style) || selStart < s.Start || selEnd > s.End) continue;
            var open = render.Hidden.LastOrDefault(h => h.End == s.Start && h.Length >= marker.Length);
            var close = render.Hidden.FirstOrDefault(h => h.Start == s.End && h.Length >= marker.Length);
            if (open.Length == 0 || close.Length == 0) continue;
            using (doc.RunUpdate())
            {
                // Remove the innermost marker characters (adjacent to the content).
                doc.Remove(line.Offset + close.Start, marker.Length);
                doc.Remove(line.Offset + open.End - marker.Length, marker.Length);
            }
            return;
        }

        if (editor.SelectionLength > 0 && area.Selection.IsMultiline == false)
        {
            int start = editor.SelectionStart, length = editor.SelectionLength;
            using (doc.RunUpdate())
            {
                doc.Insert(start + length, marker);
                doc.Insert(start, marker);
            }
            editor.Select(start + marker.Length, length);
            return;
        }

        var (ws, we) = WordAt(doc, caret);
        if (we > ws)
        {
            using (doc.RunUpdate())
            {
                doc.Insert(we, marker);
                doc.Insert(ws, marker);
            }
            editor.CaretOffset = caret + marker.Length;
        }
        else
        {
            doc.Insert(caret, marker + marker);
            editor.CaretOffset = caret + marker.Length;
        }
    }

    private static (int Start, int End) WordAt(TextDocument doc, int offset)
    {
        var line = doc.GetLineByOffset(offset);
        string text = doc.GetText(line);
        int col = offset - line.Offset;
        int s = col, e = col;
        while (s > 0 && char.IsLetterOrDigit(text[s - 1])) s--;
        while (e < text.Length && char.IsLetterOrDigit(text[e])) e++;
        return (line.Offset + s, line.Offset + e);
    }

    /// <summary>
    /// Toggles the task box on the caret line: <c>[ ]</c> ↔ <c>[x]</c>; a list item gets a box,
    /// any other line becomes a task (<c>- [ ] </c>).
    /// </summary>
    public static void ToggleTask(TextEditor editor, MarkdownView view)
    {
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        if (view.ToggleTask(line.LineNumber)) return;
        string text = doc.GetText(line);
        var bullet = System.Text.RegularExpressions.Regex.Match(text, @"^(\s*(?:>\s*)*(?:[-*+]|\d{1,9}[.)])\s+)");
        if (bullet.Success) doc.Insert(line.Offset + bullet.Length, "[ ] ");
        else
        {
            int indent = text.Length - text.TrimStart().Length;
            doc.Insert(line.Offset + indent, "- [ ] ");
        }
    }

    /// <summary>Sets the heading level of the caret line (0 removes it).</summary>
    public static void SetHeading(TextEditor editor, int level)
    {
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        string text = doc.GetText(line);
        int hashes = 0;
        while (hashes < text.Length && text[hashes] == '#') hashes++;
        int remove = hashes;
        if (hashes > 0) while (remove < text.Length && text[remove] == ' ') remove++;
        string prefix = level > 0 ? new string('#', level) + " " : "";
        doc.Replace(line.Offset, remove, prefix);
    }

    public static void InsertTable(TextEditor editor, int columns, int rows, string headerPrefix)
    {
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        string nl = TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);
        string table = TableEditor.NewTable(columns, rows, nl, headerPrefix);
        bool atLineStart = editor.CaretOffset == line.Offset && line.Length == 0;
        doc.Insert(editor.CaretOffset, (atLineStart ? "" : nl) + table + nl);
    }

    /// <summary>Applies table edits in one undo step, bottom-up so earlier positions stay valid.</summary>
    public static void Apply(TextDocument doc, IReadOnlyList<TableEdit> edits)
    {
        if (edits.Count == 0) return;
        using (doc.RunUpdate())
        {
            foreach (var e in edits.OrderByDescending(e => e.Line).ThenByDescending(e => e.Column))
            {
                var line = doc.GetLineByNumber(e.Line + 1);
                string nl = line.DelimiterLength > 0
                    ? doc.GetText(line.EndOffset, line.DelimiterLength)
                    : TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);
                switch (e.Kind)
                {
                    case TableEditKind.Replace:
                        doc.Replace(line.Offset + e.Column, e.Length, e.Text);
                        break;
                    case TableEditKind.InsertLineAfter:
                        doc.Insert(line.EndOffset, nl + e.Text);
                        break;
                    case TableEditKind.InsertLineBefore:
                        doc.Insert(line.Offset, e.Text + nl);
                        break;
                    case TableEditKind.DeleteLine:
                        if (line.DelimiterLength > 0) doc.Remove(line.Offset, line.TotalLength);
                        else if (line.PreviousLine is { } prev) doc.Remove(prev.EndOffset, line.EndOffset - prev.EndOffset);
                        else doc.Remove(line.Offset, line.Length);
                        break;
                }
            }
        }
    }

    /// <summary>Context for table commands at the caret, or null when the caret is not in a table.</summary>
    public static (MarkdownTable Table, int Row, int Column)? TableAtCaret(TextEditor editor, MarkdownView view)
    {
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        var table = view.Model.TableAtLine(line.LineNumber - 1);
        if (table is null) return null;
        int row = line.LineNumber - 1 - table.FirstLine;
        int col = TableEditor.CellIndexAt(table.Rows[row], editor.CaretOffset - line.Offset);
        return (table, row, col);
    }

    public static bool RunTableCommand(TextEditor editor, MarkdownView view, TableCommand command)
    {
        if (TableAtCaret(editor, view) is not var (table, row, col)) return false;
        var src = view.Source;
        var doc = editor.Document;
        int caretLine = editor.TextArea.Caret.Line;
        int caretCol = editor.TextArea.Caret.Column;
        IReadOnlyList<TableEdit> edits = command switch
        {
            TableCommand.InsertRowAbove => TableEditor.InsertRow(table, src, row, below: false),
            TableCommand.InsertRowBelow => TableEditor.InsertRow(table, src, row, below: true),
            TableCommand.InsertColumnLeft => TableEditor.InsertColumn(table, src, col, right: false),
            TableCommand.InsertColumnRight => TableEditor.InsertColumn(table, src, col, right: true),
            TableCommand.DeleteRow => TableEditor.DeleteRow(table, row),
            TableCommand.DeleteColumn => TableEditor.DeleteColumn(table, col),
            TableCommand.MoveRowUp => TableEditor.MoveRow(table, src, row, down: false),
            TableCommand.MoveRowDown => TableEditor.MoveRow(table, src, row, down: true),
            TableCommand.MoveColumnLeft => TableEditor.MoveColumn(table, src, col, right: false),
            TableCommand.MoveColumnRight => TableEditor.MoveColumn(table, src, col, right: true),
            TableCommand.FormatInSource => TableEditor.FormatInSource(table, src, view.Model),
            _ => [],
        };
        if (edits.Count == 0) return true;
        Apply(doc, edits);

        // Keep the caret in a sensible place.
        int targetLine = command switch
        {
            TableCommand.InsertRowBelow => row <= 1 ? table.FirstLine + 3 : caretLine + 1,
            TableCommand.InsertRowAbove => row <= 1 ? table.FirstLine + 3 : caretLine,
            TableCommand.MoveRowUp => caretLine - 1,
            TableCommand.MoveRowDown => caretLine + 1,
            TableCommand.DeleteRow => Math.Min(caretLine, doc.LineCount),
            _ => caretLine,
        };
        targetLine = Math.Clamp(targetLine, 1, doc.LineCount);
        var newTable = view.Model.TableAtLine(targetLine - 1);
        int newCol = command switch
        {
            TableCommand.InsertColumnRight or TableCommand.MoveColumnRight => col + 1,
            TableCommand.MoveColumnLeft => Math.Max(0, col - 1),
            TableCommand.InsertRowAbove or TableCommand.InsertRowBelow => 0,
            _ => col,
        };
        if (newTable?.RowAtLine(targetLine - 1) is { } newRow && newRow.Cells.Count > 0)
        {
            var cell = newRow.Cells[Math.Min(newCol, newRow.Cells.Count - 1)];
            var dl = doc.GetLineByNumber(targetLine);
            string text = doc.GetText(dl);
            int end = cell.ContentEnd(text), start = cell.ContentStart(text);
            editor.CaretOffset = dl.Offset + (start < end ? end : Math.Min(cell.Start + 1, cell.End));
        }
        else
        {
            editor.TextArea.Caret.Position = new TextViewPosition(targetLine, Math.Min(caretCol, doc.GetLineByNumber(targetLine).Length + 1));
        }
        return true;
    }

    /// <summary>Tab / Shift+Tab inside a table moves between cells; Tab in the last cell adds a row.</summary>
    public static bool HandleTab(TextEditor editor, MarkdownView view, bool backwards)
    {
        if (editor.SelectionLength > 0 && editor.TextArea.Selection.IsMultiline) return false;
        if (TableAtCaret(editor, view) is not var (table, _, _)) return false;
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        var target = TableEditor.AdjacentCell(table, view.Source, line.LineNumber - 1, editor.CaretOffset - line.Offset, !backwards);
        if (target is null)
        {
            if (backwards) return true;
            Apply(editor.Document, TableEditor.InsertRow(table, view.Source, table.Rows.Count - 1, below: true));
            var newLine = editor.Document.GetLineByNumber(table.LastLine + 2);
            var row = view.Model.TableAtLine(newLine.LineNumber - 1)?.RowAtLine(newLine.LineNumber - 1);
            editor.CaretOffset = newLine.Offset + (row is { Cells.Count: > 0 } ? row.Cells[0].Start + 1 : 0);
            return true;
        }
        var dl = editor.Document.GetLineByNumber(target.Value.Line + 1);
        editor.SelectionLength = 0;
        editor.CaretOffset = dl.Offset + target.Value.Column;
        return true;
    }

    /// <summary>
    /// Left/Right that treat hidden markup as non-existent: one key press always moves over one
    /// visible character. Returns false to let the editor handle the key.
    /// </summary>
    public static bool HandleHorizontalMove(TextEditor editor, MarkdownView view, bool right, bool extend)
    {
        var area = editor.TextArea;
        var doc = editor.Document;
        if (!extend && editor.SelectionLength > 0) return false;
        int caret = editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var render = view.RenderFor(line);
        if (render.Hidden.Count == 0) return false;
        int col = caret - line.Offset;

        int target = col;
        if (right)
        {
            target = SkipHiddenForward(render, target);
            if (target >= line.Length) return false;
            target += char.IsHighSurrogate(doc.GetCharAt(line.Offset + target)) && target + 1 < line.Length ? 2 : 1;
        }
        else
        {
            target = SkipHiddenBackward(render, target);
            if (target <= 0) return false;
            target -= target >= 2 && char.IsLowSurrogate(doc.GetCharAt(line.Offset + target - 1)) ? 2 : 1;
        }

        int newOffset = line.Offset + target;
        if (extend)
        {
            var anchor = area.Selection.IsEmpty ? caret : (area.Selection.SurroundingSegment.Offset == caret
                ? area.Selection.SurroundingSegment.EndOffset : area.Selection.SurroundingSegment.Offset);
            area.Caret.Offset = newOffset;
            area.Selection = Selection.Create(area, anchor, newOffset);
        }
        else
        {
            area.Caret.Offset = newOffset;
        }
        area.Caret.BringCaretToView();
        return true;
    }

    private static int SkipHiddenForward(LineRender render, int col)
    {
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (var h in render.Hidden)
                if (h.Start == col) { col = h.End; moved = true; }
        }
        return col;
    }

    private static int SkipHiddenBackward(LineRender render, int col)
    {
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (var h in render.Hidden)
                if (h.End == col) { col = h.Start; moved = true; }
        }
        return col;
    }

    /// <summary>
    /// Backspace/Delete next to hidden markup deletes the visible neighbour instead of a marker character.
    /// If that empties a styled span, the markers are removed too.
    /// </summary>
    public static bool HandleDelete(TextEditor editor, MarkdownView view, bool backwards)
    {
        if (editor.SelectionLength > 0) return false;
        var doc = editor.Document;
        int caret = editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var render = view.RenderFor(line);
        if (render.Hidden.Count == 0) return false;
        int col = caret - line.Offset;

        int target = backwards ? SkipHiddenBackward(render, col) : SkipHiddenForward(render, col);
        int charCol = backwards ? target - 1 : target;
        if (charCol < 0 || charCol >= line.Length) return false; // line joins: default behaviour
        if (target == col && !EmptiesSpan(render, charCol)) return false;
        if (render.IsHidden(charCol)) return false;

        // Deleting the only character of a styled span: remove the whole span with its markers.
        foreach (var s in render.Styles)
        {
            if (s.Start != charCol || s.End != charCol + 1) continue;
            var open = render.Hidden.LastOrDefault(h => h.End == s.Start);
            var close = render.Hidden.FirstOrDefault(h => h.Start == s.End);
            if (open.Length == 0 || close.Length == 0) break;
            doc.Remove(line.Offset + open.Start, close.End - open.Start);
            editor.CaretOffset = line.Offset + open.Start;
            return true;
        }

        doc.Remove(line.Offset + charCol, 1);
        editor.CaretOffset = line.Offset + (backwards ? col - 1 : col);
        return true;
    }

    private static bool EmptiesSpan(LineRender render, int charCol) =>
        render.Styles.Any(s => s.Start == charCol && s.End == charCol + 1 &&
                               render.Hidden.Any(h => h.End == s.Start) && render.Hidden.Any(h => h.Start == s.End));

}

public enum TableCommand
{
    InsertRowAbove,
    InsertRowBelow,
    InsertColumnLeft,
    InsertColumnRight,
    DeleteRow,
    DeleteColumn,
    MoveRowUp,
    MoveRowDown,
    MoveColumnLeft,
    MoveColumnRight,
    FormatInSource,
}
