using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>Editing commands for Markdown that work on the source text while it is displayed styled.</summary>
public static class MarkdownCommands
{
    /// <summary>
    /// Toggles a style marker (e.g. ** for bold) on the selection or the word at the caret. A selection is styled
    /// piece by piece — each selected table cell, each line after its heading/list/quote prefix — and every change
    /// is checked first (<see cref="InlineStyleEdit"/>): the text must look the same apart from the style and a table
    /// row must keep its cells. Pieces that cannot be styled that way are left alone; returns how many.
    /// </summary>
    public static int ToggleStyle(TextEditor editor, MarkdownView view, string marker, MdStyle style)
    {
        var doc = editor.Document;
        int firstLine = doc.GetLineByOffset(editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset).LineNumber;
        int lastLine = doc.GetLineByOffset(editor.SelectionLength > 0 ? editor.SelectionStart + editor.SelectionLength : editor.CaretOffset).LineNumber;
        // Selected table cells: their lines are the ones changed (and to be tidied up), wherever the caret is.
        if (editor.SelectionLength == 0 && view.CellSelectionPieces() is { Count: > 0 } cells)
            (firstLine, lastLine) = (cells.Min(c => c.Line), cells.Max(c => c.Line));
        // Parsers and line kinds taken before the change: inside the update the model is not reparsed per line.
        var parsers = ParsersFor(view, doc, Enumerable.Range(firstLine, Math.Max(0, Math.Min(lastLine, doc.LineCount) - firstLine + 1)));
        // One undo step for the whole change.
        using (doc.RunUpdate())
        {
            int failed = ToggleStyleCore(editor, view, marker, style, parsers);
            // "**Prost****y**" → "**Prosty**": neighbouring spans of the same style become one.
            for (int n = Math.Min(lastLine, doc.LineCount); n >= firstLine; n--)
                if (parsers.TryGetValue(n, out var p) && p is not null) MergeAdjacentSpans(doc, view, n, p);
            // A selection whose text went with the markers is empty, but stays a selection object: the next typed
            // character would then not move the caret (and "ab" would come out as "ba").
            if (editor.SelectionLength == 0) editor.TextArea.ClearSelection();
            return failed;
        }
    }

    private static int ToggleStyleCore(TextEditor editor, MarkdownView view, string marker, MdStyle style, Dictionary<int, Func<string, LineRender?>?> parsers)
    {
        var doc = editor.Document;
        List<(int Line, int Start, int End)> pieces;
        if (editor.SelectionLength == 0 && view.CellSelectionPieces() is { Count: > 0 } cells)
            pieces = cells; // selected table cells (a column, a row, part of a grid cell)
        else if (editor.SelectionLength == 0 || editor.TextArea.Selection is RectangleSelection)
        {
            // The caret on a table row but not in a cell's text (at the start or end of the line, on a pipe, or
            // anywhere when the table is drawn as a grid): the whole row.
            if (RowPiecesAtCaret(editor, view) is not { } row) return ToggleAtCaret(editor, view, marker, style);
            pieces = row;
        }
        else pieces = SelectedPieces(editor, view);
        if (pieces.Count == 0) return 0;
        // Off when every selected letter and digit already has the style. Punctuation and spaces do not count: in
        // "**adjective agreement**:" a selection up to the line end takes the plain colon, and the user means the
        // bold words (it used to add bold instead, which looked like nothing happened). Only a selection of
        // punctuation alone is judged by all its characters.
        bool HasStyle(LineRender render, int i) => render.Styles.Any(s => i >= s.Start && i < s.End && s.Style.HasFlag(style));
        bool remove = pieces.All(p =>
        {
            var line = doc.GetLineByNumber(p.Line);
            var render = view.RenderFor(line);
            string lineText = doc.GetText(line);
            bool anyWord = false;
            for (int i = p.Start; i < p.End && i < lineText.Length; i++)
            {
                if (render.IsHidden(i) || !char.IsLetterOrDigit(lineText[i])) continue;
                anyWord = true;
                if (!HasStyle(render, i)) return false;
            }
            if (anyWord) return true;
            for (int i = p.Start; i < p.End; i++)
                if (!render.IsHidden(i) && !HasStyle(render, i)) return false;
            return true;
        });
        int failed = 0;
        using (doc.RunUpdate())
        {
            foreach (var group in pieces.GroupBy(p => p.Line).Reverse())
            {
                var line = doc.GetLineByNumber(group.Key);
                string old = doc.GetText(line);
                string text = old;
                var parse = parsers.TryGetValue(group.Key, out var known) && known is not null ? known : ParserFor(view, doc, group.Key);
                foreach (var p in group.OrderByDescending(p => p.Start))
                {
                    string? result = remove ? Unwrap(text, p.Start, p.End, marker, style, parse)
                        : InlineStyleEdit.TryWrap(text, p.Start, p.End, marker, style, parse) ?? WrapInParts(text, p.Start, p.End, marker, style, parse);
                    if (result is null) { failed++; continue; }
                    text = result;
                }
                if (text != old) ReplaceDifference(doc, line.Offset, old, text);
            }
        }
        return failed;
    }

    /// <summary>
    /// All cells of the table row at the caret, when the caret is not inside a cell's text; otherwise null.
    /// </summary>
    private static List<(int Line, int Start, int End)>? RowPiecesAtCaret(TextEditor editor, MarkdownView view)
    {
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        if (view.Model.TableAtLine(line.LineNumber - 1)?.RowAtLine(line.LineNumber - 1) is not { } row) return null;
        if (row.Kind == MdLineKind.TableSeparator) return [];
        string text = doc.GetText(line);
        int col = editor.CaretOffset - line.Offset;
        var cells = row.Cells.Select(c => (Start: c.ContentStart(text), End: c.ContentEnd(text))).Where(c => c.End > c.Start).ToList();
        if (!view.TablesAsGrid && cells.Any(c => col >= c.Start && col <= c.End)) return null; // in a cell: the word there
        // A grid: the current cell (the one the caret is in) is what the command acts on.
        if (view.TablesAsGrid && view.CurrentCell is { } current && current.Line == line.LineNumber && current.Cell < row.Cells.Count)
        {
            var cell = row.Cells[current.Cell];
            int cs = cell.ContentStart(text), ce = cell.ContentEnd(text);
            return ce > cs ? [(line.LineNumber, cs, ce)] : [];
        }
        return cells.Select(c => (line.LineNumber, c.Start, c.End)).ToList();
    }

    /// <summary>No selection: inside a styled span its markers are removed; on a word the word is styled.</summary>
    private static int ToggleAtCaret(TextEditor editor, MarkdownView view, string marker, MdStyle style)
    {
        var doc = editor.Document;
        int caret = editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var render = view.RenderFor(line);
        int col = caret - line.Offset;
        string text = doc.GetText(line);
        var parse = ParserFor(view, doc, line.LineNumber);
        foreach (var s in render.Styles)
        {
            if (!s.Style.HasFlag(style) || col < s.Start || col > s.End) continue;
            if (Unwrap(text, s.Start, s.End, marker, style, parse) is { } removed)
            {
                ReplaceDifference(doc, line.Offset, text, removed);
                return 0;
            }
        }
        var (ws, we) = WordAt(doc, caret);
        if (we > ws)
        {
            if (InlineStyleEdit.TryWrap(text, ws - line.Offset, we - line.Offset, marker, style, parse) is not { } wrapped) return 1;
            ReplaceDifference(doc, line.Offset, text, wrapped);
            editor.CaretOffset = Math.Min(caret + marker.Length, line.Offset + wrapped.Length);
            return 0;
        }
        // No word: the style for what is typed next here — but never outside a table cell's content.
        if (view.Model.TableAtLine(line.LineNumber - 1)?.RowAtLine(line.LineNumber - 1) is { } row
            && !row.Cells.Any(c => col >= c.Start && col <= c.End))
            return 1;
        view.TogglePendingStyle(marker);
        return 0;
    }

