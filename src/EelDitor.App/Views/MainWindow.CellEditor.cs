using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

/// <summary>
/// Editing one table cell in a small Markdown editor over it: used when tables are drawn as grids (word wrap on),
/// where the caret cannot go inside a cell, and available with F2 anywhere in a table.
/// <list type="bullet">
/// <item>The cell's Markdown is shown like in the main editor: markers hidden, bold/italic/code/links styled, and
/// Ctrl+B, Ctrl+I, Ctrl+Shift+X, Ctrl+` toggle styles.</item>
/// <item>Saving goes through <see cref="TableCellEdit.ApplySource"/>: | is escaped, backticks that would swallow
/// the next cells are escaped, and the row must stay the same table row with the other cells unchanged —
/// otherwise nothing is written and the editor says why. Only the changed part of the line is replaced.</item>
/// <item>Enter (and Shift+Enter) starts a new line in the cell (a <c>&lt;br&gt;</c>); Ctrl+Enter, Esc, clicking
/// elsewhere or scrolling end the edit and keep it; Tab / Shift+Tab and the arrows at the edge of the text go on to the
/// next / previous / neighbouring cell.</item>
/// <item>In a grid it looks like editing in place: no frame, the cell's background, exactly over the cell's text and
/// as wide; every change is written into the row at once (the row and its neighbours follow), the whole edit is one
/// undo step (Ctrl+Z puts the row back as it was). Problems are reported on the status bar.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    private Popup? _cellPopup;
    private TextEditor? _cellEditor;
    private MarkdownView? _cellView;

    internal TextEditor? CellEditor => _cellPopup is { IsOpen: true } ? _cellEditor : null;

    internal MarkdownView? CellViewForTests => CellEditor is null ? null : _cellView;

    internal string? CellEditorError { get; private set; }

    /// <summary>Ends a cell edit (keeping it, as a click elsewhere does).</summary>
    private void CloseCellEditor()
    {
        if (_cellPopup is { IsOpen: true } popup) popup.IsOpen = false;
    }

    /// <summary>The keyboard goes back to the text: the cell being edited, or the editor.</summary>
    private void FocusEditor()
    {
        if (CellEditor is { } cell) cell.TextArea.Focus();
        else Editor.Focus();
    }

    /// <summary>
    /// A click outside the cell editor ends the edit (keeping it), like in a spreadsheet; the click still does what it
    /// does there. The menu and the toolbar are exceptions: their style commands apply to the cell's text, and every
    /// other command ends the edit itself. (Not the popup's light dismiss: it swallowed the click, so a toolbar button
    /// clicked during an edit only closed the editor, and styles went to the whole cell.)
    /// </summary>
    private void SetupCellEditorDismiss()
    {
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_cellPopup is not { IsOpen: true } popup || e.Source is not Avalonia.Visual source) return;
            // A click in a popup (the cell editor itself, an open menu's items, a context menu) reaches this window's
            // handlers too, but it is not a click elsewhere in the window: the menu items are in a popup of their own,
            // not under the menu bar, so they closed the editor and a style from the menu went to the whole cell.
            if (TopLevel.GetTopLevel(source) is { } top && !ReferenceEquals(top, this)) return;
            if (popup.Child is { } child && (ReferenceEquals(child, source) || child.IsVisualAncestorOf(source))) return;
            if (Toolbar.IsVisualAncestorOf(source) || MainMenu.IsVisualAncestorOf(source)) return;
            popup.IsOpen = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // The popup is a window of its own, above the others: it must not stay when the user goes to another program.
        Deactivated += (_, _) => Dispatcher.UIThread.Post(() => { if (!IsActive) CloseCellEditor(); });
    }

    /// <summary>F2: the cell at the caret (the first cell of the row when the table is drawn as a grid).</summary>
    private void EditCellAtCaret()
    {
        if (_markdown is null || MarkdownCommands.TableAtCaret(Editor, _markdown) is not { } at) return;
        int row = at.Row;
        if (at.Table.Rows[row].Kind == MdLineKind.TableSeparator) row++;
        if (row >= at.Table.Rows.Count) return;
        EditTableCell(at.Table.FirstLine + row + 1, _markdown.TablesAsGrid ? 0 : Math.Max(0, at.Column));
    }

    /// <param name="shownOffset">Where the caret goes in the cell's shown text (a double click); null: all selected.</param>
    internal void EditTableCell(int lineNumber, int cellIndex, int? shownOffset = null)
    {
        if (_cellPopup is { IsOpen: true }) _cellPopup.IsOpen = false; // saves the open one
        if (_markdown is not { } view || Editor.IsReadOnly || lineNumber < 1 || lineNumber > Editor.Document.LineCount) return;
        var row = view.Model.TableAtLine(lineNumber - 1)?.RowAtLine(lineNumber - 1);
        if (row is null || row.Kind == MdLineKind.TableSeparator || row.Cells.Count == 0) return;
        cellIndex = Math.Clamp(cellIndex, 0, row.Cells.Count - 1);
        // The edited cell is the current one (also when Tab moves the editor to the next cell).
        {
            var l = Editor.Document.GetLineByNumber(lineNumber);
            Editor.TextArea.ClearSelection();
            Editor.CaretOffset = l.Offset + row.Cells[cellIndex].ContentStart(Editor.Document.GetText(l));
        }

        Editor.ScrollTo(lineNumber, 1);
        Editor.TextArea.TextView.EnsureVisualLines();
        if (view.CellRect(lineNumber, cellIndex) is not { } rect) return;
        var line = Editor.Document.GetLineByNumber(lineNumber);
        string lineText = Editor.Document.GetText(line);
        var cell = row.Cells[cellIndex];
        // <br> tags are line breaks in the editor (Enter); saving turns the line breaks back into <br>. Styles running
        // over a <br> are closed and opened again around the line break (their markers would show), and joined on saving.
        var (original, splits) = TableCellEdit.CellEditorText(lineText, cell, view.RenderFor(line));

        // In a grid the editor lies exactly over the cell's text and wraps at the cell's width, so the text keeps its
        // layout while it is edited; it is as tall as the text (plus a line to type into), so no scroll bar is needed.
        var table = view.Model.TableAtLine(lineNumber - 1);
        int? gridColumns = view.TablesAsGrid && table is not null && view.GridLayout(table) is { } layout && cellIndex < layout.Widths.Length
            ? layout.Widths[cellIndex] : null;
        var (width, height) = gridColumns is int columns ? GridCellEditorSize(original, columns) : CellEditorSize(original, rect.Width);
        bool inPlace = gridColumns is not null;
        // The editor's padding (4, 2) puts the text that far from the popup's corner (measured on a rendered frame);
        // the text then lies exactly on the cell's text, inside the current cell's outline.
        Rect Place(Rect cellRect) =>
            new(cellRect.X + 2 * Editor.TextArea.TextView.WideSpaceWidth - 4, cellRect.Y - 2, cellRect.Width, cellRect.Height);
        if (inPlace) rect = Place(rect);
        var editor = new TextEditor
        {
            Document = new TextDocument(original),
            FontFamily = Editor.FontFamily,
            FontSize = Editor.FontSize,
            WordWrap = true,
            ShowLineNumbers = false,
            Width = width,
            Height = height,
            Padding = new Thickness(4, 2),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = gridColumns is null ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = Palette.Background,
            Foreground = Palette.Foreground,
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        var splitAnchors = splits.Select(sp => (Anchor: editor.Document.CreateAnchor(sp.Newline), Split: sp)).ToList();
        // The editor's text as it goes into the row: the styles split at line breaks joined again.
        string ForRow(string text) => splitAnchors.Count == 0 ? text
            : TableCellEdit.JoinSplitStyles(text, splitAnchors.Where(a => !a.Anchor.IsDeleted).Select(a => a.Split with { Newline = a.Anchor.Offset }));
        editor.TextArea.SelectionBrush = Palette.Selection;
        var cellView = new MarkdownView(editor, editor.Document, Palette, _active?.DirectoryPath) { ShowImages = false };
        // Ctrl+click on a link (and its tooltip) works in the cell being edited too.
        var fromDocument = _active;
        cellView.LinkActivated += url => { if (fromDocument is not null) _ = OpenLinkAsync(url, fromDocument); };

        // Problems (a change that would break the row) go to the status bar; in text mode also under the editor.
        var error = new TextBlock { FontSize = 12, Margin = new Thickness(4, 3, 4, 1), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = Math.Max(width, 300), IsVisible = false }
            .Res(TextBlock.ForegroundProperty, "GitDeletedBrush");
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(editor);
        if (!inPlace) panel.Children.Add(error);

        var popup = new Popup
        {
            PlacementTarget = Editor.TextArea.TextView,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementRect = rect,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = false, // see SetupCellEditorDismiss
            Child = inPlace
                ? new Border { Child = panel, Background = Palette.Background }
                : new Border { Child = panel, Padding = new Thickness(3), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) }
                    .Res(Border.BackgroundProperty, "PanelBrush").Res(Border.BorderBrushProperty, "AccentTextBrush"),
        };
        CellEditorError = null;
        var mainDoc = Editor.Document;
        string originalLine = lineText;
        string committed = original;
        bool liveChanged = false;
        long quietScrollUntil = 0;
        // In place: the whole edit is one undo step (each change is written into the row as it is typed).
        if (inPlace) mainDoc.UndoStack.StartUndoGroup();

        bool saved = false, cancelled = false;
        // Saves; false when the text cannot be written without breaking the table (the editor stays open and says why).
        bool Save()
        {
            if (saved || cancelled) return true;
            if (!Write(editor.Document.Text)) return false;
            saved = true;
            return true;
        }

        // Writes the cell's text into the row; the text as it was puts the row back exactly (keeps <br/> as written).
        bool Write(string text)
        {
            if (text == committed) return true;
            if (text == original)
            {
                // Only the part that differs (replacing the whole line would move the caret to its end: the last cell).
                var l = mainDoc.GetLineByNumber(lineNumber);
                string now = mainDoc.GetText(l);
                if (now != originalLine) ReplaceChangedPart(mainDoc, l.Offset, now, originalLine);
                committed = text;
                KeepCaretInCell();
                return true;
            }
            var problem = CommitCell(lineNumber, cellIndex, ForRow(text));
            if (problem is null)
            {
                committed = text;
                liveChanged = true;
                CellEditorError = null;
                error.IsVisible = false;
                KeepCaretInCell();
                return true;
            }
            CellEditorError = Loc.T(problem);
            error.Text = CellEditorError;
            error.IsVisible = true;
            SetStatusMessage(CellEditorError);
            return false;
        }

        // The edited cell stays the current one whatever the writes did to the main caret.
        void KeepCaretInCell()
        {
            if (view.Model.TableAtLine(lineNumber - 1)?.RowAtLine(lineNumber - 1) is not { } r || cellIndex >= r.Cells.Count) return;
            var l = mainDoc.GetLineByNumber(lineNumber);
            int column = Editor.CaretOffset - l.Offset;
            var c = r.Cells[cellIndex];
            if (Editor.CaretOffset >= l.Offset && column >= c.Start && column <= c.End) return;
            Editor.CaretOffset = l.Offset + c.ContentStart(mainDoc.GetText(l));
        }

        // In place: the row follows the typing; the editor follows the cell (its column can get wider).
        void Live()
        {
            if (!popup.IsOpen || saved || cancelled) return;
            quietScrollUntil = Environment.TickCount64 + 500;
            if (!Write(editor.Document.Text)) return;
            Editor.TextArea.TextView.EnsureVisualLines();
            if (view.CellRect(lineNumber, cellIndex) is { } now) popup.PlacementRect = Place(now);
            if (view.Model.TableAtLine(lineNumber - 1) is { } t && view.GridLayout(t) is { } grid && cellIndex < grid.Widths.Length)
                editor.Width = GridCellEditorSize(editor.Document.Text, grid.Widths[cellIndex]).Width;
        }
        if (inPlace) editor.TextChanged += (_, _) => Dispatcher.UIThread.Post(Live, DispatcherPriority.Background);

        EventHandler scrolled = (_, _) => { if (popup.IsOpen && Environment.TickCount64 > quietScrollUntil) popup.IsOpen = false; };
        Editor.TextArea.TextView.ScrollOffsetChanged += scrolled;
        popup.Closed += (_, _) =>
        {
            Editor.TextArea.TextView.ScrollOffsetChanged -= scrolled;
            // Clicking elsewhere saves, like in a spreadsheet; if that is impossible, the edit is dropped and reported.
            if (!saved && !cancelled && !Save()) SetStatusMessage(Loc.T("Table.CellNotSaved", CellEditorError ?? ""));
            if (inPlace)
            {
                mainDoc.UndoStack.EndUndoGroup();
                // Esc: the row as it was before the edit (undoing the live changes, as one step).
                if (cancelled && liveChanged && mainDoc.UndoStack.CanUndo) mainDoc.UndoStack.Undo();
            }
            cellView.Dispose();
            if (ReferenceEquals(_cellPopup, popup)) { _cellPopup = null; _cellEditor = null; _cellView = null; }
        };
        editor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            // Ctrl+Alt is AltGr: Ctrl+Alt(+Shift)+X types ź/Ź in Polish, it is not a shortcut.
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool plain = (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
            switch (e.Key)
            {
                // Esc and Ctrl+Enter end the edit and keep it (Ctrl+Z takes it back in one step); a change that cannot be
                // written is dropped and reported.
                case Key.Escape:
                case Key.Enter when ctrl:
                    e.Handled = true;
                    if (!Save()) cancelled = true;
                    popup.IsOpen = false;
                    if (cancelled) SetStatusMessage(Loc.T("Table.CellNotSaved", CellEditorError ?? ""));
                    Editor.Focus();
                    break;
                case Key.Enter:
                    break; // a new line: a <br> in the cell (Shift+Enter too)
                case Key.Left or Key.Right or Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.None && AtCellEdge(editor, cellView, e.Key):
                    // At the edge of the text the arrow goes on to the neighbouring cell, still editing.
                    if (NeighbourCell(lineNumber, cellIndex, e.Key) is not { } next) break;
                    e.Handled = true;
                    if (!Save()) return;
                    popup.IsOpen = false;
                    int atOffset = e.Key is Key.Left or Key.Up ? int.MaxValue : 0;
                    Dispatcher.UIThread.Post(() => EditTableCell(next.Line, next.Cell, atOffset), DispatcherPriority.Background);
                    break;
                case Key.Tab:
                    e.Handled = true;
                    if (!Save()) return;
                    popup.IsOpen = false;
                    MoveToCell(lineNumber, cellIndex, shift);
                    break;
                // The Markdown styles, as in the main editor.
                case Key.B when ctrl && !shift: MarkdownCommands.ToggleStyle(editor, cellView, "**", MdStyle.Bold); e.Handled = true; break;
                case Key.I when ctrl && !shift: MarkdownCommands.ToggleStyle(editor, cellView, "*", MdStyle.Italic); e.Handled = true; break;
                case Key.X when ctrl && shift: MarkdownCommands.ToggleStyle(editor, cellView, "~~", MdStyle.Strike); e.Handled = true; break;
                case Key.OemTilde when ctrl: MarkdownCommands.ToggleStyle(editor, cellView, "`", MdStyle.Code); e.Handled = true; break;
                case Key.Left when plain:
                case Key.Right when plain:
                    if (MarkdownCommands.HandleHorizontalMove(editor, cellView, e.Key == Key.Right, shift)) e.Handled = true;
                    break;
                case Key.Back when e.KeyModifiers == KeyModifiers.None:
                case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                    if (MarkdownCommands.HandleDelete(editor, cellView, e.Key == Key.Back)) e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);

        if (gridColumns is not null)
        {
            // The real height once laid out (hidden markup, wide characters): the whole text plus a line, growing while
            // typing; a scroll bar only past most of the window.
            double maxHeight = Math.Max(4 * Editor.TextArea.TextView.DefaultLineHeight, Editor.TextArea.TextView.Bounds.Height * 0.75);
            void Fit()
            {
                var tv = editor.TextArea.TextView;
                tv.EnsureVisualLines();
                // Just the text: the row grows with it (it is written through), so no spare line is needed.
                double wanted = tv.DocumentHeight + editor.Padding.Top + editor.Padding.Bottom + 2;
                editor.Height = Math.Min(maxHeight, wanted);
                editor.VerticalScrollBarVisibility = wanted > maxHeight
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            }
            editor.TextChanged += (_, _) => Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
            popup.Opened += (_, _) => Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        }

        _cellPopup = popup;
        _cellEditor = editor;
        _cellView = cellView;
        ((ISetLogicalParent)popup).SetParent(this);
        popup.IsOpen = true;
        editor.Focus();
        editor.TextArea.Focus();
        if (shownOffset is int shown) editor.CaretOffset = EditorOffsetOfShown(editor.Document, cellView, shown);
        else editor.SelectAll();
    }

    /// <summary>The offset in the cell editor's text of the <paramref name="shown"/>-th shown character (markup hidden).</summary>
    private static int EditorOffsetOfShown(TextDocument doc, MarkdownView view, int shown)
    {
        int count = 0;
        foreach (var line in doc.Lines)
        {
            var render = view.RenderFor(line);
            for (int col = 0; col < line.Length; col++)
            {
                if (render.IsHidden(col)) continue;
                if (count == shown) return line.Offset + col;
                count++;
            }
            if (count == shown) return line.EndOffset;
            count++; // the line break
        }
        return doc.TextLength;
    }

    /// <summary>
    /// The cell editor's size: at least the cell, wider for long text, and tall enough for the whole text plus room
    /// to type, so that a scroll bar is needed only for very long cells (more than most of the window).
    /// </summary>
    /// <summary>The cell editor over a grid cell: exactly the cell's text columns wide, as tall as the text.</summary>
    internal (double Width, double Height) GridCellEditorSize(string text, int columns)
    {
        var tv = Editor.TextArea.TextView;
        double charWidth = Math.Max(1, tv.WideSpaceWidth), lineHeight = tv.DefaultLineHeight;
        // Room for exactly `columns` characters (half a character more, so the last one does not wrap) and the padding.
        // Spaces just typed after the text are not in the column yet (the cell's width ignores them): room for them
        // too, or each space would wrap to the next line until the next character is typed.
        int shown = columns;
        foreach (var para in text.Split('\n'))
            if (para.TrimEnd(' ').Length <= columns) shown = Math.Max(shown, para.Length);
        double width = (shown + 0.5) * charWidth + 4 * 2;
        double height = WrappedLineCount(text, Math.Max(1, columns)) * lineHeight + 2 * 2 + 2;
        return (width, height);
    }

    internal (double Width, double Height) CellEditorSize(string text, double cellWidth)
    {
        var tv = Editor.TextArea.TextView;
        double charWidth = Math.Max(1, tv.WideSpaceWidth), lineHeight = tv.DefaultLineHeight;
        double viewWidth = tv.Bounds.Width > 0 ? tv.Bounds.Width : 1000, viewHeight = tv.Bounds.Height > 0 ? tv.Bounds.Height : 700;
        const double chrome = 4 * 2 + 20; // padding and a possible scroll bar
        double maxWidth = Math.Max(320, Math.Min(viewWidth - 24, 110 * charWidth + chrome));
        int Lines(double w) => WrappedLineCount(text, Math.Max(10, (int)((w - chrome) / charWidth)));
        double width = Math.Clamp(cellWidth, Math.Min(320, maxWidth), maxWidth);
        // Long text: rather wider than very tall.
        if (Lines(width) > 6) width = maxWidth;
        double maxHeight = Math.Max(4 * lineHeight, viewHeight * 0.75);
        double height = Math.Min(maxHeight, (Lines(width) + 2) * lineHeight + 6);
        return (width, height);
    }

    /// <summary>How many lines <paramref name="text"/> takes when word-wrapped at <paramref name="columns"/>.</summary>
    internal static int WrappedLineCount(string text, int columns)
    {
        int count = 0;
        foreach (var para in text.Split('\n'))
        {
            int col = 0;
            count++;
            foreach (var word in para.Split(' '))
            {
                int len = word.Length;
                if (col > 0 && col + 1 + len > columns) { count++; col = 0; }
                else if (col > 0) col++;
                while (len > columns) { count++; len -= columns; }
                col += len;
            }
        }
        return Math.Max(1, count);
    }

    /// <summary>
    /// On a grid row, like in a spreadsheet: Left / Right go to the neighbouring cell (stopping at the row's edge),
    /// Up / Down to the same cell of the row above / below (past the table's edge: the line before / after it), and
    /// Enter edits the current cell. False when the caret is not on a grid row.
    /// </summary>
    private bool HandleGridKey(Key key)
    {
        if (_markdown is not { TablesAsGrid: true } view || view.CaretGridRow is not { } line || view.CurrentCell is not { } current
            || view.Model.TableAtLine(line.LineNumber - 1) is not { } table) return false;
        if (Editor.SelectionLength > 0) Editor.TextArea.ClearSelection();
        int row = line.LineNumber - 1 - table.FirstLine;
        if (row < 0 || row >= table.Rows.Count) return false;
        var doc = Editor.Document;
        void GoTo(int r, int cell)
        {
            var l = doc.GetLineByNumber(table.FirstLine + r + 1);
            var cells = table.Rows[r].Cells;
            cell = Math.Clamp(cell, 0, cells.Count - 1);
            Editor.CaretOffset = l.Offset + cells[cell].ContentStart(doc.GetText(l));
            Editor.TextArea.Caret.BringCaretToView();
        }
        switch (key)
        {
            case Key.Enter:
                if (Editor.IsReadOnly) return false;
                EditTableCell(current.Line, current.Cell);
                return true;
            case Key.Left when current.Cell > 0:
                GoTo(row, current.Cell - 1);
                return true;
            case Key.Right when current.Cell < table.Rows[row].Cells.Count - 1:
                GoTo(row, current.Cell + 1);
                return true;
            default:
                // Left / Right at the row's edge: the last / first cell of the previous / next row.
                bool horizontal = key is Key.Left or Key.Right;
                int step = key is Key.Up or Key.Left ? -1 : 1, target = row + step;
                while (target >= 0 && target < table.Rows.Count && table.Rows[target].Kind == MdLineKind.TableSeparator) target += step;
                if (target >= 0 && target < table.Rows.Count && table.Rows[target].Cells.Count > 0)
                {
                    GoTo(target, horizontal ? (step < 0 ? int.MaxValue : 0) : current.Cell);
                    return true;
                }
                // Out of the table: the start of the line before / after it, if there is one.
                int outside = step < 0 ? table.FirstLine : table.FirstLine + table.Rows.Count + 1;
                if (outside >= 1 && outside <= doc.LineCount)
                {
                    Editor.CaretOffset = doc.GetLineByNumber(outside).Offset;
                    Editor.TextArea.Caret.BringCaretToView();
                }
                return true;
        }
    }

    /// <summary>
    /// Whether the cell editor's caret is at the edge of its text in the arrow's direction: before the first shown
    /// character (Left), after the last one (Right), on the first drawn line (Up) or on the last one (Down).
    /// Hidden markup at the ends does not count.
    /// </summary>
    internal static bool AtCellEdge(TextEditor editor, MarkdownView view, Key key)
    {
        if (editor.SelectionLength > 0) return false;
        var doc = editor.Document;
        int caret = editor.CaretOffset;
        bool HiddenBetween(int from, int to)
        {
            for (int o = from; o < to; o++)
            {
                var line = doc.GetLineByOffset(o);
                if (o == line.EndOffset || !view.RenderFor(line).IsHidden(o - line.Offset)) return false;
            }
            return true;
        }
        switch (key)
        {
            case Key.Left: return HiddenBetween(0, caret);
            case Key.Right: return HiddenBetween(caret, doc.TextLength);
        }
        var tv = editor.TextArea.TextView;
        tv.EnsureVisualLines();
        double Top(int offset)
        {
            var location = doc.GetLocation(offset);
            return tv.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineTop).Y;
        }
        double here = editor.TextArea.TextView.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineTop).Y;
        return key == Key.Up ? here <= Top(0) + 0.5 : here >= Top(doc.TextLength) - 0.5;
    }

    /// <summary>
    /// The cell an arrow key leads to from a cell: Left / Right the previous / next cell (at a row's edge the
    /// previous row's last / the next row's first), Up / Down the same cell of the row above / below; the separator
    /// is skipped. Null past the table's edge.
    /// </summary>
    private (int Line, int Cell)? NeighbourCell(int lineNumber, int cellIndex, Key key)
    {
        if (_markdown?.Model.TableAtLine(lineNumber - 1) is not { } table) return null;
        int row = lineNumber - 1 - table.FirstLine;
        bool horizontal = key is Key.Left or Key.Right;
        int step = key is Key.Left or Key.Up ? -1 : 1;
        if (horizontal)
        {
            int cell = cellIndex + step;
            if (row >= 0 && row < table.Rows.Count && cell >= 0 && cell < table.Rows[row].Cells.Count) return (lineNumber, cell);
        }
        int target = row + step;
        while (target >= 0 && target < table.Rows.Count && table.Rows[target].Kind == MdLineKind.TableSeparator) target += step;
        if (target < 0 || target >= table.Rows.Count || table.Rows[target].Cells.Count == 0) return null;
        int count = table.Rows[target].Cells.Count;
        int to = horizontal ? (step < 0 ? count - 1 : 0) : Math.Min(cellIndex, count - 1);
        return (table.FirstLine + target + 1, to);
    }

    private void MoveToCell(int lineNumber, int cellIndex, bool backwards)
    {
        if (_markdown?.Model.TableAtLine(lineNumber - 1) is not { } table) { Editor.Focus(); return; }
        int row = lineNumber - 1 - table.FirstLine;
        int cell = cellIndex + (backwards ? -1 : 1);
        while (true)
        {
            var r = table.Rows[row];
            if (r.Kind != MdLineKind.TableSeparator && cell >= 0 && cell < r.Cells.Count) break;
            row += backwards ? -1 : 1;
            if (row < 0 || row >= table.Rows.Count) { Editor.Focus(); return; }
            cell = backwards ? table.Rows[row].Cells.Count - 1 : 0;
        }
        int target = table.FirstLine + row + 1;
        // After the edit the model and the visual lines are rebuilt: open the next editor once they are.
        Dispatcher.UIThread.Post(() => EditTableCell(target, cell), DispatcherPriority.Background);
    }

    /// <summary>Replaces only the part of a line (at <paramref name="offset"/>) that differs between the two texts.</summary>
    private static void ReplaceChangedPart(TextDocument doc, int offset, string text, string updated)
    {
        int prefix = 0;
        while (prefix < text.Length && prefix < updated.Length && text[prefix] == updated[prefix]) prefix++;
        int suffix = 0;
        while (suffix < text.Length - prefix && suffix < updated.Length - prefix && text[^(suffix + 1)] == updated[^(suffix + 1)]) suffix++;
        doc.Replace(offset + prefix, text.Length - prefix - suffix, updated[prefix..(updated.Length - suffix)]);
    }

    /// <summary>Writes the cell's new Markdown into the source; returns the message key of a problem, or null.</summary>
    private string? CommitCell(int lineNumber, int cellIndex, string edited)
    {
        if (_markdown is not { } view || lineNumber > Editor.Document.LineCount) return null;
        var table = view.Model.TableAtLine(lineNumber - 1);
        if (table is null) return "Table.CellProblem.CellCount";
        var doc = Editor.Document;
        string LineText(int index) => doc.GetText(doc.GetLineByNumber(index + 1));
        var line = doc.GetLineByNumber(lineNumber);
        string text = doc.GetText(line);
        bool isHeader = lineNumber - 1 == table.FirstLine;
        var result = TableCellEdit.ApplySource(text, cellIndex, edited, LineText(table.FirstLine), LineText(table.FirstLine + 1), isHeader);
        if (!result.Ok) return "Table.CellProblem." + result.Problem;
        string updated = result.NewLine!;
        if (updated == text) return null;
        // Replace only the part of the line that differs (keeps the caret, bookmarks and the undo step small).
        ReplaceChangedPart(doc, line.Offset, text, updated);
        return null;
    }
}
