using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

public partial class MainWindow
{
    // ---------------------------------------------------------------- line operations

    /// <summary>First and last (1-based) line touched by the selection, or the caret line.</summary>
    private (int First, int Last) SelectedLineRange()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength == 0)
        {
            int l = Editor.TextArea.Caret.Line;
            return (l, l);
        }
        var seg = Editor.TextArea.Selection.SurroundingSegment;
        int first = doc.GetLineByOffset(seg.Offset).LineNumber;
        var lastLine = doc.GetLineByOffset(seg.EndOffset);
        int last = lastLine.LineNumber;
        // A selection ending at column 1 does not include that line.
        if (last > first && seg.EndOffset == lastLine.Offset) last--;
        return (first, last);
    }

    private string NewLineAt(int lineNumber) => TextUtilities.GetNewLineFromDocument(Editor.Document, lineNumber);

    private void DuplicateLines()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength > 0 && !Editor.TextArea.Selection.IsMultiline)
        {
            int start = Editor.SelectionStart;
            string sel = Editor.SelectedText;
            doc.Insert(start + sel.Length, sel);
            return;
        }
        var (first, last) = SelectedLineRange();
        var fl = doc.GetLineByNumber(first);
        var ll = doc.GetLineByNumber(last);
        string block = doc.GetText(fl.Offset, ll.EndOffset - fl.Offset);
        doc.Insert(ll.EndOffset, NewLineAt(last) + block);
    }

    private void DeleteLines()
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLineRange();
        var fl = doc.GetLineByNumber(first);
        var ll = doc.GetLineByNumber(last);
        int start = fl.Offset, end = ll.EndOffset + ll.DelimiterLength;
        if (ll.DelimiterLength == 0 && fl.PreviousLine is { } prev) start = prev.EndOffset;
        doc.Remove(start, end - start);
    }

    private void MoveLines(int direction)
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLineRange();
        if (direction < 0 && first == 1 || direction > 0 && last == doc.LineCount) return;
        var area = Editor.TextArea;
        int caretLine = area.Caret.Line, caretCol = area.Caret.Column;
        bool hadSelection = Editor.SelectionLength > 0;

        var fl = doc.GetLineByNumber(first);
        var ll = doc.GetLineByNumber(last);
        string block = doc.GetText(fl.Offset, ll.Length + ll.EndOffset - fl.Offset - ll.Length);
        using (doc.RunUpdate())
        {
            if (direction < 0)
            {
                var above = doc.GetLineByNumber(first - 1);
                string aboveText = doc.GetText(above);
                string nlAbove = doc.GetText(above.EndOffset, above.DelimiterLength);
                // Replace "above\nblock" with "block\nabove" keeping each line's own terminator.
                doc.Replace(above.Offset, ll.EndOffset - above.Offset, block + nlAbove + aboveText);
            }
            else
            {
                var below = doc.GetLineByNumber(last + 1);
                string belowText = doc.GetText(below);
                string nlBlock = doc.GetText(ll.EndOffset, ll.DelimiterLength);
                doc.Replace(fl.Offset, below.EndOffset - fl.Offset, belowText + nlBlock + block);
            }
        }
        int newFirst = first + direction, newLast = last + direction;
        area.Caret.Position = new TextViewPosition(caretLine + direction, caretCol);
        if (hadSelection)
        {
            var nf = doc.GetLineByNumber(newFirst);
            var nl = doc.GetLineByNumber(newLast);
            Editor.Select(nf.Offset, nl.EndOffset - nf.Offset);
        }
    }

    private (string? Line, string? BlockStart, string? BlockEnd) CommentTokens()
    {
        string ext = Path.GetExtension(_active?.FilePath ?? "").ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".cxx" or ".java" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx"
                or ".go" or ".rs" or ".swift" or ".kt" or ".kts" or ".scala" or ".dart" or ".php" or ".fs" or ".groovy" or ".gradle"
                or ".jsonc" or ".proto" or ".zig" or ".v" or ".sv" => ("//", "/*", "*/"),
            ".css" or ".scss" or ".less" => (null, "/*", "*/"),
            ".py" or ".ps1" or ".psm1" or ".sh" or ".bash" or ".zsh" or ".yml" or ".yaml" or ".toml" or ".r" or ".pl" or ".rb"
                or ".cmake" or ".mk" or ".dockerfile" or ".conf" or ".properties" or ".gitignore" or ".tf" or ".nim" => ("#", null, null),
            ".sql" or ".lua" or ".hs" or ".elm" => ("--", null, null),
            ".ini" or ".asm" or ".s" or ".reg" => (";", null, null),
            ".bat" or ".cmd" => ("REM ", null, null),
            ".vb" or ".vbs" => ("'", null, null),
            ".tex" or ".erl" => ("%", null, null),
            ".html" or ".htm" or ".xml" or ".xaml" or ".axaml" or ".csproj" or ".props" or ".targets" or ".svg" or ".md" or ".vue" or ".config" => (null, "<!--", "-->"),
            _ => ("#", null, null),
        };
    }

    /// <summary>Toggles (null), adds (true) or removes (false) line comments on the selected lines.</summary>
    private void ToggleComment(bool? add)
    {
        var (lineToken, blockStart, blockEnd) = CommentTokens();
        var doc = Editor.Document;
        var (first, last) = SelectedLineRange();
        if (lineToken is null && blockStart is not null && blockEnd is not null)
        {
            ToggleBlockComment(first, last, blockStart, blockEnd, add);
            return;
        }
        if (lineToken is null) return;
        string token = lineToken.TrimEnd();

        var lines = Enumerable.Range(first, last - first + 1).Select(doc.GetLineByNumber).ToList();
        bool allCommented = lines.Where(l => doc.GetText(l).Trim().Length > 0)
            .All(l => doc.GetText(l).TrimStart().StartsWith(token, StringComparison.Ordinal));
        bool doAdd = add ?? !allCommented;
        int indent = lines.Where(l => doc.GetText(l).Trim().Length > 0)
            .Select(l => doc.GetText(l).TakeWhile(c => c is ' ' or '\t').Count()).DefaultIfEmpty(0).Min();

        using (doc.RunUpdate())
        {
            foreach (var l in lines)
            {
                string text = doc.GetText(l);
                if (text.Trim().Length == 0) continue;
                if (doAdd)
                {
                    doc.Insert(l.Offset + indent, token + " ");
                }
                else
                {
                    int i = text.TakeWhile(c => c is ' ' or '\t').Count();
                    if (!text.AsSpan(i).StartsWith(token, StringComparison.Ordinal)) continue;
                    int len = token.Length + (i + token.Length < text.Length && text[i + token.Length] == ' ' ? 1 : 0);
                    doc.Remove(l.Offset + i, len);
                }
            }
        }
    }

    private void ToggleBlockComment(int first, int last, string start, string end, bool? add)
    {
        var doc = Editor.Document;
        var fl = doc.GetLineByNumber(first);
        var ll = doc.GetLineByNumber(last);
        int s = Editor.SelectionLength > 0 ? Editor.SelectionStart : fl.Offset;
        int e = Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : ll.EndOffset;
        string text = doc.GetText(s, e - s);
        string trimmed = text.Trim();
        bool commented = trimmed.StartsWith(start, StringComparison.Ordinal) && trimmed.EndsWith(end, StringComparison.Ordinal);
        if (add ?? !commented)
        {
            using (doc.RunUpdate())
            {
                doc.Insert(e, " " + end);
                doc.Insert(s, start + " ");
            }
        }
        else if (commented)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            int b = text.LastIndexOf(end, StringComparison.Ordinal);
            string inner = text[(a + start.Length)..b];
            if (inner.StartsWith(' ')) inner = inner[1..];
            if (inner.EndsWith(' ')) inner = inner[..^1];
            doc.Replace(s, e - s, text[..a] + inner + text[(b + end.Length)..]);
        }
    }

    private void ChangeCase(bool upper)
    {
        var area = Editor.TextArea;
        var doc = Editor.Document;
        if (area.Selection.IsEmpty)
        {
            int c = Editor.CaretOffset;
            if (c >= doc.TextLength) return;
            string ch = doc.GetText(c, 1);
            doc.Replace(c, 1, upper ? ch.ToUpperInvariant() : ch.ToLowerInvariant());
            return;
        }
        using (doc.RunUpdate())
        {
            foreach (var seg in area.Selection.Segments.Reverse().ToList())
            {
                string t = doc.GetText(seg.StartOffset, seg.Length);
                doc.Replace(seg.StartOffset, seg.Length, upper ? t.ToUpper(System.Globalization.CultureInfo.CurrentCulture) : t.ToLower(System.Globalization.CultureInfo.CurrentCulture));
            }
        }
    }

    private void TrimTrailingWhitespace()
    {
        var doc = Editor.Document;
        using (doc.RunUpdate())
        {
            foreach (var line in doc.Lines.Reverse().ToList())
            {
                string t = doc.GetText(line);
                int n = t.Length;
                while (n > 0 && t[n - 1] is ' ' or '\t') n--;
                // Markdown hard line breaks (two trailing spaces) are significant.
                if (_active?.IsMarkdown == true && t.Length - n == 2 && t.EndsWith("  ")) continue;
                if (n < t.Length) doc.Remove(line.Offset + n, t.Length - n);
            }
        }
    }

    private async Task GoToLineAsync()
    {
        var input = await Dialogs.Input(this, Loc.T("Menu.Search.GotoLine"), Loc.T("GotoLine.Prompt", Editor.Document.LineCount), Editor.TextArea.Caret.Line.ToString());
        if (input is null) return;
        var parts = input.Split(':', ',');
        if (!int.TryParse(parts[0].Trim(), out int line)) return;
        int col = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int c) ? c : 1;
        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        Editor.TextArea.Caret.Position = new TextViewPosition(line, Math.Max(1, col));
        Editor.ScrollTo(line, col);
        Editor.Focus();
    }

    private void GoToMatchingBrace()
    {
        var doc = Editor.Document;
        int offset = Editor.CaretOffset;
        const string open = "([{<", close = ")]}>";
        for (int probe = offset; probe >= Math.Max(0, offset - 1); probe--)
        {
            if (probe >= doc.TextLength) continue;
            char c = doc.GetCharAt(probe);
            int oi = open.IndexOf(c), ci = close.IndexOf(c);
            if (oi < 0 && ci < 0) continue;
            bool forward = oi >= 0;
            char me = c, other = forward ? close[oi] : open[ci];
            int depth = 0;
            for (int i = probe; forward ? i < doc.TextLength : i >= 0; i += forward ? 1 : -1)
            {
                char x = doc.GetCharAt(i);
                if (x == me) depth++;
                else if (x == other && --depth == 0)
                {
                    Editor.CaretOffset = forward ? i + 1 : i;
                    Editor.TextArea.Caret.BringCaretToView();
                    return;
                }
            }
            return;
        }
    }

    // ---------------------------------------------------------------- bookmarks

    private readonly Dictionary<AvaloniaEdit.Document.TextDocument, TextSegmentCollection<TextSegment>> _bookmarks = new();
    private BookmarkRenderer? _bookmarkRenderer;

    private TextSegmentCollection<TextSegment> Bookmarks
    {
        get
        {
            if (!_bookmarks.TryGetValue(Editor.Document, out var set))
            {
                set = new TextSegmentCollection<TextSegment>(Editor.Document);
                _bookmarks[Editor.Document] = set;
            }
            if (_bookmarkRenderer is null)
            {
                _bookmarkRenderer = new BookmarkRenderer(this);
                Editor.TextArea.TextView.BackgroundRenderers.Add(_bookmarkRenderer);
            }
            return set;
        }
    }

    internal TextSegmentCollection<TextSegment>? CurrentBookmarks => _bookmarks.GetValueOrDefault(Editor.Document);

    private void ToggleBookmark()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        var set = Bookmarks;
        var existing = set.FindSegmentsContaining(line.Offset).FirstOrDefault(s => Editor.Document.GetLineByOffset(s.StartOffset).LineNumber == line.LineNumber);
        if (existing is not null) set.Remove(existing);
        else set.Add(new TextSegment { StartOffset = line.Offset, Length = 0 });
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void GoToBookmark(int direction)
    {
        var set = Bookmarks;
        if (set.Count == 0) return;
        int caretLine = Editor.TextArea.Caret.Line;
        var lines = set.Select(s => Editor.Document.GetLineByOffset(s.StartOffset).LineNumber).Distinct().OrderBy(l => l).ToList();
        int target = direction > 0
            ? lines.FirstOrDefault(l => l > caretLine, lines[0])
            : lines.LastOrDefault(l => l < caretLine, lines[^1]);
        Editor.TextArea.Caret.Position = new TextViewPosition(target, 1);
        Editor.ScrollTo(target, 1);
    }

    private void ClearBookmarks()
    {
        Bookmarks.Clear();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private sealed class BookmarkRenderer(MainWindow window) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var set = window.CurrentBookmarks;
            if (set is null || set.Count == 0 || !textView.VisualLinesValid) return;
            var brush = window.Palette.Accent;
            foreach (var vl in textView.VisualLines)
            {
                var dl = vl.FirstDocumentLine;
                if (!set.FindSegmentsContaining(dl.Offset).Any() && !set.FindOverlappingSegments(dl.Offset, dl.Length).Any()) continue;
                double y = vl.VisualTop - textView.VerticalOffset;
                drawingContext.FillRectangle(brush, new Rect(0, y, 3, vl.Height));
            }
        }
    }

    // ---------------------------------------------------------------- view options

    private void ToggleWordWrap()
    {
        Editor.WordWrap = !Editor.WordWrap;
        _settings.WordWrap = Editor.WordWrap;
    }

    private void SetWhitespace(bool spaces, bool eol)
    {
        Editor.Options.ShowSpaces = spaces;
        Editor.Options.ShowTabs = spaces;
        Editor.Options.ShowEndOfLine = eol;
        _settings.ShowSpacesAndTabs = spaces;
        _settings.ShowEndOfLine = eol;
        RefreshCommandStates();
    }

    private void ToggleAllCharacters()
    {
        bool all = Editor.Options.ShowSpaces && Editor.Options.ShowEndOfLine;
        SetWhitespace(!all, !all);
    }

    private void ToggleLineNumbers()
    {
        Editor.ShowLineNumbers = !Editor.ShowLineNumbers;
        _settings.ShowLineNumbers = Editor.ShowLineNumbers;
    }

    private void ToggleCurrentLine()
    {
        Editor.Options.HighlightCurrentLine = !Editor.Options.HighlightCurrentLine;
        _settings.HighlightCurrentLine = Editor.Options.HighlightCurrentLine;
    }

    private double _sidebarWidthBeforeHide = 260;

    private void ToggleSidebar()
    {
        var col = MainGrid.ColumnDefinitions[0];
        if (col.Width.Value > 0)
        {
            _sidebarWidthBeforeHide = col.ActualWidth;
            col.Width = new GridLength(0);
        }
        else col.Width = new GridLength(_sidebarWidthBeforeHide);
    }

    private void Zoom(int step)
    {
        Editor.FontSize = step == 0 ? 13 : Math.Clamp(Editor.FontSize + step, 6, 48);
        _settings.FontSize = Editor.FontSize;
        _diffView?.SetFontSize(Editor.FontSize);
        _diffLinePanel?.SetFont(Editor.FontFamily, Editor.FontSize);
        SetStatusMessage($"{Editor.FontSize:0} pt");
    }

    private void CycleVisual(int direction)
    {
        var order = _files.VisualOrder().ToList();
        if (order.Count == 0) return;
        int i = _active is null ? -1 : order.IndexOf(_active);
        Activate(order[((i + direction) % order.Count + order.Count) % order.Count]);
    }

    private void SetTheme(AppTheme theme)
    {
        _settings.Theme = theme;
        App.ApplyTheme(theme);
        RefreshCommandStates();
    }

    private async Task CopyToClipboardAsync(string? text)
    {
        if (text is null || Clipboard is null) return;
        await Clipboard.SetTextAsync(text);
        SetStatusMessage(Loc.T("Status.Copied"));
    }

    private static void RevealInFileManager(string? path, bool isDirectory = false)
    {
        if (path is null) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", isDirectory ? new[] { path } : ["-R", path]);
            else
                Process.Start("xdg-open", isDirectory ? path : Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---------------------------------------------------------------- markdown

    private void ClearFormatting()
    {
        if (_markdown is null || Editor.IsReadOnly) return;
        var (cleared, skipped) = MarkdownCommands.ClearFormatting(Editor, _markdown);
        SetStatusMessage(skipped > 0 ? Loc.T("Md.ClearedSkipped", cleared, skipped) : Loc.T("Md.Cleared", cleared));
    }

    private void ConvertStyle(MdStyle from, MdStyle to)
    {
        if (_markdown is null || Editor.IsReadOnly) return;
        var (converted, skipped) = MarkdownCommands.ConvertStyle(Editor, _markdown, from, to);
        SetStatusMessage(skipped > 0 ? Loc.T("Convert.DoneSkipped", converted, skipped) : Loc.T("Convert.Done", converted));
    }

    private void MdStyleToggle(string marker, MdStyle style)
    {
        if (_markdown is null) return;
        int failed = MarkdownCommands.ToggleStyle(Editor, _markdown, marker, style);
        if (failed > 0) SetStatusMessage(Loc.T("Md.StyleSkipped", failed));
    }

    private void ToggleMarkdownRaw()
    {
        _settings.MarkdownRaw = !_settings.MarkdownRaw;
        if (_active is { IsMarkdown: true, IsLarge: false })
        {
            _markdown?.Dispose();
            _markdown = _settings.MarkdownRaw ? null : CreateMarkdownView(_active);
            if (_cyrillicView is not null) ApplyCyrillicView(_active);
        }
        RefreshCommandStates();
        UpdateStatus();
    }

    private async Task InsertTableAsync()
    {
        var input = await Dialogs.Input(this, Loc.T("Menu.Md.Table.Insert"), Loc.T("Table.SizePrompt"), "3x2");
        if (input is null) return;
        var parts = input.ToLowerInvariant().Split('x', '×', '*', ' ');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int cols) || !int.TryParse(parts[^1], out int rows)) return;
        MarkdownCommands.InsertTable(Editor, Math.Clamp(cols, 1, 50), Math.Clamp(rows, 0, 500), Loc.T("Table.ColumnHeader"));
        Editor.Focus();
    }

    private TableSelectionRenderer? _tableSelection;

    /// <summary>Highlights a table column (or row). Copy then copies it as tab separated values.</summary>
    private void SelectTablePart(bool column)
    {
        if (_markdown is null || MarkdownCommands.TableAtCaret(Editor, _markdown) is not var (table, row, col)) return;
        var ranges = column ? TableEditor.ColumnRanges(table, col) : TableEditor.RowRanges(table, row);
        if (_tableSelection is null)
        {
            _tableSelection = new TableSelectionRenderer(this);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_tableSelection);
            Editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                if (_tableSelection.Ranges.Count > 0 && !_tableSelection.Sticky) { _tableSelection.Ranges = []; Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background); }
                _tableSelection.Sticky = false;
            };
        }
        _tableSelection.Ranges = ranges.ToList();
        _tableSelection.Column = column ? col : -1;
        _tableSelection.Sticky = true;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        SetStatusMessage(Loc.T(column ? "Table.ColumnSelected" : "Table.RowSelected"));
    }

    private async Task CopyTableColumnAsync()
    {
        if (_markdown is null || MarkdownCommands.TableAtCaret(Editor, _markdown) is not var (table, _, col)) return;
        string nl = NewLineAt(Editor.TextArea.Caret.Line);
        await CopyToClipboardAsync(TableEditor.ColumnAsTsv(table, _markdown.Source, col, nl));
    }

    private sealed class TableSelectionRenderer(MainWindow window) : IBackgroundRenderer
    {
        public List<CellRange> Ranges { get; set; } = [];
        public int Column { get; set; } = -1;
        public bool Sticky { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Ranges.Count == 0) return;
            var doc = textView.Document;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            foreach (var r in Ranges)
            {
                if (r.Line + 1 > doc.LineCount) continue;
                var line = doc.GetLineByNumber(r.Line + 1);
                int s = Math.Min(line.Offset + r.Start, line.EndOffset), e = Math.Min(line.Offset + r.End, line.EndOffset);
                if (e > s) builder.AddSegment(textView, new TextSegment { StartOffset = s, EndOffset = e });
                builder.CloseFigure();
            }
            if (builder.CreateGeometry() is { } g) drawingContext.DrawGeometry(window.Palette.TableSelection, null, g);
        }
    }

    // ---------------------------------------------------------------- dialogs

    private ShortcutsPopup? _shortcuts;

    private void ShowShortcuts()
    {
        if (_shortcuts is null)
        {
            _shortcuts = new ShortcutsPopup(MainGrid, _commands.Values);
            EditorHost.Children.Add(_shortcuts.Popup);
        }
        _shortcuts.Show();
    }

    private async Task ShowSettingsAsync()
    {
        var dlg = new SettingsWindow(_settings);
        await dlg.ShowDialog(this);
        if (!dlg.Saved) return;
        Editor.FontFamily = new FontFamily(_settings.FontFamily);
        Editor.FontSize = _settings.FontSize;
        SetIndentation(_settings.UseSpaces, _settings.TabSize);
        App.ApplyTheme(_settings.Theme);
        SettingsStore.Save(_settings);
        RefreshCommandStates();
        if (dlg.LanguageChanged)
        {
            // Show the notice in the new language, which is what the user will see after the restart.
            string current = Loc.Language;
            Loc.Load(_settings.Language);
            string notice = Loc.T("Settings.LanguageRestart");
            Loc.Load(current);
            await Dialogs.Info(this, AppInfo.Name, notice);
        }
    }
}