    /// <summary>
    /// A range that crosses other markup ("ab **cd** ef" with "b **c" selected) cannot get one pair of markers
    /// without restyling the text around it: each part between the markers it crosses is wrapped on its own
    /// ("a*b* ***c***d"). Null when a part cannot be wrapped either.
    /// </summary>
    private static string? WrapInParts(string text, int start, int end, string marker, MdStyle style, Func<string, LineRender?> parse)
    {
        if (parse(text) is not { } render) return null;
        // Parts: runs of visible characters with the same styles, without the hidden markup between them.
        var parts = new List<(int Start, int End)>();
        MdStyle StyleAt(int i) { var st = MdStyle.None; foreach (var s in render.Styles) if (i >= s.Start && i < s.End) st |= s.Style; return st; }
        int ps = -1;
        for (int i = start; i <= end; i++)
        {
            bool visible = i < end && !render.IsHidden(i);
            if (ps >= 0 && (!visible || StyleAt(i) != StyleAt(ps)))
            {
                int pe = i;
                while (pe > ps && char.IsWhiteSpace(text[pe - 1])) pe--;
                int s0 = ps;
                while (s0 < pe && char.IsWhiteSpace(text[s0])) s0++;
                if (pe > s0) parts.Add((s0, pe));
                ps = -1;
            }
            if (visible && ps < 0) ps = i;
        }
        if (parts.Count < 2) return null;
        string result = text;
        foreach (var (s, e) in Enumerable.Reverse(parts))
        {
            if (StyleAt(s).HasFlag(style)) continue;
            if (InlineStyleEdit.TryWrap(result, s, e, marker, style, parse) is not { } wrapped) return null;
            result = wrapped;
        }
        return result;
    }

    /// <summary>Removes the style of the span around [start, end): the markers right outside it, or those of the enclosing span.</summary>
    /// <summary>The ways a style may be written: bold as ** or __, italic as * or _ (files from elsewhere use both).</summary>
    private static string[] MarkerVariants(string marker) => marker switch
    {
        "**" => ["**", "__"],
        "*" => ["*", "_"],
        _ => [marker],
    };

    private static string? TryUnwrapAny(string text, int start, int end, string marker, Func<string, LineRender?> parse) =>
        MarkerVariants(marker).Select(m => InlineStyleEdit.TryUnwrap(text, start, end, m, parse)).FirstOrDefault(r => r is not null);

    private static string? Unwrap(string text, int start, int end, string marker, MdStyle style, Func<string, LineRender?> parse)
    {
        if (TryUnwrapAny(text, start, end, marker, parse) is { } direct) return direct;
        if (parse(text) is not { } render) return null;
        // Part of a span: the style ends before the range and starts again after it ("**p**ogr**ubieniem**").
        if (UnwrapPart(text, start, end, marker, style, parse, render) is { } part) return part;
        // Every styled span that overlaps the range (the range may include the markers themselves), right to left
        // so the earlier positions stay valid.
        var spans = render.Styles.Where(s => s.Style.HasFlag(style) && s.Start < end + marker.Length && s.End > start - marker.Length)
            .OrderByDescending(s => s.Start).ToList();
        if (spans.Count == 0) return null;
        string result = text;
        foreach (var s in spans)
        {
            if (TryUnwrapAny(result, s.Start, s.End, marker, parse) is not { } next) return null;
            result = next;
        }
        return result;
    }

    /// <summary>
    /// Removes the style from [start, end) only, when that is a part of one styled span: the span is closed before
    /// the range and opened again after it (spaces at the edges stay outside the markers). Null when the range is
    /// the whole span (the markers just go) or the result would show anything else.
    /// </summary>
    private static string? UnwrapPart(string text, int start, int end, string marker, MdStyle style, Func<string, LineRender?> parse, LineRender render)
    {
        // Only the visible characters of the range count.
        while (start < end && (render.IsHidden(start) || char.IsWhiteSpace(text[start]))) start++;
        while (end > start && (render.IsHidden(end - 1) || char.IsWhiteSpace(text[end - 1]))) end--;
        if (end <= start) return null;
        var span = render.Styles.Where(s => s.Style.HasFlag(style) && s.Start <= start && s.End >= end)
            .OrderBy(s => s.End - s.Start).FirstOrDefault();
        if (span.Style == MdStyle.None) return null;
        // Punctuation left alone at an edge of the span goes with the range: it cannot keep the style by itself
        // ("**Ważna uwaga:**" without "uwaga" would be "**Ważna** uwaga**:**", and a ** between a letter and a colon
        // opens nothing), so "uwaga:" loses it and "Ważna" stays bold.
        bool OnlyPunctuation(int a, int b)
        {
            for (int i = a; i < b; i++)
                if (!render.IsHidden(i) && char.IsLetterOrDigit(text[i])) return false;
            return true;
        }
        if (end < span.End && OnlyPunctuation(end, span.End)) end = span.End;
        if (start > span.Start && OnlyPunctuation(span.Start, start)) start = span.Start;
        if (span.Start == start && span.End == end) return null;
        var open = render.Hidden.LastOrDefault(h => h.End == span.Start);
        if (open.Length == 0) return null;
        string m = new(text[open.Start], marker.Length); // "**" or "__"
        int close = start, reopen = end;
        while (close > span.Start && char.IsWhiteSpace(text[close - 1])) close--;
        while (reopen < span.End && char.IsWhiteSpace(text[reopen])) reopen++;
        if (close == span.Start && reopen == span.End) return null;
        string r = text;
        if (reopen == span.End)
        {
            if (span.End + m.Length > r.Length || r.Substring(span.End, m.Length) != m) return null;
            r = r.Remove(span.End, m.Length);
        }
        else r = r.Insert(reopen, m);
        if (close == span.Start)
        {
            if (span.Start < m.Length || r.Substring(span.Start - m.Length, m.Length) != m) return null;
            r = r.Remove(span.Start - m.Length, m.Length);
        }
        else r = r.Insert(close, m);

        // The same characters; the range (and the spaces at its edges) lose the style, nothing else changes.
        if (parse(r) is not { } after) return null;
        var (oldText, oldStyles) = InlineStyleEdit.Visible(text, render);
        var (newText, newStyles) = InlineStyleEdit.Visible(r, after);
        if (newText != oldText) return null;
        int Vis(int p) { int n = 0; for (int i = 0; i < p; i++) if (!render.IsHidden(i)) n++; return n; }
        int vc = Vis(close), vs = Vis(start), ve = Vis(end), vr = Vis(reopen);
        for (int i = 0; i < newStyles.Length; i++)
        {
            var without = oldStyles[i] & ~style;
            bool ok = i >= vs && i < ve ? newStyles[i] == without
                : i >= vc && i < vr ? newStyles[i] == without || newStyles[i] == oldStyles[i]
                : newStyles[i] == oldStyles[i];
            if (!ok) return null;
        }
        return r;
    }

    /// <summary>How a candidate line is checked: as a row of its table, or as an ordinary line.</summary>
    /// <summary>
    /// Writes the spans of one style as another (highlight ↔ bold ↔ italic) in the selection, or in the whole document
    /// without one; one undo step. Returns how many spans were converted and how many had to be left as they are.
    /// </summary>
    public static (int Converted, int Skipped) ConvertStyle(TextEditor editor, MarkdownView view, MdStyle from, MdStyle to)
    {
        var doc = editor.Document;
        if (editor.SelectionLength == 0 && view.CellSelectionPieces() is { Count: > 0 } cells)
            return ApplyToPieces(view, doc, cells, (text, s, e, parse) => (StyleConversion.Convert(text, from, to, s, e, parse, out int c, out int k), c, k));
        bool selection = editor.SelectionLength > 0;
        int selStart = selection ? editor.SelectionStart : 0, selEnd = selection ? editor.SelectionStart + editor.SelectionLength : doc.TextLength;
        int first = doc.GetLineByOffset(selStart).LineNumber, last = doc.GetLineByOffset(selEnd).LineNumber;
        int converted = 0, skipped = 0;
        var parsers = ParsersFor(view, doc, Enumerable.Range(first, last - first + 1));
        using (doc.RunUpdate())
        {
            for (int n = first; n <= last; n++)
            {
                var line = doc.GetLineByNumber(n);
                if (parsers[n] is null) continue;
                string text = doc.GetText(line);
                int start = Math.Max(0, selStart - line.Offset), end = Math.Min(text.Length, selEnd - line.Offset);
                if (end <= start && selection) continue;
                string result = StyleConversion.Convert(text, from, to, start, selection ? end : text.Length, parsers[n]!, out int c, out int s);
                converted += c;
                skipped += s;
                if (result != text) ReplaceDifference(doc, line.Offset, text, result);
            }
        }
        return (converted, skipped);
    }

    /// <summary>
    /// Removes all inline formatting (bold, italic, strikethrough, highlight, code, sub/superscript, links) from the
    /// selection, or from the spans at the caret; one undo step. Returns the spans cleared and those left as they are.
    /// </summary>
    public static (int Cleared, int Skipped) ClearFormatting(TextEditor editor, MarkdownView view)
    {
        var doc = editor.Document;
        if (editor.SelectionLength == 0 && view.CellSelectionPieces() is { Count: > 0 } cells)
            return ApplyToPieces(view, doc, cells, (text, s, e, parse) => (StyleConversion.Clear(text, s, e, parse, out int c, out int k), c, k));
        int selStart = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        int selEnd = editor.SelectionLength > 0 ? editor.SelectionStart + editor.SelectionLength : editor.CaretOffset;
        int first = doc.GetLineByOffset(selStart).LineNumber, last = doc.GetLineByOffset(selEnd).LineNumber;
        int cleared = 0, skipped = 0;
        var parsers = ParsersFor(view, doc, Enumerable.Range(first, last - first + 1));
        using (doc.RunUpdate())
        {
            for (int n = first; n <= last; n++)
            {
                var line = doc.GetLineByNumber(n);
                if (parsers[n] is null) continue;
                string text = doc.GetText(line);
                int start = Math.Clamp(selStart - line.Offset, 0, text.Length), end = Math.Clamp(selEnd - line.Offset, 0, text.Length);
                if (end <= start && selStart != selEnd) continue;
                string result = StyleConversion.Clear(text, start, end, parsers[n]!, out int c, out int s);
                cleared += c;
                skipped += s;
                if (result != text) ReplaceDifference(doc, line.Offset, text, result);
            }
        }
        return (cleared, skipped);
    }

    /// <summary>
    /// Runs a line change on each of the given pieces (selected table cells), right to left within a line so the
    /// columns of the others stay valid; one undo step. Returns the summed counts.
    /// </summary>
    private static (int, int) ApplyToPieces(MarkdownView view, TextDocument doc, List<(int Line, int Start, int End)> pieces,
        Func<string, int, int, Func<string, LineRender?>, (string Text, int Done, int Skipped)> change)
    {
        int done = 0, skipped = 0;
        var parsers = ParsersFor(view, doc, pieces.Select(p => p.Line).Distinct());
        using (doc.RunUpdate())
        {
            foreach (var group in pieces.GroupBy(p => p.Line))
            {
                var line = doc.GetLineByNumber(group.Key);
                string old = doc.GetText(line), text = old;
                if (parsers[group.Key] is not { } parse) continue;
                foreach (var p in group.OrderByDescending(p => p.Start))
                {
                    var (result, d, s) = change(text, p.Start, Math.Min(p.End, text.Length), parse);
                    text = result;
                    done += d;
                    skipped += s;
                }
                if (text != old) ReplaceDifference(doc, line.Offset, old, text);
            }
        }
        return (done, skipped);
    }

    // ---------------------------------------------------------------- lists

    private static List<string> AllLines(TextDocument doc) => doc.Lines.Select(l => doc.GetText(l)).ToList();

    /// <summary>Applies a list change: the new line, the renumbered items, the caret at the new item's text.</summary>
    private static void Apply(TextEditor editor, ListEdit.Change change, int? replaceFrom = null)
    {
        var doc = editor.Document;
        string nl = AvaloniaEdit.Document.TextUtilities.GetNewLineFromDocument(doc, Math.Max(1, Math.Min(change.InsertAt, doc.LineCount)));
        using (doc.RunUpdate())
        {
            if (replaceFrom is int from)
            {
                // Enter inside an item: the rest of the line goes to the new item.
                var line = doc.GetLineByNumber(change.InsertAt);
                doc.Replace(from, line.EndOffset - from, nl + change.Text);
            }
            else if (change.InsertAt < doc.LineCount)
                doc.Insert(doc.GetLineByNumber(change.InsertAt + 1).Offset, change.Text + nl);
            else
                doc.Insert(doc.TextLength, nl + change.Text);
            foreach (var (index, text) in change.Renumbered)
            {
                var l = doc.GetLineByNumber(index + 1);
                ReplaceDifference(doc, l.Offset, doc.GetText(l), text);
            }
            var newLine = doc.GetLineByNumber(change.InsertAt + 1);
            editor.TextArea.ClearSelection();
            editor.CaretOffset = newLine.Offset + Math.Min(change.CaretColumn, newLine.Length);
        }
    }

    /// <summary>The caret's line is a Markdown list item (or a line of one), outside tables and code.</summary>
    public static bool InList(TextEditor editor, MarkdownView view)
    {
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        if (view.RenderFor(line).Kind != MdLineKind.Normal || view.Model.TableAtLine(line.LineNumber - 1) is not null) return false;
        if (ListEdit.Parse(doc.GetText(line)) is not null) return true;
        // A continuation line: look up a few lines for its item.
        var lines = new List<string>();
        for (int n = Math.Max(1, line.LineNumber - 50); n <= line.LineNumber; n++) lines.Add(doc.GetText(doc.GetLineByNumber(n)));
        return ListEdit.ItemStart(lines, lines.Count - 1) >= 0;
    }

    /// <summary>The caret is in an ordered list (one that <see cref="RenumberList"/> can number).</summary>
    public static bool InOrderedList(TextEditor editor, MarkdownView view)
    {
        if (!InList(editor, view)) return false;
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        var lines = new List<string>();
        for (int n = Math.Max(1, line.LineNumber - 50); n <= line.LineNumber; n++) lines.Add(doc.GetText(doc.GetLineByNumber(n)));
        int start = ListEdit.ItemStart(lines, lines.Count - 1);
        return start >= 0 && ListEdit.Parse(lines[start]) is { Ordered: true };
    }

    /// <summary>Numbers the caret's ordered list 1…N, as one undo step; only the numbers change.</summary>
    public static bool RenumberList(TextEditor editor, MarkdownView view)
    {
        if (!InList(editor, view)) return false;
        var doc = editor.Document;
        int index = doc.GetLineByOffset(editor.CaretOffset).LineNumber - 1;
        if (ListEdit.RenumberList(AllLines(doc), index) is not { } changes) return false;
        using (doc.RunUpdate())
            foreach (var (i, text) in changes)
            {
                var l = doc.GetLineByNumber(i + 1);
                ReplaceDifference(doc, l.Offset, doc.GetText(l), text);
            }
        return true;
    }

    /// <summary>A new empty item before or after the caret's item; an ordered list is renumbered.</summary>
    public static bool InsertListItem(TextEditor editor, MarkdownView view, bool after)
    {
        if (!InList(editor, view)) return false;
        var doc = editor.Document;
        int index = doc.GetLineByOffset(editor.CaretOffset).LineNumber - 1;
        if (ListEdit.InsertItem(AllLines(doc), index, after) is not { } change) return false;
        Apply(editor, change);
        return true;
    }

    /// <summary>
    /// Enter on a list item's line: the rest of the line becomes the next item (renumbered); on an empty item the
    /// marker goes and the list ends. Shift+Enter: a new line inside the item, indented to its text.
    /// </summary>
    public static bool HandleListEnter(TextEditor editor, MarkdownView view, bool shift)
    {
        if (editor.SelectionLength > 0 || editor.IsReadOnly) return false;
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        string text = doc.GetText(line);
        if (view.RenderFor(line).Kind != MdLineKind.Normal || view.Model.TableAtLine(line.LineNumber - 1) is not null) return false;
        if (ListEdit.Parse(text) is not { } item) return false;
        int col = editor.CaretOffset - line.Offset;
        if (col < item.ContentStart) return false;
        string nl = AvaloniaEdit.Document.TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);
        if (shift)
        {
            string indent = item.Prefix + new string(' ', item.ContentStart - item.Prefix.Length);
            doc.Insert(editor.CaretOffset, nl + indent);
            return true;
        }
        if (ListEdit.IsEmptyItem(text))
        {
            doc.Replace(line.Offset, line.Length, "");
            return true;
        }
        using (doc.RunUpdate())
        {
            // Enter inside bold, italic…: the markers are closed and reopened around the break.
            PrepareLineBreakInStyle(editor, view);
            line = doc.GetLineByOffset(editor.CaretOffset);
            int caretCol = editor.CaretOffset - line.Offset;
            if (ListEdit.SplitItem(AllLines(doc), line.LineNumber - 1, caretCol) is not { } change) return false;
            Apply(editor, change, editor.CaretOffset);
        }
        return true;
    }

    /// <summary>
    /// Line parsers for the given lines, taken from the model once (before a change): null for code lines, which are
    /// never restyled. Commands use these inside their update instead of the model.
    /// </summary>
    private static Dictionary<int, Func<string, LineRender?>?> ParsersFor(MarkdownView view, TextDocument doc, IEnumerable<int> lineNumbers)
    {
        var result = new Dictionary<int, Func<string, LineRender?>?>();
        foreach (int n in lineNumbers)
        {
            if (n < 1 || n > doc.LineCount || result.ContainsKey(n)) continue;
            result[n] = view.Model.GetLine(n - 1).Kind is MdLineKind.Code or MdLineKind.FenceDelimiter ? null : ParserFor(view, doc, n);
        }
        return result;
    }

    private static Func<string, LineRender?> ParserFor(MarkdownView view, TextDocument doc, int lineNumber)
    {
        if (view.Model.TableAtLine(lineNumber - 1) is { } table && table.RowAtLine(lineNumber - 1) is { } row)
        {
            string Line(int index) => doc.GetText(doc.GetLineByNumber(index + 1));
            bool isHeader = lineNumber - 1 == table.FirstLine;
            return InlineStyleEdit.TableRowParser(Line(table.FirstLine), Line(table.FirstLine + 1), isHeader, row.Cells.Count);
        }
        return InlineStyleEdit.LineParser;
    }

    /// <summary>Replaces only the part of a line that changed (small undo step, caret and anchors kept).</summary>
    private static void ReplaceDifference(TextDocument doc, int lineOffset, string oldText, string newText)
    {
        int prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;
        doc.Replace(lineOffset + prefix, oldText.Length - prefix - suffix, newText[prefix..(newText.Length - suffix)]);
    }

    /// <summary>
    /// The pieces of a selection to style (line number, start and end column): the selected part of each table
    /// cell, of each ordinary line after its heading/list/quote prefix; never pipes, prefixes or code blocks.
    /// </summary>
    private static List<(int Line, int Start, int End)> SelectedPieces(TextEditor editor, MarkdownView view)
    {
        var doc = editor.Document;
        int selStart = editor.SelectionStart, selEnd = selStart + editor.SelectionLength;
        var firstLine = doc.GetLineByOffset(selStart);
        var lastLine = doc.GetLineByOffset(selEnd);
        var pieces = new List<(int, int, int)>();
        for (int n = firstLine.LineNumber; n <= lastLine.LineNumber; n++)
        {
            var line = doc.GetLineByNumber(n);
            string text = doc.GetText(line);
            void Add(int start, int end)
            {
                start = Math.Max(start, selStart - line.Offset);
                end = Math.Min(end, selEnd - line.Offset);
                // Spaces and hidden markers at the edges do not count (a selection of a lone "**" is nothing).
                var render = view.RenderFor(line);
                while (start < end && (char.IsWhiteSpace(text[start]) || render.IsHidden(start))) start++;
                while (end > start && (char.IsWhiteSpace(text[end - 1]) || render.IsHidden(end - 1))) end--;
                if (end > start) pieces.Add((n, start, end));
            }
            if (view.Model.TableAtLine(n - 1)?.RowAtLine(n - 1) is { } row)
            {
                if (row.Kind == MdLineKind.TableSeparator) continue;
                foreach (var cell in row.Cells)
                {
                    int cs = cell.ContentStart(text), ce = cell.ContentEnd(text);
                    if (ce > cs) Add(cs, ce);
                }
                continue;
            }
            if (view.RenderFor(line).Kind is MdLineKind.Code or MdLineKind.FenceDelimiter) continue;
            int prefix = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(#{1,6}\s+|(>\s*)+|[-*+]\s+(\[[ xX]\]\s+)?|\d{1,9}[.)]\s+)*").Length;
            Add(prefix, text.Length);
        }
        return pieces;
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

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico"];

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// <c>![name](path)</c> for an image file: the path is relative to the Markdown file's folder (with forward
    /// slashes, so it works on GitHub too) or absolute when the file has no folder or is on another drive.
    /// Paths with spaces or parentheses are written in angle brackets.
    /// </summary>
    public static string ImageMarkdown(string? markdownDirectory, string imagePath)
    {
        string full = Path.GetFullPath(imagePath);
        string target = full;
        if (markdownDirectory is { Length: > 0 }
            && string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(Path.GetFullPath(markdownDirectory)), StringComparison.OrdinalIgnoreCase))
            target = Path.GetRelativePath(markdownDirectory, full);
        target = target.Replace('\\', '/');
        if (target.IndexOfAny([' ', '(', ')', '<', '>']) >= 0) target = "<" + target + ">";
        string alt = Path.GetFileNameWithoutExtension(full).Replace('[', '(').Replace(']', ')');
        return $"![{alt}]({target})";
    }

    /// <summary>Inserts image references at <paramref name="offset"/>, one per line when there are several.</summary>
    public static void InsertImages(TextEditor editor, string? markdownDirectory, IReadOnlyList<string> images, int offset)
    {
        if (images.Count == 0) return;
        var doc = editor.Document;
        offset = Math.Clamp(offset, 0, doc.TextLength);
        string newLine = doc.LineCount > 1 ? TextUtilities.GetNewLineFromDocument(doc, 1) : Environment.NewLine;
        string text = string.Join(newLine, images.Select(i => ImageMarkdown(markdownDirectory, i)));
        doc.Insert(offset, text);
        editor.CaretOffset = offset + text.Length;
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
            TableCommand.Split => TableEditor.Split(table, src, row),
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
            TableCommand.Split => row >= 2 ? caretLine + 3 : caretLine,
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
        // Only markers between the caret and the line's edge: go to the edge first, then the default move
        // goes on to the next line (otherwise the first press only steps over the markers and seems to do nothing).
        bool ToEdge(int edge)
        {
            if (edge == col) return false;
            int edgeOffset = line.Offset + edge;
            if (extend)
            {
                var anchor = area.Selection.IsEmpty ? caret : (area.Selection.SurroundingSegment.Offset == caret
                    ? area.Selection.SurroundingSegment.EndOffset : area.Selection.SurroundingSegment.Offset);
                area.Caret.Offset = edgeOffset;
                area.Selection = Selection.Create(area, anchor, edgeOffset);
            }
            else area.Caret.Offset = edgeOffset;
            return false;
        }
        if (right)
        {
            target = SkipHiddenForward(render, target);
            if (target >= line.Length) return ToEdge(line.Length);
            target += char.IsHighSurrogate(doc.GetCharAt(line.Offset + target)) && target + 1 < line.Length ? 2 : 1;
        }
        else
        {
            target = SkipHiddenBackward(render, target);
            if (target <= 0) return ToEdge(0);
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

    /// <summary>The hidden "## " of a heading (after optional indentation), or null.</summary>
    private static HiddenRange? HeadingPrefix(LineRender render, string text)
    {
        if (render.Kind != MdLineKind.Heading) return null;
        int ws = 0;
        while (ws < text.Length && char.IsWhiteSpace(text[ws])) ws++;
        if (ws >= text.Length || text[ws] != '#') return null;
        var h = render.Hidden.FirstOrDefault(h => h.Start == ws);
        return h.Length > 0 ? h : null;
    }

    /// <summary>
    /// Backspace/Delete next to hidden markup deletes the visible neighbour instead of a marker character.
    /// If that empties a styled span, the markers are removed too.
    /// </summary>
    public static bool HandleDelete(TextEditor editor, MarkdownView view, bool backwards)
    {
        if (SelectionShowsNothing(editor, view))
        {
            // Only hidden markers selected: as without a selection, from where it is seen.
            int at = backwards ? editor.SelectionStart : editor.SelectionStart + editor.SelectionLength;
            editor.TextArea.ClearSelection();
            editor.CaretOffset = at;
        }
        if (editor.SelectionLength > 0) return DeleteSelectionKeepingMarkers(editor, view);
        var doc = editor.Document;
        int caret = editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        if (view.RenderFor(line).Kind == MdLineKind.Rule)
        {
            // A horizontal rule is drawn across the view: Backspace or Delete anywhere on it removes the whole line.
            int start = line.Offset, end = line.EndOffset + line.DelimiterLength;
            if (line.DelimiterLength == 0 && line.PreviousLine is { } previous) start = previous.EndOffset;
            doc.Remove(start, end - start);
            return true;
        }
        int col = caret - line.Offset;
        // A table drawn as a grid shows a row as one element: the default deletion would take the whole row.
        // Rows are edited in the cell editor; only joining with the neighbouring line stays.
        if (view.TablesAsGrid && view.Model.TableAtLine(line.LineNumber - 1) is not null && (backwards ? col > 0 : col < line.Length))
            return true;
        var render = view.RenderFor(line);
        string text = doc.GetText(line);
        // Backspace at the start of a heading's text: the line becomes ordinary text (the hidden "## " goes),
        // instead of joining the previous line with a visible "##".
        if (backwards && HeadingPrefix(render, text) is { } hp && col >= hp.Start && col <= hp.End && (col > hp.Start || hp.Start == 0))
        {
            doc.Remove(line.Offset + hp.Start, hp.Length);
            editor.CaretOffset = line.Offset + hp.Start;
            return true;
        }
        // Delete at the end of the line before a heading: its text joins this line as ordinary text.
        if (!backwards && SkipHiddenForward(render, col) >= line.Length && line.NextLine is { } next
            && HeadingPrefix(view.RenderFor(next), doc.GetText(next)) is { Start: 0 } np)
        {
            doc.Remove(line.EndOffset, line.DelimiterLength + np.Length);
            editor.CaretOffset = line.EndOffset;
            return true;
        }
        if (render.Hidden.Count == 0) return false;

        int target = backwards ? SkipHiddenBackward(render, col) : SkipHiddenForward(render, col);
        int charCol = backwards ? target - 1 : target;
        if (charCol < 0 || charCol >= line.Length)
        {
            // Line joins: default behaviour, but from the line's edge, not through the markers before it.
            if (target != col) editor.CaretOffset = line.Offset + target;
            return false;
        }
        if (render.IsHidden(charCol)) return false;

        // The edit that removes just this character from what the line shows (checked with the parser).
        if (CheckedDeletion(doc, view, line, charCol, backwards ? col - 1 : col) is { } edit)
        {
            if (edit.Text == doc.GetText(line).Remove(charCol, 1) && target == col) return false; // plain: default behaviour
            ReplaceDifference(doc, line.Offset, doc.GetText(line), edit.Text);
            editor.CaretOffset = line.Offset + edit.Caret;
            return true;
        }
        if (target == col && !EmptiesSpan(render, charCol)) return false;

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

    /// <summary>
    /// The line without the visible character at <paramref name="charCol"/>, such that it shows exactly the other
    /// characters with their styles, and the caret column. Tried in order: the character alone (caret kept next to
    /// the same markers); a space left at the edge of a styled span moved out of it ("**Prosty w**" →
    /// "**Prosty** ", "**w ord**" → " **ord**"); the character with the markers around it, as many as become
    /// empty ("***o***" → ""). Null when none of these works.
    /// </summary>
    private static (string Text, int Caret)? CheckedDeletion(TextDocument doc, MarkdownView view, DocumentLine line, int charCol, int plainCaret)
    {
        string text = doc.GetText(line);
        var parse = ParserFor(view, doc, line.LineNumber);
        if (parse(text) is not { } before) return null;
        var (shown, styles) = InlineStyleEdit.Visible(text, before);
        int v = 0;
        for (int i = 0; i < charCol; i++) if (!before.IsHidden(i)) v++;
        if (v >= shown.Length) return null;
        string expected = shown.Remove(v, 1);
        var expectedStyles = styles.Where((_, i) => i != v).ToArray();
        const MdStyle Inline = SplittableStyles | MdStyle.Link;
        bool Shows(string candidate)
        {
            if (parse(candidate) is not { } r) return false;
            var (t, st) = InlineStyleEdit.Visible(candidate, r);
            if (t != expected) return false;
            for (int i = 0; i < st.Length; i++)
                if (!char.IsWhiteSpace(t[i]) && (st[i] & Inline) != (expectedStyles[i] & Inline)) return false;
            return true;
        }
        var candidates = new List<(string Text, int Caret)>();
        bool HiddenAt(int i) => i >= 0 && i < text.Length && before.IsHidden(i);
        int RunEnd(int i) { while (HiddenAt(i)) i++; return i; }
        int RunStart(int i) { while (HiddenAt(i - 1)) i--; return i; }
        // 1. The character alone.
        candidates.Add((text.Remove(charCol, 1), plainCaret));
        // 2. A space at the edge of the span: the markers go to the other side of it.
        if (charCol > 0 && text[charCol - 1] == ' ' && HiddenAt(charCol + 1))
        {
            int end = RunEnd(charCol + 1);
            string markers = text[(charCol + 1)..end];
            candidates.Add((text[..(charCol - 1)] + markers + " " + text[end..], charCol - 1 + markers.Length + 1));
        }
        if (charCol + 1 < text.Length && text[charCol + 1] == ' ' && HiddenAt(charCol - 1))
        {
            int start = RunStart(charCol);
            string markers = text[start..charCol];
            candidates.Add((text[..start] + " " + markers + text[(charCol + 2)..], start));
        }
        // 3. The character with the markers around it, one pair of hidden runs at a time.
        int a = charCol, b = charCol + 1;
        while (HiddenAt(a - 1) && HiddenAt(b))
        {
            // Hidden ranges are whole markers: step over one range on each side.
            var left = before.Hidden.LastOrDefault(h => h.End == a);
            var right = before.Hidden.FirstOrDefault(h => h.Start == b);
            if (left.Length == 0 || right.Length == 0) break;
            // "*b***" closes italic and bold in one run: take only the partner of the opening marker.
            string open = text[left.Start..left.End];
            int rightEnd = right.Length > open.Length && text.AsSpan(b, open.Length).SequenceEqual(open) ? b + open.Length : right.End;
            a = left.Start;
            b = rightEnd;
            string rest = text[..a] + text[b..];
            candidates.Add((rest, a));
            // The span is gone, and the one around it would end or start with a space now ("**a **").
            const string MarkerChars = "*_~=`";
            int me = a;
            while (me < rest.Length && MarkerChars.Contains(rest[me])) me++;
            if (a > 0 && rest[a - 1] == ' ' && me > a)
                candidates.Add((rest[..(a - 1)] + rest[a..me] + " " + rest[me..], me));
            int ms = a;
            while (ms > 0 && MarkerChars.Contains(rest[ms - 1])) ms--;
            if (a < rest.Length && rest[a] == ' ' && ms < a)
                candidates.Add((rest[..ms] + " " + rest[ms..a] + rest[(a + 1)..], ms));
        }
        foreach (var c in candidates)
            if (Shows(c.Text)) return c;
        // 4. The plain deletion with a marker moved over a space next to it, or (the style cannot stay, as for
        // "r *[x]*" without the space) with that span's markers removed: the same text, without visible markers.
        if (RepairMarkers(text.Remove(charCol, 1), expected, parse) is { } repaired)
            return (repaired, CaretAtVisible(repaired, parse, v));
        return null;
    }

    private static bool IsMarkerChar(char c) => c is '*' or '_' or '~' or '=' or '`';

    /// <summary>
    /// What the line holding <paramref name="start"/> must show after [start, end) (possibly over several lines,
    /// which then become one) is replaced by <paramref name="typed"/>, and where the typed text starts in it.
    /// Null for a table row (its cells are parsed with the table).
    /// </summary>
    private static (string? Shown, int VisibleStart) ShownAfterReplacing(TextDocument doc, MarkdownView view, int start, int end, string typed)
    {
        var first = doc.GetLineByOffset(start);
        var last = doc.GetLineByOffset(end);
        var sb = new System.Text.StringBuilder();
        int visibleStart = -1;
        for (int n = first.LineNumber; n <= last.LineNumber; n++)
        {
            // A table row is parsed with its table: only a change within one row is checked.
            if (view.Model.TableAtLine(n - 1) is not null && first.LineNumber != last.LineNumber) return (null, 0);
            var line = doc.GetLineByNumber(n);
            var render = view.RenderFor(line);
            string text = doc.GetText(line);
            for (int i = 0; i <= text.Length; i++)
            {
                int offset = line.Offset + i;
                if (offset == start && visibleStart < 0) { visibleStart = sb.Length; sb.Append(typed); }
                if (i == text.Length) break;
                if (!render.IsHidden(i) && (offset < start || offset >= end)) sb.Append(text[i]);
            }
        }
        return (sb.ToString(), Math.Max(0, visibleStart));
    }

    /// <summary>True when the selection shows no character (only hidden markers, e.g. a closing "**").</summary>
    public static bool SelectionShowsNothing(TextEditor editor, MarkdownView view)
    {
        if (editor.SelectionLength == 0 || editor.TextArea.Selection is RectangleSelection) return false;
        var doc = editor.Document;
        int a = editor.SelectionStart, b = a + editor.SelectionLength;
        var line = doc.GetLineByOffset(a);
        if (b > line.EndOffset) return false;
        var render = view.RenderFor(line);
        for (int i = a - line.Offset; i < b - line.Offset; i++) if (!render.IsHidden(i)) return false;
        return true;
    }

    /// <summary>
    /// Typing over a selection, like a word processor: the typed text takes the place, and the style, of the
    /// selection's first character; the markers of partly selected spans stay. Within one line the result must
    /// show the old text with the selection replaced by the typed text, without visible markers (a marker left
    /// next to a space is moved over it).
    /// </summary>
    public static void ReplaceSelectionWithTyped(TextEditor editor, MarkdownView view, string typed)
    {
        var doc = editor.Document;
        int start = editor.SelectionStart, length = editor.SelectionLength;
        var line = doc.GetLineByOffset(start);
        var parse = ParserFor(view, doc, line.LineNumber);
        var (expected, visibleStart) = ShownAfterReplacing(doc, view, start, start + length, typed);
        using (doc.RunUpdate())
        {
            doc.Insert(start, typed);
            editor.Select(start + typed.Length, length);
            if (!DeleteSelectionKeepingMarkers(editor, view)) doc.Remove(start + typed.Length, length);
            editor.TextArea.ClearSelection();
            editor.CaretOffset = start + typed.Length;
            if (expected is not null && doc.GetText(line) is var now && parse(now) is { } r && InlineStyleEdit.Visible(now, r).Text != expected
                && RepairMarkers(now, expected, parse) is { } repaired)
            {
                ReplaceDifference(doc, line.Offset, now, repaired);
                editor.CaretOffset = line.Offset + CaretAtVisible(repaired, parse, visibleStart + typed.Length);
            }
            MergeAdjacentSpans(doc, view, doc.GetLineByOffset(editor.CaretOffset).LineNumber);
        }
    }

    /// <summary>
    /// A line whose edit left a marker shown as text ("**Cała** linia" after deleting a word became "** linia"):
    /// the same line with a run of markers moved over the space next to it, or else with a pair of equal marker
    /// runs removed (the style is lost, the text stays), such that it shows <paramref name="expected"/>.
    /// Null when nothing works.
    /// </summary>
    internal static string? RepairMarkers(string text, string expected, Func<string, LineRender?> parse)
    {
        bool Shows(string t) => parse(t) is { } r && InlineStyleEdit.Visible(t, r).Text == expected;
        if (Shows(text)) return text;
        var runs = new List<(int Start, int End)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsMarkerChar(text[i])) continue;
            int e = i;
            while (e < text.Length && text[e] == text[i]) e++;
            runs.Add((i, e));
            i = e - 1;
        }
        foreach (var (s, e) in runs)
        {
            string run = text[s..e];
            if (s > 0 && text[s - 1] == ' ' && Shows(text[..(s - 1)] + run + " " + text[e..])) return text[..(s - 1)] + run + " " + text[e..];
            if (e < text.Length && text[e] == ' ' && Shows(text[..s] + " " + run + text[(e + 1)..])) return text[..s] + " " + run + text[(e + 1)..];
        }
        if (runs.Count > 40) return null;
        // A run whose partner went with the deleted text ("*d gi"): the partner again at the end of the word
        // ("*d* gi"), or at the start of the word before the run.
        foreach (var (s, e) in runs)
        {
            string run = text[s..e];
            int we = e;
            while (we < text.Length && !char.IsWhiteSpace(text[we]) && !IsMarkerChar(text[we])) we++;
            if (we > e && Shows(text[..we] + run + text[we..])) return text[..we] + run + text[we..];
            int ws = s;
            while (ws > 0 && !char.IsWhiteSpace(text[ws - 1]) && !IsMarkerChar(text[ws - 1])) ws--;
            if (ws < s && Shows(text[..ws] + run + text[ws..])) return text[..ws] + run + text[ws..];
        }
        for (int i = 0; i < runs.Count; i++)
            for (int j = i + 1; j < runs.Count; j++)
            {
                var (s1, e1) = runs[i];
                var (s2, e2) = runs[j];
                if (text[s1..e1] != text[s2..e2]) continue;
                string candidate = text[..s1] + text[e1..s2] + text[e2..];
                if (Shows(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>The column in <paramref name="text"/> before its visible character number <paramref name="visible"/>.</summary>
    private static int CaretAtVisible(string text, Func<string, LineRender?> parse, int visible)
    {
        if (parse(text) is not { } r) return Math.Min(text.Length, visible);
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (r.IsHidden(i)) continue;
            if (n == visible) return i;
            n++;
        }
        return text.Length;
    }

    /// <summary>
    /// Deletes the selection without leaving a lone style marker behind. Markers are hidden and have no width, so a
    /// selection made with the mouse easily starts before "**" and ends before the closing "**"; deleting it as it is
    /// would leave the other marker visible. Here a styled span whose whole text is selected goes with both markers,
    /// and the markers of a span that is only partly selected are kept (the text around them is deleted). A
    /// selection end never cuts other hidden markup (a link target). Returns false when the plain deletion is right;
    /// otherwise deletes and leaves the caret where the selection started.
    /// </summary>
    public static bool DeleteSelectionKeepingMarkers(TextEditor editor, MarkdownView view)
    {
        if (editor.SelectionLength == 0 || editor.TextArea.Selection is RectangleSelection) return false;
        var doc = editor.Document;
        // Within one line: what the line must show afterwards, to repair markers left next to a space.
        var selLine = doc.GetLineByOffset(editor.SelectionStart);
        var lineParse = ParserFor(view, doc, selLine.LineNumber);
        var (expectedShown, _) = ShownAfterReplacing(doc, view, editor.SelectionStart, editor.SelectionStart + editor.SelectionLength, "");
        bool done = DeleteSelectionRanges(editor, view);
        if (expectedShown is null) return done;
        using (doc.RunUpdate())
        {
            if (!done)
            {
                // The plain deletion, done here so it can be checked (and repaired) in the same undo step.
                int s = editor.SelectionStart;
                doc.Remove(s, editor.SelectionLength);
                editor.Select(s, 0);
            }
            string now = doc.GetText(selLine);
            if (lineParse(now) is { } after && InlineStyleEdit.Visible(now, after).Text != expectedShown
                && RepairMarkers(now, expectedShown, lineParse) is { } repaired)
            {
                // The deletion left a marker shown as text ("** linia"): moved over the space, or removed.
                int caretCol = editor.CaretOffset - selLine.Offset, visibleCaret = 0;
                for (int k = 0; k < caretCol && k < now.Length; k++) if (!after.IsHidden(k)) visibleCaret++;
                ReplaceDifference(doc, selLine.Offset, now, repaired);
                editor.Select(selLine.Offset + CaretAtVisible(repaired, lineParse, visibleCaret), 0);
            }
            editor.TextArea.ClearSelection();
        }
        return true;
    }

    private static bool DeleteSelectionRanges(TextEditor editor, MarkdownView view)
    {
        var doc = editor.Document;
        int a = editor.SelectionStart, b = a + editor.SelectionLength;
        var keep = new List<(int Start, int End)>();   // marker ranges inside the selection that must stay
        foreach (int n in new[] { doc.GetLineByOffset(a).LineNumber, doc.GetLineByOffset(b).LineNumber }.Distinct())
        {
            var line = doc.GetLineByNumber(n);
            var render = view.RenderFor(line);
            int o = line.Offset;
            var markers = new HashSet<(int, int)>();
            foreach (var s in render.Styles)
            {
                var open = render.Hidden.LastOrDefault(h => h.End == s.Start);
                var close = render.Hidden.FirstOrDefault(h => h.Start == s.End);
                if (open.Length == 0 || close.Length == 0) continue;
                markers.Add((open.Start, open.End));
                markers.Add((close.Start, close.End));
                int os = o + open.Start, cs = o + s.Start, ce = o + s.End, cl = o + close.End;
                if (a <= cs && b >= ce)
                {
                    // The whole text of the span is selected: its markers go too.
                    if (a >= os) a = os;
                    if (b <= cl) b = cl;
                }
                else if (b > os && a < cl)
                {
                    // Partly selected: its markers stay.
                    keep.Add((os, cs));
                    keep.Add((ce, cl));
                }
            }
            // Never cut through other hidden markup (e.g. a link target).
            foreach (var h in render.Hidden)
            {
                if (markers.Contains((h.Start, h.End))) continue;
                int hs = o + h.Start, he = o + h.End;
                if (a > hs && a < he) a = he;
                if (b > hs && b < he) b = hs;
            }
        }
        if (b < a) b = a;
        // The ranges to delete: [a, b) without the kept markers.
        var ranges = new List<(int Start, int End)>();
        int pos = a;
        foreach (var (ks, ke) in keep.Where(k => k.End > a && k.Start < b).Distinct().OrderBy(k => k.Start))
        {
            if (ks > pos) ranges.Add((pos, Math.Min(ks, b)));
            pos = Math.Max(pos, ke);
        }
        if (pos < b) ranges.Add((pos, b));
        if (ranges.Count == 1 && ranges[0] == (editor.SelectionStart, editor.SelectionStart + editor.SelectionLength)) return false;

        using (doc.RunUpdate())
            foreach (var (rs, re) in Enumerable.Reverse(ranges))
                doc.Remove(rs, re - rs);
        int caret = ranges.Count > 0 ? ranges[0].Start : a;
        editor.Select(caret, 0);
        editor.CaretOffset = caret;
        return true;
    }

    /// <summary>
    /// The Markdown to copy for the selection, with every style that overlaps it closed: the selected part of a
    /// styled span is copied between that span's markers, and a marker without its partner is never copied
    /// (it would show as stars after pasting). Null when the selection can be copied as it is.
    /// </summary>
    public static string? BalancedSelectionText(TextEditor editor, MarkdownView view)
    {
        if (editor.SelectionLength == 0 || editor.TextArea.Selection is RectangleSelection) return null;
        var doc = editor.Document;
        int a = editor.SelectionStart, b = a + editor.SelectionLength;
        var sb = new System.Text.StringBuilder(b - a + 16);
        bool changed = false;
        var first = doc.GetLineByOffset(a);
        var last = doc.GetLineByOffset(b);
        for (int n = first.LineNumber; n <= last.LineNumber; n++)
        {
            var line = doc.GetLineByNumber(n);
            string text = doc.GetText(line);
            int from = Math.Max(a, line.Offset) - line.Offset, to = Math.Min(b, line.EndOffset) - line.Offset;
            var render = view.RenderFor(line);
            // Styled spans overlapping the selected part of this line, with their markers.
            var spans = new List<(int Content, int ContentEnd, string Open, string Close, HiddenRange OpenRange, HiddenRange CloseRange)>();
            foreach (var s in render.Styles)
            {
                if (s.Style.HasFlag(MdStyle.Heading) || s.Style.HasFlag(MdStyle.TaskDone)) continue;
                var open = render.Hidden.LastOrDefault(h => h.End == s.Start);
                var close = render.Hidden.FirstOrDefault(h => h.Start == s.End);
                if (open.Length == 0 || close.Length == 0) continue;
                int cs = Math.Max(s.Start, from), ce = Math.Min(s.End, to);
                if (ce <= cs) continue;
                if (from <= open.Start && to >= close.End) continue; // the whole span with both markers: as it is
                spans.Add((cs, ce, text[open.Start..open.End], text[close.Start..close.End], open, close));
            }
            if (spans.Count == 0)
            {
                sb.Append(text, from, to - from);
            }
            else
            {
                changed = true;
                bool IsMarkerOfSpan(int i) => spans.Any(s => i >= s.OpenRange.Start && i < s.OpenRange.End || i >= s.CloseRange.Start && i < s.CloseRange.End);
                for (int i = from; i <= to; i++)
                {
                    foreach (var s in spans.Where(s => s.ContentEnd == i).Reverse()) sb.Append(s.Close);
                    if (i == to) break;
                    foreach (var s in spans.Where(s => s.Content == i)) sb.Append(s.Open);
                    if (!IsMarkerOfSpan(i)) sb.Append(text[i]);
                }
            }
            if (n < last.LineNumber) sb.Append(doc.GetText(line.Offset + line.Length, line.DelimiterLength));
        }
        return changed ? sb.ToString() : null;
    }

    private const MdStyle MergeableStyles = MdStyle.Bold | MdStyle.Italic | MdStyle.Strike | MdStyle.Highlight;

    /// <summary>
    /// Typing: a character typed right after the closing marker of a styled word goes inside the style (the bold,
    /// italic… is extended); a space typed right before a closing marker goes after it (inside, "**word **" would
    /// stop being bold). Moves the caret before the text is inserted.
    /// </summary>
    public static void AdjustCaretForTyping(TextEditor editor, MarkdownView view, string typed)
    {
        if (typed.Length == 0 || editor.SelectionLength > 0) return;
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        int col = editor.CaretOffset - line.Offset;
        var render = view.RenderFor(line);
        string text = doc.GetText(line);
        bool space = char.IsWhiteSpace(typed[0]);
        // Typing at the start of a heading's text goes after the hidden "## " (before it, the heading would break).
        if (!typed.Contains('\n') && HeadingPrefix(render, text) is { } hp && col == hp.Start)
        {
            editor.CaretOffset = line.Offset + hp.End;
            return;
        }
        foreach (var s in render.Styles)
        {
            if ((s.Style & MergeableStyles) == 0 || (s.Style & (MdStyle.Code | MdStyle.Link)) != 0 || s.End <= s.Start) continue;
            var close = render.Hidden.FirstOrDefault(h => h.Start == s.End);
            if (close.Length == 0) continue;
            // Several markers can close at the same place ("***"): use the outermost end.
            int closeEnd = close.End;
            while (render.Hidden.FirstOrDefault(h => h.Start == closeEnd) is { Length: > 0 } next) closeEnd = next.End;
            if (!space && col == closeEnd && !char.IsWhiteSpace(text[s.End - 1]))
            {
                editor.CaretOffset = line.Offset + s.End;
                return;
            }
            if (space && col == s.End)
            {
                editor.CaretOffset = line.Offset + closeEnd;
                return;
            }
            // A space or Enter at the start of a styled word goes before its opening marker ("** bold**" is not bold).
            if (space && col == s.Start && render.Hidden.LastOrDefault(h => h.End == s.Start) is { Length: > 0 } open)
            {
                int openStart = open.Start;
                while (render.Hidden.LastOrDefault(h => h.End == openStart) is { Length: > 0 } prev) openStart = prev.Start;
                editor.CaretOffset = line.Offset + openStart;
                return;
            }
        }
    }

    /// <summary>
    /// Called after <see cref="AdjustCaretForTyping"/>: when the text typed at the caret would not show as typed
    /// there (a space before a closing "**", a letter between "**" and "**" of two neighbouring spans), the caret
    /// goes to another place in the same run of hidden markup where it does, and where no other character changes
    /// its style. Nothing changes when the caret is not next to hidden markup.
    /// </summary>
    public static void EnsureTypingShowsAsTyped(TextEditor editor, MarkdownView view, string typed)
    {
        if (typed.Length == 0 || typed.Contains('\n') || editor.SelectionLength > 0) return;
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        int col = editor.CaretOffset - line.Offset;
        string text = doc.GetText(line);
        var parse = ParserFor(view, doc, line.LineNumber);
        if (parse(text) is not { } whole) return;
        if (!whole.IsHidden(col) && !(col > 0 && whole.IsHidden(col - 1))) return;
        var (shown, styles) = InlineStyleEdit.Visible(text, whole);
        int v = 0;
        for (int i = 0; i < col; i++) if (!whole.IsHidden(i)) v++;
        string expected = shown.Insert(v, typed);
        bool Ok(int p)
        {
            if (parse(text[..p] + typed + text[p..]) is not { } r) return false;
            var (t, st) = InlineStyleEdit.Visible(text[..p] + typed + text[p..], r);
            if (t != expected) return false;
            for (int i = 0; i < st.Length; i++)
            {
                int j = i < v ? i : i >= v + typed.Length ? i - typed.Length : -1;
                if (j >= 0 && st[i] != styles[j]) return false;
            }
            return true;
        }
        if (Ok(col)) return;
        int rs = col, re = col;
        while (rs > 0 && whole.IsHidden(rs - 1)) rs--;
        while (re < text.Length && whole.IsHidden(re)) re++;
        for (int p = rs; p <= re; p++)
            if (p != col && Ok(p))
            {
                editor.CaretOffset = line.Offset + p;
                return;
            }
    }

    private const MdStyle SplittableStyles = MergeableStyles | MdStyle.Code;

    /// <summary>
    /// Enter inside styled text ("**bo|ld**"): the styles open at the caret are closed before the line break and
    /// opened again after it ("**bo**" / "**ld**"), so neither line is left with a lone marker. Changes the line so
    /// that the caret stands between the closing and the opening markers (the caller then inserts the line break
    /// there, with its usual indentation). Spaces at the caret are dropped ("**a **" would not be bold). False when
    /// the caret is not inside a style or the split would change what is shown.
    /// </summary>
    public static bool PrepareLineBreakInStyle(TextEditor editor, MarkdownView view)
    {
        if (editor.SelectionLength > 0 || editor.TextArea.Selection is RectangleSelection) return false;
        var doc = editor.Document;
        var line = doc.GetLineByOffset(editor.CaretOffset);
        if (view.Model.TableAtLine(line.LineNumber - 1) is not null) return false;
        int col = editor.CaretOffset - line.Offset;
        string text = doc.GetText(line);
        var render = view.RenderFor(line);
        // Both lines must show the same characters with the same styles as the two parts of the line did.
        var parse = InlineStyleEdit.LineParser;
        if (parse(text) is not { } whole) return false;
        // The inline styles only: a heading's text is bold as a whole, which the new line does not keep.
        static (string Text, MdStyle[] Styles) Inline(string t, LineRender r)
        {
            int headingText = HeadingPrefix(r, t) is { } h ? h.End : -1;
            var sb = new System.Text.StringBuilder();
            var st = new List<MdStyle>();
            for (int i = 0; i < t.Length; i++)
            {
                if (r.IsHidden(i)) continue;
                var style = MdStyle.None;
                foreach (var s in r.Styles)
                    if (i >= s.Start && i < s.End && s.Start != headingText) style |= s.Style;
                sb.Append(t[i]);
                st.Add(style);
            }
            return (sb.ToString(), st.ToArray());
        }
        var (shown, styles) = Inline(text, whole);
        int VisibleBefore(int index) { int n = 0; for (int i = 0; i < index; i++) if (!whole.IsHidden(i)) n++; return n; }
        bool Same(string candidate, int from, int to)
        {
            if (parse(candidate) is not { } r) return false;
            var (t, st) = Inline(candidate, r);
            if (t != shown[from..to]) return false;
            for (int i = 0; i < st.Length; i++) if ((st[i] & SplittableStyles) != (styles[from + i] & SplittableStyles)) return false;
            return true;
        }

        // At the start of a heading's text: the whole heading moves down.
        if (HeadingPrefix(whole, text) is { } hp && col >= hp.Start && col <= hp.End)
        {
            editor.CaretOffset = line.Offset + hp.Start;
            return true;
        }
        // Next to hidden markers ("*b*|**c**", "**word|**"): the break goes where, in that run of markers, both
        // lines show what they showed before (the caret's own place first).
        if (whole.IsHidden(col) || col > 0 && whole.IsHidden(col - 1))
        {
            int rs = col, re = col;
            while (rs > 0 && whole.IsHidden(rs - 1)) rs--;
            while (re < text.Length && whole.IsHidden(re)) re++;
            int v = VisibleBefore(col);
            foreach (int p in new[] { col }.Concat(Enumerable.Range(rs, re - rs + 1)))
                if (Same(text[..p], 0, v) && Same(text[p..], v, shown.Length))
                {
                    editor.CaretOffset = line.Offset + p;
                    return true;
                }
        }

        int headingStart = HeadingPrefix(whole, text) is { } hp2 ? hp2.End : -1;
        var spans = render.Styles.Where(s => s.Start < col && col < s.End && s.Start != headingStart && (s.Style & (SplittableStyles | MdStyle.Link)) != 0)
            .OrderBy(s => s.Start).ThenByDescending(s => s.End).ToList();
        if (spans.Count == 0 || spans.Any(s => s.Style.HasFlag(MdStyle.Link))) return false;
        int left = col, right = col;
        while (left > 0 && text[left - 1] == ' ') left--;
        while (right < text.Length && text[right] == ' ') right++;
        if (spans.Any(s => left <= s.Start || right >= s.End)) return false;
        var markers = new List<string>();
        foreach (var s in spans)
        {
            var open = render.Hidden.LastOrDefault(h => h.End == s.Start);
            if (open.Length == 0) return false;
            char c = text[open.Start];
            string? marker = s.Style.HasFlag(MdStyle.Code) ? text[open.Start..open.End]
                : (s.Style & MergeableStyles) switch
                {
                    MdStyle.Bold => new string(c, 2),
                    MdStyle.Italic => c.ToString(),
                    MdStyle.Bold | MdStyle.Italic => new string(c, 3),
                    MdStyle.Strike => "~~",
                    MdStyle.Highlight => "==",
                    _ => null,
                };
            if (marker is null) return false;
            markers.Add(marker);
        }
        string closing = string.Concat(Enumerable.Reverse(markers)), opening = string.Concat(markers);
        string first = text[..left] + closing, second = opening + text[right..];
        int a = VisibleBefore(left), b = VisibleBefore(right);
        if (!Same(first, 0, a) || !Same(second, b, shown.Length)) return false;
        doc.Replace(line.Offset + left, right - left, closing + opening);
        editor.CaretOffset = line.Offset + left + closing.Length;
        return true;
    }

    /// <summary>
    /// Joins neighbouring spans of the same style on a line ("**Prost****y**" → "**Prosty**"), when the line then
    /// shows the same text with the whole joined range in that style.
    /// </summary>
    public static void MergeAdjacentSpans(TextDocument doc, MarkdownView view, int lineNumber, Func<string, LineRender?>? parser = null)
    {
        var parse = parser ?? ParserFor(view, doc, lineNumber);
        for (int guard = 0; guard < 20; guard++)
        {
            var line = doc.GetLineByNumber(lineNumber);
            string text = doc.GetText(line);
            // The line parsed as it is now (not the document model, which is not reparsed inside an update).
            if (parse(text) is not { } render) return;
            string? merged = null;
            foreach (var s1 in render.Styles)
            {
                var kind = s1.Style & MergeableStyles;
                if (kind == 0 || (s1.Style & (MdStyle.Code | MdStyle.Link)) != 0) continue;
                var close1 = render.Hidden.FirstOrDefault(h => h.Start == s1.End);
                if (close1.Length == 0) continue;
                var open2 = render.Hidden.FirstOrDefault(h => h.Start == close1.End);
                if (open2.Length == 0 || text.Substring(close1.Start, close1.Length) != text.Substring(open2.Start, open2.Length)) continue;
                if (!render.Styles.Any(s2 => s2.Start == open2.End && (s2.Style & MergeableStyles) == kind)) continue;
                string candidate = text[..close1.Start] + text[open2.End..];
                if (parse(text) is not { } before || parse(candidate) is not { } after) continue;
                var (oldText, _) = InlineStyleEdit.Visible(text, before);
                var (newText, newStyles) = InlineStyleEdit.Visible(candidate, after);
                if (oldText != newText) continue;
                // The joined range keeps the style.
                int from = 0;
                for (int i = 0; i < s1.Start; i++) if (!before.IsHidden(i)) from++;
                var s2 = render.Styles.First(s => s.Start == open2.End);
                int count = 0;
                for (int i = s1.Start; i < s2.End; i++) if (!before.IsHidden(i)) count++;
                bool ok = true;
                for (int i = from; i < from + count && ok; i++) ok = (newStyles[i] & kind) == kind;
                if (!ok) continue;
                merged = candidate;
                break;
            }
            if (merged is null) return;
            ReplaceDifference(doc, line.Offset, text, merged);
        }
    }

    private static bool EmptiesSpan(LineRender render, int charCol) =>
        render.Styles.Any(s => s.Start == charCol && s.End == charCol + 1 &&
                               render.Hidden.Any(h => h.End == s.Start) && render.Hidden.Any(h => h.Start == s.End));

}

public enum TableCommand
{
    Split,
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
