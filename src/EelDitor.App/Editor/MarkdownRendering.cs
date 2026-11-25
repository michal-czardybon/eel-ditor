using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>
/// The width AvaloniaEdit wraps lines at. <c>TextView.Bounds</c> is updated only after layout, so while a resized view
/// builds its lines it still holds the old width; grids built from it were one resize behind, and rows built at
/// different times (in view, by the height primer, after scrolling) got different column widths. The measured width
/// is in the private <c>_lastAvailableSize</c>, set before any line is built (read by reflection; Bounds if missing).
/// </summary>
internal static class WrapWidth
{
    private static readonly System.Reflection.FieldInfo? LastAvailableSize =
        typeof(TextView).GetField("_lastAvailableSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    public static double Of(TextView tv) =>
        LastAvailableSize?.GetValue(tv) is Size size && size.Width > 0 && !double.IsInfinity(size.Width) ? size.Width : tv.Bounds.Width;
}

/// <summary>Adapts an AvaloniaEdit document to the Markdown parser's line source.</summary>
public sealed class DocumentLineSource(TextDocument document) : ILineSource
{
    public int LineCount => document.LineCount;

    public string GetLine(int index)
    {
        var line = document.GetLineByNumber(index + 1);
        return document.GetText(line.Offset, line.Length);
    }
}

/// <summary>
/// Keeps the Markdown display model in sync with a document and installs the rendering pieces
/// (virtual padding, hidden markup, styles) into a <see cref="TextEditor"/>.
/// The document text is never changed by rendering.
/// </summary>
public sealed class MarkdownView : IDisposable
{
    private readonly TextEditor _editor;
    private readonly TextDocument _document;
    private readonly PaddingGenerator _padding;
    private readonly HiddenMarkupGenerator _hidden;
    private readonly MarkdownColorizer _colorizer;
    private readonly WidgetGenerator _widgets;
    private readonly TableGridGenerator _grid;
    private readonly RuleGenerator _rules;
    private readonly RuleRenderer _ruleRenderer;
    private readonly CurrentCellRenderer _cellRenderer;
    private readonly LineHeightPrimer _primer;
    private readonly LongLineGenerators _longLines;
    private readonly GridTextSelection _gridSelection;
    private readonly IBrush? _caretBrush;
    private bool _disposed;

    public MarkdownView(TextEditor editor, TextDocument document, EditorPalette palette, string? baseDirectory = null)
    {
        _editor = editor;
        _document = document;
        BaseDirectory = baseDirectory;
        Source = new DocumentLineSource(document);
        Model = MarkdownDocument.Parse(Source);
        _padding = new PaddingGenerator(this);
        _hidden = new HiddenMarkupGenerator(this);
        _colorizer = new MarkdownColorizer(this, palette);
        _widgets = new WidgetGenerator(this);
        _grid = new TableGridGenerator(this);

        // Padding must come before hidden markup: both can be interested in the same offset and
        // the padding element is zero-length (see VisualLine construction rules). Widgets (task boxes,
        // image previews) replace characters that the hidden-markup generator would otherwise hide.
        var tv = editor.TextArea.TextView;
        // With word wrap on, a table row is drawn as one grid row whose cells wrap (it replaces the whole line).
        tv.ElementGenerators.Insert(0, _grid);
        tv.ElementGenerators.Insert(1, _widgets);
        tv.ElementGenerators.Insert(2, _padding);
        tv.ElementGenerators.Insert(3, _hidden);
        // Horizontal rules: drawn across the view.
        _rules = new RuleGenerator(this);
        tv.ElementGenerators.Insert(4, _rules);
        _ruleRenderer = new RuleRenderer(this);
        tv.BackgroundRenderers.Add(_ruleRenderer);
        _cellRenderer = new CurrentCellRenderer(this, tv).Attach();

        // Lines over 3000 characters (long table rows) get the generated elements too; this must run first.
        _longLines = new LongLineGenerators(tv);
        tv.LineTransformers.Insert(0, _longLines);
        tv.LineTransformers.Add(_colorizer);
        // On a table row drawn as a grid the caret would be as tall as the row: it is hidden and the row is
        // outlined instead.
        _caretBrush = editor.TextArea.Caret.CaretBrush;
        _document.Changed += OnDocumentChanged;
        _document.UpdateFinished += OnUpdateFinished;
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        tv.PointerHover += OnPointerHover;
        tv.PointerHoverStopped += OnPointerHoverStopped;
        tv.Redraw();

        // The real heights of tall lines (tables as grids, images…), so the scrollbar is right from the start.
        _primer = new LineHeightPrimer(editor, this);
        // After OnPointerPressed (registered above): a double click on a cell opens the cell editor first.
        _gridSelection = new GridTextSelection(this);
        tv.PropertyChanged += OnTextViewPropertyChanged;
        editor.PropertyChanged += OnEditorPropertyChanged;
        _primer.All();
    }

    internal TextDocument Document => _document;

    private Func<string, string>? _wordTransform;

    /// <summary>Converts the words of table cells drawn as grids (the Cyrillic view); null: shown as written.</summary>
    public Func<string, string>? WordTransform
    {
        get => _wordTransform;
        set
        {
            _wordTransform = value;
            if (!_disposed) _editor.TextArea.TextView.Redraw();
        }
    }

    private double _primedWidth;

    private void OnTextViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.BoundsProperty) return;
        double width = _editor.TextArea.TextView.Bounds.Width;
        // The width matters only when lines wrap (tables are drawn as grids then).
        if (Math.Abs(width - _primedWidth) < 1 || !_editor.WordWrap) return;
        _primedWidth = width;
        _primer.All();
    }

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextEditor.WordWrapProperty || e.Property == TextEditor.FontSizeProperty || e.Property == TextEditor.FontFamilyProperty)
            _primer.All();
        if (e.Property == TextEditor.WordWrapProperty) UpdateGridCaret();
    }

    /// <summary>The column or row selected in a table shown as text (Select column / row), as source ranges.</summary>
    internal Func<IReadOnlyList<CellRange>>? TextModeCellRanges { get; set; }

    /// <summary>
    /// Selected table cells as source ranges (line number, start, end), when cells rather than text are selected: the
    /// grid's selection, or Select column / row in a table shown as text. Null when there is none.
    /// </summary>
    internal List<(int Line, int Start, int End)>? CellSelectionPieces()
    {
        if (TablesAsGrid) return _gridSelection.SourcePieces();
        if (TextModeCellRanges?.Invoke() is not { Count: > 0 } ranges) return null;
        var pieces = new List<(int, int, int)>();
        foreach (var r in ranges)
        {
            if (Model.GetLine(r.Line).Kind == MdLineKind.TableSeparator || r.Line + 1 > _document.LineCount) continue;
            string text = _document.GetText(_document.GetLineByNumber(r.Line + 1));
            int s = r.Start, e = Math.Min(r.End, text.Length);
            while (s < e && text[s] is ' ' or '\t') s++;
            while (e > s && text[e - 1] is ' ' or '\t') e--;
            if (e > s) pieces.Add((r.Line + 1, s, e));
        }
        return pieces;
    }

    /// <summary>The selection of shown text in tables drawn as grids.</summary>
    internal GridTextSelection GridSelection => _gridSelection;

    /// <summary>The line is a table row drawn as one grid row (one element for the whole line).</summary>
    internal bool IsGridRow(DocumentLine line) =>
        TablesAsGrid && line.Length > 0 && Model.TableAtLine(line.LineNumber - 1)?.RowAtLine(line.LineNumber - 1) is not null;

    /// <summary>The caret's line when it is a grid row (then the caret itself is hidden), else null.</summary>
    internal DocumentLine? CaretGridRow
    {
        get
        {
            if (_disposed || !ReferenceEquals(_editor.Document, _document)) return null;
            var line = _document.GetLineByOffset(Math.Min(_editor.CaretOffset, _document.TextLength));
            return IsGridRow(line) ? line : null;
        }
    }

    private bool _caretHidden;

    private void UpdateGridCaret()
    {
        bool hide = CaretGridRow is not null;
        if (hide != _caretHidden)
        {
            _caretHidden = hide;
            _editor.TextArea.Caret.CaretBrush = hide ? Brushes.Transparent : _caretBrush;
            // The current-line band would cover the whole grid row with its bottom border (and so look shifted down);
            // the current cell is highlighted instead.
            if (hide) { _highlightLine = _editor.Options.HighlightCurrentLine; _editor.Options.HighlightCurrentLine = false; }
            else if (_highlightLine is { } restore) { _editor.Options.HighlightCurrentLine = restore; _highlightLine = null; }
        }
        // The current cell is drawn by its row (exactly where the grid is): rebuild the rows that change.
        var current = CaretGridRow is { } row ? (row.LineNumber, CellAtCaret(row)) : ((int, int)?)null;
        if (current != _currentCell)
        {
            var tv = _editor.TextArea.TextView;
            foreach (var n in new[] { _currentCell?.Line, current?.Item1 })
                if (n is int number && number <= _document.LineCount) tv.Redraw(_document.GetLineByNumber(number));
            _currentCell = current;
        }
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _cellRenderer?.InvalidateVisual();
    }

    private (int Line, int Cell)? _currentCell;

    /// <summary>Redraws the frames of the current and the selected cells.</summary>
    internal void InvalidateCellFrames() => _cellRenderer?.InvalidateVisual();

    /// <summary>The user's current-line highlight, turned off while the caret is on a grid row; null when not changed.</summary>
    private bool? _highlightLine;

    /// <summary>The user's choice of the current-line band (applied now, or when the caret leaves a grid row).</summary>
    internal void SetCurrentLineHighlight(bool on)
    {
        if (_highlightLine is not null) _highlightLine = on;
        else _editor.Options.HighlightCurrentLine = on;
    }

    /// <summary>The cell of a grid row the caret is in (by its column in the source), for drawing; null on other rows.</summary>
    internal int? CurrentCellOf(DocumentLine line) => _currentCell is { } c && c.Line == line.LineNumber ? c.Cell : null;

    /// <summary>The current cell (line number, cell index), or null when the caret is not on a grid row.</summary>
    internal (int Line, int Cell)? CurrentCell => _currentCell;

    private int CellAtCaret(DocumentLine line)
    {
        if (Model.TableAtLine(line.LineNumber - 1)?.RowAtLine(line.LineNumber - 1) is not { Cells.Count: > 0 } row) return 0;
        int column = Math.Clamp(_editor.CaretOffset - line.Offset, 0, line.Length);
        for (int c = 0; c < row.Cells.Count; c++)
            if (column <= row.Cells[c].End) return c;
        return row.Cells.Count - 1;
    }

    /// <summary>
    /// Typing over a selection keeps the style markers whole; typing right after a styled word extends the style;
    /// afterwards neighbouring spans of the same style are joined.
    /// </summary>
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _editor.IsReadOnly) return;
        if (_editor.SelectionLength == 0 && CaretGridRow is not null)
        {
            e.Handled = true;
            return;
        }
        EndLineBreakUndoGroup();
        // Everything done here and the insertion itself are one undo step (ended in OnTextEntered).
        _document.UndoStack.StartUndoGroup();
        _lineBreakUndoGroup = true;
        // An empty selection left over by an edit: typing must move the caret as usual.
        if (_editor.SelectionLength == 0 && !_editor.TextArea.Selection.IsEmpty) _editor.TextArea.ClearSelection();
        if (MarkdownCommands.SelectionShowsNothing(_editor, this))
        {
            // Only hidden markers selected (nothing shows as selected): typing as without a selection.
            int at = _editor.SelectionStart;
            _editor.TextArea.ClearSelection();
            _editor.CaretOffset = at;
        }
        if (_editor.SelectionLength > 0 && _editor.TextArea.Selection is not AvaloniaEdit.Editing.RectangleSelection && !e.Text.Contains('\n'))
        {
            // Like a word processor: the typed text takes the place, and the style, of the selection's first
            // character. It is inserted before the selection is deleted, so no marker is ever left next to a space
            // ("*iele s*" with "s" typed over stays italic, instead of passing through "*iele *").
            MarkdownCommands.ReplaceSelectionWithTyped(_editor, this, e.Text);
            e.Handled = true;
            EndLineBreakUndoGroup();
            return;
        }
        if (_editor.SelectionLength > 0 && _editor.TextArea.Selection is not AvaloniaEdit.Editing.RectangleSelection
            && !MarkdownCommands.DeleteSelectionKeepingMarkers(_editor, this))
        {
            // Deleted here rather than replaced by the editor, so the rules below see the caret where the text goes.
            int start = _editor.SelectionStart;
            _document.Remove(start, _editor.SelectionLength);
            _editor.Select(start, 0);
        }
        if (ApplyPendingStyle(e.Text)) { e.Handled = true; EndLineBreakUndoGroup(); return; }
        // Enter inside bold, italic…: the markers are closed and reopened around the break.
        if (e.Text.Contains('\n') && MarkdownCommands.PrepareLineBreakInStyle(_editor, this)) return;
        MarkdownCommands.AdjustCaretForTyping(_editor, this, e.Text);
        MarkdownCommands.EnsureTypingShowsAsTyped(_editor, this, e.Text);
    }

    private bool _lineBreakUndoGroup;

    // ---------------------------------------------------------------- a style for the next typed text

    private readonly List<string> _pendingMarkers = [];
    private int _pendingOffset = -1;

    /// <summary>
    /// Ctrl+B (I, …) with nothing to style: like a word processor, the style is applied to what is typed next at
    /// this place; nothing is inserted until then (an empty "****" would show as stars). Again: off.
    /// </summary>
    internal void TogglePendingStyle(string marker)
    {
        if (_pendingOffset != _editor.CaretOffset) ClearPendingStyle();
        _pendingOffset = _editor.CaretOffset;
        if (!_pendingMarkers.Remove(marker)) _pendingMarkers.Add(marker);
        if (_pendingMarkers.Count == 0) _pendingOffset = -1;
    }

    internal bool HasPendingStyle => _pendingOffset >= 0;

    private void ClearPendingStyle()
    {
        _pendingMarkers.Clear();
        _pendingOffset = -1;
    }

    private bool ApplyPendingStyle(string typed)
    {
        if (_pendingOffset < 0) return false;
        var markers = _pendingMarkers.ToList();
        bool here = _pendingOffset == _editor.CaretOffset && _editor.SelectionLength == 0;
        ClearPendingStyle();
        if (!here || string.IsNullOrWhiteSpace(typed) || typed.Contains('\n')) return false;
        string open = string.Concat(markers), close = string.Concat(Enumerable.Reverse(markers));
        int at = _editor.CaretOffset;
        var line = _document.GetLineByOffset(at);
        string text = _document.GetText(line);
        int col = at - line.Offset;
        string candidate = text[..col] + open + typed + close + text[col..];
        // Only when the typed text really gets the style there (not e.g. inside a word with "_").
        if (InlineStyleEdit.LineParser(candidate) is not { } r
            || !r.Styles.Any(s => s.Start <= col + open.Length && s.End >= col + open.Length + typed.Length))
            return false;
        _document.Insert(at, open + typed + close);
        _editor.CaretOffset = at + open.Length + typed.Length;
        return true;
    }

    // ---------------------------------------------------------------- the caret never inside a marker

    private bool _snapPosted;

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_pendingOffset >= 0 && _editor.CaretOffset != _pendingOffset) ClearPendingStyle();
        UpdateGridCaret();
        if (_snapPosted) return;
        _snapPosted = true;
        Dispatcher.UIThread.Post(SnapCaret, DispatcherPriority.Send);
    }

    /// <summary>
    /// A caret inside hidden markup (between the two "*" of "**", inside a link's address) would make the next
    /// edit split a marker; it goes to the end of the markup (the same place on screen).
    /// </summary>
    private void SnapCaret()
    {
        _snapPosted = false;
        if (_disposed || _editor.SelectionLength > 0 || !ReferenceEquals(_editor.Document, _document)) return;
        int offset = _editor.CaretOffset;
        var line = _document.GetLineByOffset(offset);
        int col = offset - line.Offset;
        foreach (var h in RenderFor(line).Hidden)
            if (col > h.Start && col < h.End)
            {
                _editor.CaretOffset = line.Offset + h.End;
                return;
            }
    }

    private void EndLineBreakUndoGroup()
    {
        if (!_lineBreakUndoGroup) return;
        _lineBreakUndoGroup = false;
        _document.UndoStack.EndUndoGroup();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        EndLineBreakUndoGroup();
        if (string.IsNullOrEmpty(e.Text) || _editor.IsReadOnly) return;
        // The caret follows the removed markers by itself (the document moves it).
        MarkdownCommands.MergeAdjacentSpans(_document, this, _document.GetLineByOffset(_editor.CaretOffset).LineNumber);
    }

    /// <summary>Directory that relative image paths and links are resolved against (the file's folder).</summary>
    public string? BaseDirectory { get; }

    internal TextEditor Editor => _editor;

    /// <summary>Ctrl+click on a link (the URL or path as written).</summary>
    public event Action<string>? LinkActivated;

    /// <summary>Double click on a table cell drawn as a grid (word wrap on): line number (1-based) and cell index.</summary>
    public event Action<int, int, int?>? CellEditRequested;

    /// <summary>Tables are drawn as grids with wrapping cells (the editor wraps lines).</summary>
    public bool TablesAsGrid => _editor.WordWrap;

    /// <summary>Image previews at all (off in the cell editor: a table shows its images the same way everywhere).</summary>
    internal bool ShowImages { get; init; } = true;

    /// <summary>
    /// Whether images on the line are drawn as previews. Not in tables: a grid cannot show them, so a table shows an
    /// image as its text (italic) in every view — as text, as a grid and in the cell editor alike.
    /// </summary>
    internal bool PreviewsImagesOn(DocumentLine line) => ShowImages && Model.TableAtLine(line.LineNumber - 1) is null;

    /// <summary>Grid layout of a table, recomputed when the model or the available width changes.</summary>
    internal TableGridLayout? GridLayout(MarkdownTable table) => _grid.Layout(table);

    /// <summary>
    /// The rectangle of a cell in text-view coordinates (for the cell editor), or null when the row is not visible.
    /// </summary>
    public Rect? CellRect(int lineNumber, int cellIndex)
    {
        var tv = _editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var line = _document.GetLineByNumber(lineNumber);
        var visual = tv.GetVisualLine(lineNumber);
        if (visual is null || Model.TableAtLine(lineNumber - 1) is not { } table) return null;
        var row = table.RowAtLine(lineNumber - 1);
        if (row is null || cellIndex >= row.Cells.Count) return null;
        double top = visual.VisualTop - tv.ScrollOffset.Y;
        if (TablesAsGrid && GridLayout(table) is { } layout)
        {
            double cw = tv.WideSpaceWidth;
            double x0 = visual.GetVisualPosition(0, VisualYPosition.LineTop).X - tv.ScrollOffset.X;
            double left = x0 + TableWrap.BorderAt(layout.Widths, cellIndex) * cw;
            double width = (layout.Widths[Math.Min(cellIndex, layout.Widths.Length - 1)] + 3) * cw;
            double lh = tv.DefaultLineHeight;
            bool header = row.LineIndex == table.FirstLine;
            return new Rect(left, top + (header ? lh : 0), width, lh);
        }
        string text = _document.GetText(line);
        var cell = row.Cells[cellIndex];
        double a = visual.GetVisualPosition(visual.GetVisualColumn(cell.Start), VisualYPosition.LineTop).X - tv.ScrollOffset.X;
        double b = visual.GetVisualPosition(visual.GetVisualColumn(Math.Min(cell.End, text.Length)), VisualYPosition.LineTop).X - tv.ScrollOffset.X;
        return new Rect(a, top, Math.Max(40, b - a), tv.DefaultLineHeight);
    }

    /// <summary>The cell under the pointer of a table row drawn as a grid, or null.</summary>
    private (int Line, int Cell, int? Offset)? GridCellAt(PointerEventArgs e)
    {
        if (!TablesAsGrid) return null;
        var tv = _editor.TextArea.TextView;
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        var visual = tv.GetVisualLineFromVisualTop(p.Y);
        if (visual is null || visual.Elements.Count != 1 || visual.Elements[0] is not TableRowElement el) return null;
        double x0 = visual.GetVisualPosition(0, VisualYPosition.LineTop).X;
        int ch = (int)Math.Floor((p.X - x0) / tv.WideSpaceWidth);
        var widths = el.Layout.Widths;
        int cell = 0;
        while (cell + 1 < widths.Length && ch >= TableWrap.BorderAt(widths, cell + 1)) cell++;
        // Where in the cell's shown text: the cell editor puts its caret there.
        int drawn = (int)Math.Floor((p.Y - visual.VisualTop) / Math.Max(1, tv.DefaultLineHeight));
        int? offset = el.Hit((p.X - x0) / Math.Max(1, tv.WideSpaceWidth), drawn) is { } hit && hit.Cell == cell ? hit.Offset : null;
        return (visual.FirstDocumentLine.LineNumber, cell, offset);
    }

    /// <summary>Checks or unchecks the task box on the given line (1-based); false when the line has none.</summary>
    public bool ToggleTask(int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > _document.LineCount) return false;
        var line = _document.GetLineByNumber(lineNumber);
        var render = RenderFor(line);
        if (render.TaskColumn < 0) return false;
        _document.Replace(line.Offset + render.TaskColumn + 1, 1, render.TaskChecked ? " " : "x");
        return true;
    }

    /// <summary>The styled range under the pointer (links, footnotes, images), with its line.</summary>
    private (DocumentLine Line, StyledRange Range)? RangeAt(PointerEventArgs e)
    {
        if (GridRowAt(e) is { } grid) return GridRangeAt(e, grid.Visual, grid.Element);
        if (_editor.GetPositionFromPoint(e.GetPosition(_editor)) is not { } pos) return null;
        var line = _document.GetLineByNumber(pos.Line);
        int col = _document.GetOffset(pos.Location) - line.Offset;
        foreach (var s in RenderFor(line).Styles)
            if (s.Url is not null && col >= s.Start && col <= s.End && (s.Style & (MdStyle.Link | MdStyle.Footnote | MdStyle.Image)) != 0)
                return (line, s);
        return null;
    }

    /// <summary>Whether a link (footnote, image) is under the pointer.</summary>
    internal bool HasLinkAt(PointerEventArgs e) => RangeAt(e) is not null;

    /// <summary>The grid row under the pointer (tables drawn as grids), or null.</summary>
    private (VisualLine Visual, TableRowElement Element)? GridRowAt(PointerEventArgs e)
    {
        if (!TablesAsGrid) return null;
        var tv = _editor.TextArea.TextView;
        if (!tv.VisualLinesValid) return null;
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        var visual = tv.GetVisualLineFromVisualTop(p.Y);
        return visual is { Elements.Count: 1 } && visual.Elements[0] is TableRowElement el ? (visual, el) : null;
    }

    /// <summary>
    /// The link (footnote, image) under the pointer in a grid row: the character under it, mapped back to the source
    /// through the hidden markup. A grid row is one element, so the editor's own hit-testing gives only its start.
    /// </summary>
    private (DocumentLine Line, StyledRange Range)? GridRangeAt(PointerEventArgs e, VisualLine visual, TableRowElement el)
    {
        var tv = _editor.TextArea.TextView;
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        double x0 = visual.GetVisualPosition(0, VisualYPosition.LineTop).X;
        int drawn = (int)Math.Floor((p.Y - visual.VisualTop) / Math.Max(1, tv.DefaultLineHeight));
        if (el.CharAt((p.X - x0) / Math.Max(1, tv.WideSpaceWidth), drawn) is not { } hit) return null;
        var line = visual.FirstDocumentLine;
        if (Model.TableAtLine(line.LineNumber - 1)?.RowAtLine(line.LineNumber - 1) is not { } row || hit.Cell >= row.Cells.Count) return null;
        var render = RenderFor(line);
        var map = TableWrap.ShownToSource(_document.GetText(line), row.Cells[hit.Cell], render);
        if (hit.Index >= map.Count) return null;
        int col = map[hit.Index];
        foreach (var s in render.Styles)
            if (s.Url is not null && col >= s.Start && col < s.End && (s.Style & (MdStyle.Link | MdStyle.Footnote | MdStyle.Image)) != 0)
                return (line, s);
        return null;
    }

    /// <summary>Line number of the task box under the pointer, or 0.</summary>
    private int TaskBoxAt(PointerEventArgs e)
    {
        var tv = _editor.TextArea.TextView;
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        var visualLine = tv.GetVisualLineFromVisualTop(p.Y);
        if (visualLine is null) return 0;
        var line = visualLine.FirstDocumentLine;
        var render = RenderFor(line);
        if (render.TaskColumn < 0) return 0;
        double left = visualLine.GetVisualPosition(visualLine.GetVisualColumn(render.TaskStart), VisualYPosition.TextTop).X;
        double right = visualLine.GetVisualPosition(visualLine.GetVisualColumn(render.TaskColumn + 3), VisualYPosition.TextTop).X;
        return p.X >= left && p.X <= right ? line.LineNumber : 0;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_editor.TextArea).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2 && GridCellAt(e) is { } cell && Model.TableAtLine(cell.Line - 1)?.RowAtLine(cell.Line - 1)?.Kind != MdLineKind.TableSeparator)
        {
            // The caret goes into the edited cell's text, so that cell stays the current one.
            var line = _document.GetLineByNumber(cell.Line);
            int column = Model.TableAtLine(cell.Line - 1)?.RowAtLine(cell.Line - 1) is { } row && cell.Cell < row.Cells.Count
                ? row.Cells[cell.Cell].ContentStart(_document.GetText(line)) : 0;
            _editor.TextArea.Caret.Offset = line.Offset + column;
            CellEditRequested?.Invoke(cell.Line, cell.Cell, cell.Offset);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (TaskBoxAt(e) is > 0 and int taskLine && ToggleTask(taskLine)) e.Handled = true;
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (RangeAt(e) is not { } hit) return;
        if (hit.Range.Style.HasFlag(MdStyle.Footnote))
        {
            int target = Model.FootnoteDefinitionLine(hit.Range.Url!);
            if (target < 0) return;
            var line = _document.GetLineByNumber(target + 1);
            _editor.TextArea.Caret.Offset = line.Offset;
            _editor.TextArea.Caret.BringCaretToView();
        }
        else LinkActivated?.Invoke(hit.Range.Url!);
        e.Handled = true;
    }

    private void OnPointerHover(object? sender, PointerEventArgs e)
    {
        if (RangeAt(e) is not { } hit) return;
        string? tip = hit.Range.Style.HasFlag(MdStyle.Footnote) ? FootnoteText(hit.Range.Url!) : hit.Range.Url;
        if (string.IsNullOrEmpty(tip)) return;
        if (!hit.Range.Style.HasFlag(MdStyle.Image)) tip += Environment.NewLine + "Ctrl+klik / Ctrl+click";
        var tv = _editor.TextArea.TextView;
        ToolTip.SetTip(tv, tip);
        ToolTip.SetIsOpen(tv, true);
    }

    private void OnPointerHoverStopped(object? sender, PointerEventArgs e)
    {
        var tv = _editor.TextArea.TextView;
        ToolTip.SetIsOpen(tv, false);
        ToolTip.SetTip(tv, null);
    }

    private string? FootnoteText(string label)
    {
        int i = Model.FootnoteDefinitionLine(label);
        if (i < 0) return null;
        string text = Source.GetLine(i);
        int colon = text.IndexOf("]:", StringComparison.Ordinal);
        return colon < 0 ? text : text[(colon + 2)..].Trim();
    }

    public DocumentLineSource Source { get; }

    private MarkdownDocument _model = null!;
    private bool _modelStale;

    /// <summary>
    /// The display model, reparsed lazily after changes: a command that changes 800 table rows in one update parses
    /// the document once, not 800 times (which froze the editor for seconds, and as long again on undo).
    /// </summary>
    public MarkdownDocument Model
    {
        get
        {
            if (_modelStale)
            {
                _modelStale = false;
                _model = MarkdownDocument.Parse(Source);
            }
            return _model;
        }
        private set { _model = value; _modelStale = false; }
    }

    public event EventHandler? ModelChanged;

    public EditorPalette Palette
    {
        get => _colorizer.Palette;
        set { _colorizer.Palette = value; _editor.TextArea.TextView.Redraw(); }
    }

    // Lines (1-based) changed since the display was last brought up to date; -1 when none.
    private int _changedFrom = -1, _changedTo = -1;

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _modelStale = true;
        int first = _document.GetLineByOffset(Math.Min(e.Offset, _document.TextLength)).LineNumber;
        int last = _document.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, _document.TextLength)).LineNumber;
        _changedFrom = _changedFrom < 0 ? first : Math.Min(_changedFrom, first);
        _changedTo = Math.Max(_changedTo, last);
        // Inside an update (a command, undo, redo) the rest waits until it ends: once for all its changes.
        if (!_document.IsInUpdate) AfterChanges();
    }

    private void OnUpdateFinished(object? sender, EventArgs e)
    {
        if (_changedFrom >= 0) AfterChanges();
    }

    /// <summary>Brings the display up to date with the changed text (once per edit or per update).</summary>
    private void AfterChanges()
    {
        if (_disposed) return;
        int from = Math.Min(_changedFrom, _document.LineCount), to = Math.Min(Math.Max(_changedTo, from), _document.LineCount);
        _changedFrom = _changedTo = -1;
        ClearPendingStyle();
        UpdateGridCaret();
        ModelChanged?.Invoke(this, EventArgs.Empty);
        _editor.TextArea.TextView.Redraw();
        // An edited table can change the column widths, and so the height of rows out of view.
        if (!TablesAsGrid || from < 1) return;
        var measured = new HashSet<int>();
        for (int n = from; n <= to; n++)
            if (Model.TableAtLine(n - 1) is { } table && measured.Add(table.FirstLine))
            {
                _primer.Lines(table.FirstLine + 1, table.LastLine + 1);
                n = table.LastLine + 1;
            }
    }

    public LineRender RenderFor(DocumentLine line) => Model.GetLine(line.LineNumber - 1);

    /// <summary>
    /// AvaloniaEdit's VisualLine.LENGTH_LIMIT: longer lines get no element generators from AvaloniaEdit (no zero-width
    /// elements, no padding); <see cref="LongLineGenerators"/> runs them for those lines.
    /// </summary>
    public const int GeneratorLineLimit = 3000;

    public void Dispose()
    {
        _disposed = true;
        _document.Changed -= OnDocumentChanged;
        _document.UpdateFinished -= OnUpdateFinished;
        _editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        var tv = _editor.TextArea.TextView;
        _primer.Dispose();
        _gridSelection.Dispose();
        tv.PropertyChanged -= OnTextViewPropertyChanged;
        _editor.PropertyChanged -= OnEditorPropertyChanged;
        tv.ElementGenerators.Remove(_grid);
        if (_caretHidden) _editor.TextArea.Caret.CaretBrush = _caretBrush;
        if (_highlightLine is { } highlight) _editor.Options.HighlightCurrentLine = highlight;

        tv.ElementGenerators.Remove(_widgets);
        tv.ElementGenerators.Remove(_padding);
        tv.ElementGenerators.Remove(_hidden);
        tv.ElementGenerators.Remove(_rules);
        tv.BackgroundRenderers.Remove(_ruleRenderer);
        _cellRenderer.Detach();
        _editor.TextArea.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _editor.TextArea.TextEntering -= OnTextEntering;
        _editor.TextArea.TextEntered -= OnTextEntered;
        tv.PointerHover -= OnPointerHover;
        tv.PointerHoverStopped -= OnPointerHoverStopped;
        tv.LineTransformers.Remove(_colorizer);
        tv.LineTransformers.Remove(_longLines);
        tv.Redraw();
    }
}

/// <summary>
/// Inserts virtual spaces/dashes so table columns line up. When possible the padding is merged with a
/// neighbouring real character (space or pipe) into one element, so the caret never stops inside padding.
/// </summary>
internal sealed class PaddingGenerator(MarkdownView view) : VisualLineElementGenerator
{
    private readonly record struct Spec(int Offset, int DocLength, string Text);

    private readonly List<Spec> _specs = [];

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _specs.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        var render = view.RenderFor(line);
        if (render.Paddings.Count == 0) return;

        string text = context.Document.GetText(line.Offset, line.Length);
        var claimed = new HashSet<int>();
        foreach (var group in render.Paddings.GroupBy(p => p.Offset).OrderBy(g => g.Key))
        {
            int at = group.Key;
            if (at > text.Length) continue;
            string fill = string.Concat(group.Select(p => new string(p.Fill, p.Width)));
            if (at < text.Length && IsAbsorbable(text[at], render, at) && claimed.Add(at))
                _specs.Add(new Spec(line.Offset + at, 1, fill + text[at]));
            else if (at > 0 && IsAbsorbable(text[at - 1], render, at - 1) && claimed.Add(at - 1))
                _specs.Add(new Spec(line.Offset + at - 1, 1, text[at - 1] + fill));
            else
                _specs.Add(new Spec(line.Offset + at, 0, fill));
        }
        _specs.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.DocLength.CompareTo(b.DocLength));
    }

    private static bool IsAbsorbable(char c, LineRender render, int column) =>
        c is ' ' or '|' && !render.IsHidden(column);

    public override int GetFirstInterestedOffset(int startOffset)
    {
        foreach (var s in _specs)
            if (s.Offset >= startOffset) return s.Offset;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var s in _specs)
            if (s.Offset == offset) return new FormattedTextElement(s.Text, s.DocLength);
        return null;
    }
}

/// <summary>
/// Replaces markup characters (**, _, `, #, link targets) with zero-width elements.
/// AvaloniaEdit does not run element generators for lines longer than <see cref="MarkdownView.GeneratorLineLimit"/>;
/// <see cref="LongLineGenerators"/> runs them (or, if it cannot, <see cref="MarkdownColorizer"/> collapses the markup).
/// </summary>
internal sealed class HiddenMarkupGenerator(MarkdownView view) : VisualLineElementGenerator
{
    private readonly List<HiddenRange> _ranges = [];
    private IReadOnlyList<ImageRef> _images = [];
    private int _lineOffset;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _ranges.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        _lineOffset = line.Offset;
        var render = view.RenderFor(line);
        _images = view.PreviewsImagesOn(line) ? render.Images : [];
        // Merge adjacent ranges (e.g. "***" parsed as "*" + "**") into a single element; an image tail stays
        // separate because the widget generator draws the preview in its place.
        foreach (var h in render.Hidden)
        {
            if (h.End > line.Length) continue;
            if (_ranges.Count > 0 && _ranges[^1].End >= h.Start
                && !render.Images.Any(im => im.AnchorStart == h.Start || im.AnchorStart == _ranges[^1].Start))
            {
                var last = _ranges[^1];
                _ranges[^1] = new HiddenRange(last.Start, Math.Max(last.End, h.End) - last.Start);
            }
            else _ranges.Add(h);
        }
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        foreach (var r in _ranges)
            if (_lineOffset + r.Start >= startOffset) return _lineOffset + r.Start;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var r in _ranges)
            if (_lineOffset + r.Start == offset)
            {
                // The tail of an image whose preview is shown: its last character carries the preview.
                if (_images.FirstOrDefault(i => i.AnchorStart == r.Start) is { Path: not null } image && r.Length == image.AnchorLength
                    && ImageCache.Load(view.BaseDirectory, image.Path) is not null)
                    return r.Length > 1 ? new HiddenElement(r.Length - 1) : null;
                return new HiddenElement(r.Length);
            }
        return null;
    }
}

/// <summary>
/// An element drawn in place of several document characters. It has one visual column per character, so the text
/// line's character indices stay in step with the document: with a single column for many characters, AvaloniaEdit
/// maps clicks after the element too far to the right (by the number of extra characters). The caret skips the
/// inside, so it still behaves as one unit.
/// </summary>
public abstract class WholeElement(int documentLength) : VisualLineElement(Math.Max(1, documentLength), documentLength)
{
    /// <summary>Text-source length of the run that starts at <paramref name="startVisualColumn"/>.</summary>
    protected int RunLength(int startVisualColumn) => Math.Max(1, VisualLength - (startVisualColumn - VisualColumn));

    public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, AvaloniaEdit.Document.CaretPositioningMode mode)
    {
        int start = VisualColumn, end = VisualColumn + VisualLength;
        if (direction == AvaloniaEdit.Document.LogicalDirection.Forward)
            return visualColumn < start ? start : visualColumn < end ? end : -1;
        return visualColumn > end ? end : visualColumn > start ? start : -1;
    }
}

/// <summary>Hidden markup: zero width, the caret jumps over it.</summary>
internal sealed class HiddenElement(int documentLength) : WholeElement(documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new ZeroWidthRun(TextRunProperties, 0, RunLength(startVisualColumn));
}

/// <summary>
/// The grid trims trailing whitespace. Draw it at its regular width until it reaches the available space.
/// </summary>
internal sealed class CellTrailingWhitespaceGenerator(MarkdownView view, int columns) : VisualLineElementGenerator
{
    internal const double TrailingSpaceAllowance = 0.75;
    public int Columns { get; set; } = columns;

    private int _start;
    private int _length;
    private double _scale;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        var line = context.VisualLine.FirstDocumentLine;
        var text = context.Document.GetText(line.Offset, line.Length);
        int trimmedLength = text.AsSpan().TrimEnd().Length;
        _start = line.Offset + trimmedLength;
        _length = line.Length - trimmedLength;
        if (_length == 0) return;

        var render = view.RenderFor(line);
        string shown = new(Enumerable.Range(0, trimmedLength).Where(i => !render.IsHidden(i)).Select(i => text[i]).ToArray());
        var (start, length) = TableWrap.WrapRanges(shown, Columns)[^1];
        int used = TextWidth.Of(shown.AsSpan(start, length));
        _scale = Math.Min(1, (Math.Max(0, Columns - used) + TrailingSpaceAllowance) / _length);
    }

    public override int GetFirstInterestedOffset(int startOffset) =>
        _length > 0 && _scale < 1 && startOffset <= _start ? _start : -1;

    public override VisualLineElement? ConstructElement(int offset) =>
        offset == _start && _length > 0 && _scale < 1 ? new TrailingWhitespaceElement(CurrentContext.VisualLine, _length, _scale) : null;

    private sealed class TrailingWhitespaceElement(VisualLine line, int length, double scale) : VisualLineText(line, length)
    {
        // One run per space gives the formatter a caret stop at each offset.
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new TrailingSpaceRun(TextRunProperties, scale * context.TextView.WideSpaceWidth);
    }

    private sealed class TrailingSpaceRun(TextRunProperties properties, double width) : DrawableTextRun
    {
        public override int Length => 1;
        public override TextRunProperties Properties => properties;
        public override Size Size => new(width, 0);
        public override double Baseline => 0;
        public override void Draw(DrawingContext drawingContext, Point origin) { }
    }
}

internal sealed class ZeroWidthRun(TextRunProperties properties, double height, int length = 1) : DrawableTextRun
{
    public override int Length { get; } = Math.Max(1, length);
    public override TextRunProperties Properties { get; } = properties;
    public override Size Size => new(0, height);
    public override double Baseline => height * 0.8;
    public override void Draw(DrawingContext drawingContext, Point origin) { }
}

/// <summary>Applies Markdown styles. Bold keeps the monospace font, so alignment is unaffected.</summary>
internal sealed class MarkdownColorizer(MarkdownView view, EditorPalette palette) : DocumentColorizingTransformer
{
    public EditorPalette Palette { get; set; } = palette;

    protected override void ColorizeLine(DocumentLine line)
    {
        var render = view.RenderFor(line);
        int o = line.Offset;
        switch (render.Kind)
        {
            case MdLineKind.FenceDelimiter:
                Change(o, o + line.Length, el => el.TextRunProperties.SetForegroundBrush(Palette.Dim));
                return;
            case MdLineKind.Code:
                Change(o, o + line.Length, el => el.TextRunProperties.SetForegroundBrush(Palette.CodeForeground));
                return;
            case MdLineKind.TableSeparator:
                Change(o, o + line.Length, el => el.TextRunProperties.SetForegroundBrush(Palette.Dim));
                return;
        }

        // <br>: a line break in a table cell (drawn as one in the grid); in the text it stays, dimmed.
        foreach (var b in render.Breaks)
            if (b.End <= line.Length) Change(o + b.Start, o + b.End, el => el.TextRunProperties.SetForegroundBrush(Palette.Dim));

        foreach (var s in render.Styles)
        {
            var style = s.Style;
            Change(o + s.Start, o + s.End, el =>
            {
                var p = el.TextRunProperties;
                var tf = p.Typeface;
                var weight = style.HasFlag(MdStyle.Bold) ? FontWeight.Bold : tf.Weight;
                var fontStyle = style.HasFlag(MdStyle.Italic) ? FontStyle.Italic : tf.Style;
                if (weight != tf.Weight || fontStyle != tf.Style)
                    p.SetTypeface(new Typeface(tf.FontFamily, fontStyle, weight, tf.Stretch));
                if (style.HasFlag(MdStyle.Strike)) p.SetTextDecorations(TextDecorations.Strikethrough);
                if (style.HasFlag(MdStyle.Code))
                {
                    p.SetForegroundBrush(Palette.CodeForeground);
                    el.BackgroundBrush = Palette.CodeBackground;
                }
                if (style.HasFlag(MdStyle.Link))
                {
                    p.SetForegroundBrush(Palette.Accent);
                    p.SetTextDecorations(TextDecorations.Underline);
                }
                if (style.HasFlag(MdStyle.Heading)) p.SetForegroundBrush(Palette.Accent);
                if (style.HasFlag(MdStyle.Highlight)) el.BackgroundBrush = Palette.Highlight;
                if (style.HasFlag(MdStyle.Footnote))
                {
                    p.SetForegroundBrush(Palette.Accent);
                    if (s.Url is not null) // a reference: superscript
                    {
                        p.SetFontRenderingEmSize(p.FontRenderingEmSize * 0.75);
                        p.SetBaselineAlignment(BaselineAlignment.Superscript);
                    }
                }
                if (style.HasFlag(MdStyle.Image))
                {
                    p.SetForegroundBrush(Palette.Dim);
                    p.SetTypeface(new Typeface(p.Typeface.FontFamily, FontStyle.Italic, p.Typeface.Weight, p.Typeface.Stretch));
                }
                if (style.HasFlag(MdStyle.TaskDone)) p.SetForegroundBrush(Palette.Dim);
                if (style.HasFlag(MdStyle.Subscript) || style.HasFlag(MdStyle.Superscript))
                {
                    p.SetFontRenderingEmSize(p.FontRenderingEmSize * 0.75);
                    p.SetBaselineAlignment(style.HasFlag(MdStyle.Subscript) ? BaselineAlignment.Subscript : BaselineAlignment.Superscript);
                }
            });
        }

        // Symbols and emoji in bold text stay at normal weight (see SymbolText: bold made them white, for good).
        string lineText = CurrentContext.Document.GetText(line);
        if (SymbolText.HasSymbol(lineText))
            foreach (var (start, length, symbols) in SymbolText.Runs(lineText))
                if (symbols)
                    Change(o + start, o + start + length, el =>
                    {
                        var tf = el.TextRunProperties.Typeface;
                        if (tf.Weight != FontWeight.Normal) el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeight.Normal, tf.Stretch));
                    });

        // Headings: the level shows in the size (the font stays monospace; headings are not aligned with anything).
        if (render.Kind == MdLineKind.Heading && HeadingScale(render.HeadingLevel) is var scale and > 1)
            Change(o, o + line.Length, el => el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * scale));

        if (line.Length > MarkdownView.GeneratorLineLimit && !LongLineGenerators.Supported)
        {
            // Long line: the hidden-markup generator is not run, so draw the markers transparent and
            // practically zero-width. The characters stay in the text (the caret logic skips them as usual).
            foreach (var h in render.Hidden)
                Change(o + h.Start, o + Math.Min(h.End, line.Length), el =>
                {
                    el.TextRunProperties.SetFontRenderingEmSize(0.01);
                    el.TextRunProperties.SetBaselineAlignment(BaselineAlignment.Baseline);
                    el.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
                    el.BackgroundBrush = null;
                });
        }

        if (render.IsTableRow)
        {
            // Dim the pipes so the table reads as a grid rather than punctuation.
            string text = CurrentContext.Document.GetText(line.Offset, line.Length);
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '|' && !render.IsHidden(i))
                    Change(o + i, o + i + 1, el => el.TextRunProperties.SetForegroundBrush(Palette.Dim));
        }
    }

    private void Change(int start, int end, Action<VisualLineElement> action)
    {
        if (end > start) ChangeLinePart(start, end, action);
    }

    /// <summary>Font size of a heading relative to the text: # 1.5×, ## 1.3×, ### 1.15×, deeper levels as the text.</summary>
    public static double HeadingScale(int level) => level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 };
}


/// <summary>
/// Task boxes (<c>[ ]</c>/<c>[x]</c> drawn as ☐/☑, clicking toggles them) and image previews (drawn in place
/// of the hidden <c>](path)</c>). Images are loaded from local files only, never from the network.
/// </summary>
internal sealed class WidgetGenerator(MarkdownView view) : VisualLineElementGenerator
{
    private readonly record struct Spec(int Offset, int Length, int LineNumber, bool Task, bool Checked, string? Path);

    private readonly List<Spec> _specs = [];

    /// <summary>Largest preview, in device-independent pixels.</summary>
    public const double MaxImageHeight = 200, MaxImageWidth = 480;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _specs.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        var render = view.RenderFor(line);
        if (render.TaskColumn >= 0 && render.TaskColumn + 3 <= line.Length)
            _specs.Add(new Spec(line.Offset + render.TaskStart, render.TaskColumn + 3 - render.TaskStart, line.LineNumber, true, render.TaskChecked, null));
        // An image preview (one visual column) takes only the last character of the hidden "](path)"; the
        // characters before it are hidden markup. Elements of one column must cover one character (see WholeElement).
        if (!view.PreviewsImagesOn(line)) return;
        foreach (var image in render.Images)
            if (image.AnchorStart + image.AnchorLength <= line.Length)
                _specs.Add(new Spec(line.Offset + image.AnchorStart + image.AnchorLength - 1, 1, line.LineNumber, false, false, image.Path));
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        foreach (var s in _specs)
            if (s.Offset >= startOffset) return s.Offset;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var s in _specs)
        {
            if (s.Offset != offset) continue;
            if (s.Task) return new TaskBoxElement(s.Length, s.Checked, view.Palette);
            if (ImageCache.Load(view.BaseDirectory, s.Path!) is not { } bitmap) return null; // the hidden generator hides the tail
            var image = new Image
            {
                Source = bitmap,
                MaxHeight = MaxImageHeight,
                MaxWidth = MaxImageWidth,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Margin = new Thickness(4, 2),
            };
            ToolTip.SetTip(image, $"{s.Path} ({bitmap.PixelSize.Width}×{bitmap.PixelSize.Height})");
            return new InlineObjectElement(s.Length, image);
        }
        return null;
    }

}

/// <summary>
/// A task box (replacing <c>- [ ]</c>): one caret stop, as wide as the characters it replaces and as high as
/// a line, so the line keeps its height and the text stays aligned. Clicks are handled by <see cref="MarkdownView"/>.
/// </summary>
public sealed class TaskBoxElement(int documentLength, bool isChecked, EditorPalette palette) : WholeElement(documentLength)
{
    public bool IsChecked { get; } = isChecked;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var tv = context.TextView;
        return new TaskBoxRun(TextRunProperties, tv.WideSpaceWidth * DocumentLength, tv.DefaultLineHeight,
            TextRunProperties.FontRenderingEmSize, IsChecked, palette, RunLength(startVisualColumn));
    }
}

internal sealed class TaskBoxRun(TextRunProperties properties, double width, double height, double fontSize, bool isChecked, EditorPalette palette, int length) : DrawableTextRun
{
    public override int Length { get; } = length;
    public override TextRunProperties Properties { get; } = properties;
    public override Size Size => new(width, height);
    public override double Baseline => height * 0.8;

    public override void Draw(DrawingContext context, Point origin)
    {
        double side = Math.Round(fontSize * 0.8);
        double x = Math.Round(origin.X + (width - side) / 2), y = Math.Round(origin.Y + (height - side) / 2);
        var rect = new Rect(x + 0.5, y + 0.5, side - 1, side - 1);
        context.DrawRectangle(isChecked ? palette.Accent : null, new Pen(isChecked ? palette.Accent : palette.Dim, 1.2), rect, 2, 2);
        if (!isChecked) return;
        var tick = new StreamGeometry();
        using (var g = tick.Open())
        {
            g.BeginFigure(new Point(rect.X + side * 0.22, rect.Y + side * 0.52), false);
            g.LineTo(new Point(rect.X + side * 0.42, rect.Y + side * 0.72));
            g.LineTo(new Point(rect.X + side * 0.78, rect.Y + side * 0.3));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(palette.Background, Math.Max(1.5, side * 0.14), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), tick);
    }
}

/// <summary>Decoded image previews, keyed by full path and refreshed when the file changes.</summary>
internal static class ImageCache
{
    private static readonly Dictionary<string, (DateTime Stamp, Bitmap? Bitmap)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    public const long MaxFileSize = 32 * 1024 * 1024;

    public static string? Resolve(string? baseDirectory, string path)
    {
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal) && !path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return null; // remote images are never fetched
        try
        {
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
            else path = Uri.UnescapeDataString(path);
            if (!Path.IsPathRooted(path))
            {
                if (baseDirectory is null) return null;
                path = Path.Combine(baseDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            }
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static Bitmap? Load(string? baseDirectory, string path)
    {
        if (Resolve(baseDirectory, path) is not { } full || !Extensions.Contains(Path.GetExtension(full).ToLowerInvariant())) return null;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxFileSize) return null;
            if (Cache.TryGetValue(full, out var hit) && hit.Stamp == info.LastWriteTimeUtc) return hit.Bitmap;
            Bitmap? bitmap;
            try
            {
                using var stream = File.OpenRead(full);
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { bitmap = null; } // not an image we can decode
            Cache[full] = (info.LastWriteTimeUtc, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}


/// <summary>Column widths of a table drawn as a grid, for a given available width.</summary>
internal sealed record TableGridLayout(int[] Widths, int Available);

/// <summary>
/// Word wrap on: every table row is replaced by one element that draws the row as a grid with wrapped cells,
/// so the table stays a regular table at any window width. The source is not changed; cells are edited in a
/// small editor (double click or F2).
/// </summary>
internal sealed class TableGridGenerator(MarkdownView view) : VisualLineElementGenerator
{
    private readonly Dictionary<int, TableGridLayout> _layouts = [];
    private MarkdownDocument? _model;
    private int _available;
    private int _lineOffset = -1, _lineLength;
    private MarkdownTable? _table;
    private TableRow? _row;
    private DocumentLine? _line;

    public TableGridLayout Layout(MarkdownTable table)
    {
        var tv = view.Editor.TextArea.TextView;
        int available = Math.Max(20, (int)((WrapWidth.Of(tv) - 12) / Math.Max(1, tv.WideSpaceWidth)));
        if (!ReferenceEquals(_model, view.Model) || available != _available)
        {
            _layouts.Clear();
            _model = view.Model;
            _available = available;
        }
        if (!_layouts.TryGetValue(table.FirstLine, out var layout))
        {
            var columns = TableWrap.ColumnTexts(table, view.Source, view.Model.GetLine);
            _layouts[table.FirstLine] = layout = new TableGridLayout(TableWrap.ColumnWidths(columns, available), available);
        }
        return layout;
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _lineOffset = -1;
        if (!view.TablesAsGrid) return;
        var line = context.VisualLine.FirstDocumentLine;
        if (line.Length == 0) return;
        if (view.Model.TableAtLine(line.LineNumber - 1) is not { } table || table.RowAtLine(line.LineNumber - 1) is not { } row) return;
        _lineOffset = line.Offset;
        _lineLength = line.Length;
        _table = table;
        _row = row;
        _line = line;
    }

    public override int GetFirstInterestedOffset(int startOffset) => _lineOffset >= startOffset ? _lineOffset : -1;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (offset != _lineOffset || _table is null || _row is null || _line is null) return null;
        var layout = Layout(_table);
        string text = CurrentContext.Document.GetText(_line.Offset, _line.Length);
        var render = view.RenderFor(_line);
        var cells = new List<GridCell>();
        var aligns = new List<ColumnAlignment>();
        for (int c = 0; c < layout.Widths.Length; c++)
        {
            var (cellText, styles) = c < _row.Cells.Count && _row.Kind != MdLineKind.TableSeparator
                ? TableWrap.StyledCellText(text, _row.Cells[c], render) : ("", []);
            if (view.WordTransform is { } transform) (cellText, styles) = CyrillicViewGenerator.ConvertStyled(cellText, styles, transform);
            cells.Add(new GridCell(cellText, styles, TableWrap.WrapRanges(cellText, layout.Widths[c])));
            aligns.Add(c < _table.Alignments.Count ? _table.Alignments[c] : ColumnAlignment.None);
        }
        bool first = _row.LineIndex == _table.FirstLine, last = _row.LineIndex == _table.LastLine;
        return new TableRowElement(_lineLength, layout, cells, aligns, _row.Kind, first, last, view.Palette, view.CurrentCellOf(_line));
    }
}


/// <summary>One wrapped line of a cell: its text and the Markdown style of every character.</summary>
internal sealed record StyledLine(string Text, MdStyle[] Styles);

/// <summary>A cell of a grid row: its shown text, the style of every character, and its wrapped lines (ranges of the text).</summary>
internal sealed record GridCell(string Text, MdStyle[] Styles, List<(int Start, int Length)> Ranges)
{
    public List<StyledLine> Lines => Ranges.Select(r => new StyledLine(Text.Substring(r.Start, r.Length), Styles[r.Start..(r.Start + r.Length)])).ToList();
}

internal sealed class TableRowElement(int documentLength, TableGridLayout layout, List<GridCell> cells,
    List<ColumnAlignment> aligns, MdLineKind kind, bool first, bool last, EditorPalette palette, int? currentCell = null) : VisualLineElement(1, documentLength)
{
    /// <summary>The cell the caret is in (highlighted), or null.</summary>
    public int? CurrentCell { get; } = currentCell;

    private List<BoxLines.Line>? _lines;

    public TableGridLayout Layout { get; } = layout;

    public IReadOnlyList<GridCell> Cells => cells;

    public MdLineKind Kind => kind;

    /// <summary>The drawn lines (borders and text lines).</summary>
    public List<BoxLines.Line> Lines => _lines ??= BoxLines.For(Layout.Widths, cells.Select(c => c.Lines).ToList(), aligns, kind, first, last);

    /// <summary>Drawn lines above the first text line (the table's top border).</summary>
    private int TopLines => kind != MdLineKind.TableSeparator && first ? 1 : 0;

    /// <summary>Character column where text line <paramref name="line"/> of <paramref name="cell"/> is drawn.</summary>
    private int ColumnOf(int cell, int line) =>
        Lines[TopLines + line].Cells is { } parts && cell < parts.Count ? parts[cell].Column : TableWrap.BorderAt(Layout.Widths, cell) + 2;

    /// <summary>
    /// The cell and the position in its text at a point given in character columns and drawn line index from the
    /// element's top left; null on the separator row.
    /// </summary>
    /// <summary>
    /// The frame of a cell on the grid's own lines: the middle of its │ borders and of the +---+ lines above and below
    /// its text (the line above belongs to the row before), relative to the element's top left.
    /// </summary>
    public Rect? CellFrame(int cell, double charWidth, double lineHeight)
    {
        if (cell < 0 || cell >= Layout.Widths.Length) return null;
        int firstText = Lines.FindIndex(l => l.Cells is not null), lastText = Lines.FindLastIndex(l => l.Cells is not null);
        if (firstText < 0) return null;
        double left = (TableWrap.BorderAt(Layout.Widths, cell) + 0.5) * charWidth;
        double right = (TableWrap.BorderAt(Layout.Widths, cell + 1) + 0.5) * charWidth;
        double top = (firstText - 0.5) * lineHeight, bottom = (lastText + 1.5) * lineHeight;
        return new Rect(left, top, right - left, bottom - top);
    }

    public (int Cell, int Offset)? Hit(double column, int drawnLine)
    {
        if (kind == MdLineKind.TableSeparator || cells.Count == 0) return null;
        var widths = Layout.Widths;
        int cell = 0;
        while (cell + 1 < widths.Length && column >= TableWrap.BorderAt(widths, cell + 1)) cell++;
        var g = cells[cell];
        int line = drawnLine - TopLines;
        if (line < 0) return (cell, 0);
        if (line >= g.Ranges.Count) return (cell, g.Text.Length);
        var (start, length) = g.Ranges[line];
        double x = ColumnOf(cell, line);
        int k = 0;
        while (k < length)
        {
            int w = TextWidth.Of(g.Text.AsSpan(start + k, 1));
            if (column < x + w / 2.0) break;
            x += w;
            k++;
        }
        return (cell, start + k);
    }

    /// <summary>
    /// The character drawn at a point (character columns and drawn line from the element's top left): its cell and
    /// index in the cell's shown text; null between characters, on borders and on the separator row.
    /// </summary>
    public (int Cell, int Index)? CharAt(double column, int drawnLine)
    {
        if (kind == MdLineKind.TableSeparator || cells.Count == 0) return null;
        var widths = Layout.Widths;
        int cell = 0;
        while (cell + 1 < widths.Length && column >= TableWrap.BorderAt(widths, cell + 1)) cell++;
        if (cell >= cells.Count) return null;
        var g = cells[cell];
        int line = drawnLine - TopLines;
        if (line < 0 || line >= g.Ranges.Count) return null;
        var (start, length) = g.Ranges[line];
        double x = ColumnOf(cell, line);
        for (int k = 0; k < length; k++)
        {
            int w = TextWidth.Of(g.Text.AsSpan(start + k, 1));
            if (column >= x && column < x + w) return (cell, start + k);
            x += w;
        }
        return null;
    }

    /// <summary>Rectangles (in character columns and drawn lines) covering characters [from, to) of a cell's text.</summary>
    public IEnumerable<(int Column, int Line, int Width)> Spans(int cell, int from, int to)
    {
        if (cell >= cells.Count) yield break;
        var g = cells[cell];
        for (int l = 0; l < g.Ranges.Count; l++)
        {
            var (start, length) = g.Ranges[l];
            int a = Math.Max(from, start), b = Math.Min(to, start + length);
            if (b <= a) continue;
            int col = ColumnOf(cell, l) + TextWidth.Of(g.Text.AsSpan(start, a - start));
            yield return (col, TopLines + l, TextWidth.Of(g.Text.AsSpan(a, b - a)));
        }
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new TableRowRun(TextRunProperties, context.TextView.WideSpaceWidth, context.TextView.DefaultLineHeight, Lines, kind, palette, Layout.Widths, CurrentCell);
}

/// <summary>
/// The text lines of one table row drawn as ASCII art, like a console (grid) table:
/// <code>
/// +------+------+   top border (first row)
/// | head | head |
/// +======+======+   separator row
/// | a    | text |
/// |      | wrap |
/// +------+------+   after every row
/// </code>
/// Everything is text in the editor's monospace font on the editor's line grid, so it lines up exactly.
/// </summary>
internal static class BoxLines
{
    /// <summary>One drawn line: the text, and whether it is a border (drawn dim) or a text line (cells).</summary>
    public readonly record struct Line(string Text, bool Border, List<(int Column, StyledLine Text)>? Cells);

    public static List<Line> For(int[] widths, List<List<StyledLine>> cells, List<ColumnAlignment> aligns, MdLineKind kind, bool first, bool last)
    {
        string Rule(char left, char fill, char cross, char right) =>
            left + string.Join(cross, widths.Select(w => new string(fill, w + 2))) + right;
        var lines = new List<Line>();
        if (kind == MdLineKind.TableSeparator)
        {
            lines.Add(new Line(last ? Rule('+', '-', '+', '+') : Rule('+', '=', '+', '+'), true, null));
            return lines;
        }
        if (first) lines.Add(new Line(Rule('+', '-', '+', '+'), true, null));
        int height = Math.Max(1, cells.Max(c => c.Count));
        for (int l = 0; l < height; l++)
        {
            var parts = new List<(int, StyledLine)>();
            for (int c = 0; c < widths.Length; c++)
            {
                var text = l < cells[c].Count ? cells[c][l] : new StyledLine("", []);
                int free = Math.Max(0, widths[c] - TextWidth.Of(text.Text));
                int shift = aligns[c] switch { ColumnAlignment.Right => free, ColumnAlignment.Center => free / 2, _ => 0 };
                parts.Add((TableWrap.BorderAt(widths, c) + 2 + shift, text));
            }
            // The vertical borders as a line of its own: │ at every border position.
            var border = new char[TableWrap.GridWidth(widths)];
            Array.Fill(border, ' ');
            for (int c = 0; c <= widths.Length; c++) border[TableWrap.BorderAt(widths, c)] = '|';
            lines.Add(new Line(new string(border), false, parts));
        }
        if (kind != MdLineKind.TableHeader || last)
            lines.Add(new Line(Rule('+', '-', '+', '+'), true, null));
        return lines;
    }
}

internal sealed class TableRowRun(TextRunProperties properties, double charWidth, double lineHeight, List<BoxLines.Line> lines,
    MdLineKind kind, EditorPalette palette, int[]? widths = null, int? currentCell = null) : DrawableTextRun
{
    public override TextRunProperties Properties { get; } = properties;

    private readonly int _columns = lines.Count == 0 ? 1 : lines.Max(l => l.Text.Length);

    public override Size Size => new(_columns * charWidth, Math.Max(1, lines.Count) * lineHeight);

    public override double Baseline => lineHeight * 0.8;

    /// <summary>
    /// The shaped text, built on the first draw and kept: a row is repainted many times (scrolling, the caret, the
    /// height primer moving the view) but its text changes only when the row is built again.
    /// </summary>
    private (List<(IBrush Brush, Rect Rect)> Fills, List<(FormattedText Text, Point At, double Scale)> Texts)? _shaped;

    public override void Draw(DrawingContext context, Point origin)
    {
        var (fills, texts) = _shaped ??= Build();
        var offset = new Vector(origin.X, origin.Y);
        foreach (var (brush, rect) in fills) context.FillRectangle(brush, rect.Translate(offset));
        foreach (var (text, at, scale) in texts)
        {
            if (scale == 1) { context.DrawText(text, at + offset); continue; }
            var p = at + offset;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(p.X, p.Y)))
                context.DrawText(text, default);
        }
    }

    /// <summary>The advance of a piece of text in a face, measured once per piece, face and size.</summary>
    private static readonly Dictionary<(string Text, Typeface Face, double Size), double> Advances = [];

    private static double Advance(string text, Typeface face, double size)
    {
        if (Advances.TryGetValue((text, face, size), out double width)) return width;
        if (Advances.Count > 20_000) Advances.Clear();
        width = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, null)
            .WidthIncludingTrailingWhitespace;
        return Advances[(text, face, size)] = width;
    }

    /// <summary>
    /// A run of one style, cut where a character is drawn wider or narrower than the columns it is counted for (an
    /// emoji or a symbol from a fallback font, a non-breaking hyphen): drawn as one piece, such a character pushed the
    /// rest of the run into the next characters or over the cell's border. Each piece: its text, its first column
    /// (relative to the run) and whether it fits its columns.
    /// </summary>
    private static IEnumerable<(string Text, int Column, int Columns, bool Fits)> Pieces(string run, Typeface face, double size, double charWidth)
    {
        int start = 0, column = 0, pieceColumn = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(run);
        while (e.MoveNext())
        {
            string element = e.GetTextElement();
            int at = e.ElementIndex, columns = TextWidth.Of(element);
            // Symbols are measured (and drawn) at normal weight: never bold, see SymbolText.
            var measureFace = face.Weight != FontWeight.Normal && SymbolText.HasSymbol(element)
                ? new Typeface(face.FontFamily, face.Style, FontWeight.Normal, face.Stretch) : face;
            bool fits = element.Length == 1 && element[0] is >= ' ' and < '\u007f'
                || columns > 0 && Math.Abs(Advance(element, measureFace, size) - columns * charWidth) <= 0.5;
            if (!fits)
            {
                if (at > start) yield return (run[start..at], pieceColumn, column - pieceColumn, true);
                yield return (element, column, columns, false);
                start = at + element.Length;
                pieceColumn = column + columns;
            }
            column += columns;
        }
        if (start < run.Length) yield return (run[start..], pieceColumn, column - pieceColumn, true);
    }

    /// <summary>Shapes the row once: the frame lines and every styled run, at positions relative to the run's origin.</summary>
    private (List<(IBrush Brush, Rect Rect)> Fills, List<(FormattedText Text, Point At, double Scale)> Texts) Build()
    {
        var tf = Properties.Typeface;
        double size = Properties.FontRenderingEmSize;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // Not the run's brush: the Markdown colorizer dims the pipes, which are part of this element.
        var textBrush = kind == MdLineKind.TableHeader ? palette.Accent : palette.Foreground;
        var fills = new List<(IBrush, Rect)>();
        var texts = new List<(FormattedText, Point, double)>();
        var faces = new Dictionary<(FontStyle, FontWeight), Typeface>();
        Typeface Face(FontStyle style, FontWeight weight) =>
            faces.TryGetValue((style, weight), out var f) ? f : faces[(style, weight)] = new Typeface(tf.FontFamily, style, weight, tf.Stretch);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            double y = i * lineHeight;
            var frame = new FormattedText(line.Text, culture, FlowDirection.LeftToRight, tf, size, palette.Dim);
            double dy = (lineHeight - frame.Height) / 2;
            texts.Add((frame, new Point(0, y + dy), 1));
            if (line.Cells is null) continue;
            foreach (var (column, text) in line.Cells)
            {
                // Runs of equal style, drawn like the Markdown colorizer draws them in the text.
                int x = column, k = 0;
                while (k < text.Text.Length)
                {
                    var style = text.Styles[k];
                    int j = k + 1;
                    while (j < text.Text.Length && text.Styles[j] == style) j++;
                    string run = text.Text[k..j];
                    int width = TextWidth.Of(run);
                    bool isBold = kind == MdLineKind.TableHeader || style.HasFlag(MdStyle.Bold);
                    var face = Face(style.HasFlag(MdStyle.Italic) || style.HasFlag(MdStyle.Image) ? FontStyle.Italic : tf.Style,
                        isBold ? FontWeight.Bold : tf.Weight);
                    IBrush brush = style.HasFlag(MdStyle.Code) ? palette.CodeForeground
                        : style.HasFlag(MdStyle.Link) || style.HasFlag(MdStyle.Footnote) ? palette.Accent
                        : style.HasFlag(MdStyle.TaskDone) || style.HasFlag(MdStyle.Image) ? palette.Dim
                        : textBrush;
                    var rect = new Rect(x * charWidth, y, width * charWidth, lineHeight);
                    if (style.HasFlag(MdStyle.Code)) fills.Add((palette.CodeBackground, rect));
                    if (style.HasFlag(MdStyle.Highlight)) fills.Add((palette.Highlight, rect));
                    // Sub- and superscripts: smaller and shifted, but in their own cells, so the grid stays aligned.
                    bool sub = style.HasFlag(MdStyle.Subscript), sup = style.HasFlag(MdStyle.Superscript);
                    double shift = sup ? -lineHeight * 0.12 : sub ? lineHeight * 0.22 : 0;
                    FormattedText Shape(string s, double em)
                    {
                        // Symbols and emoji never bold (see SymbolText).
                        var pieceFace = face.Weight != FontWeight.Normal && SymbolText.HasSymbol(s) ? Face(face.Style, FontWeight.Normal) : face;
                        var ft = new FormattedText(s, culture, FlowDirection.LeftToRight, pieceFace, em, brush);
                        if (style.HasFlag(MdStyle.Strike)) ft.SetTextDecorations(TextDecorations.Strikethrough);
                        else if (style.HasFlag(MdStyle.Link)) ft.SetTextDecorations(TextDecorations.Underline);
                        return ft;
                    }
                    if (sub || sup) texts.Add((Shape(run, size * 0.75), new Point(rect.X, y + dy + shift), 1));
                    else
                        // Every character on its own columns: one drawn wider than counted is made smaller to fit them,
                        // one drawn narrower is centred on them.
                        foreach (var (piece, column0, columns, fits) in Pieces(run, face, size, charWidth))
                        {
                            var ft = Shape(piece, size);
                            double left = (x + column0) * charWidth, room = columns * charWidth, advance = ft.WidthIncludingTrailingWhitespace;
                            if (fits || advance <= 0) texts.Add((ft, new Point(left, y + dy), 1));
                            else if (advance > room)
                            {
                                double scale = Math.Max(0.3, room / advance);
                                texts.Add((ft, new Point(left, y + dy + ft.Height * (1 - scale) / 2), scale));
                            }
                            // On the text's baseline: a fallback font has its own (a non-breaking hyphen sat low, like "_").
                            else texts.Add((ft, new Point(left + (room - advance) / 2, y + dy + frame.Baseline - ft.Baseline), 1));
                        }
                    x += width;
                    k = j;
                }
            }
        }
        return (fills, texts);
    }
}





/// <summary>A horizontal rule line (<c>---</c>): its characters are replaced by one empty element; <see cref="RuleRenderer"/> draws the line.</summary>
/// <summary>
/// Outlines the current cell of a grid on the grid's own lines (above the text layer, so the borders it covers show
/// in the accent colour and nothing — text, the in-place cell editor — ever overlaps the outline).
/// </summary>
internal sealed class CurrentCellRenderer(MarkdownView view, TextView textView) : Control
{
    private (Typeface Face, double Size, double Height, double Offset)? _separatorStroke;

    public CurrentCellRenderer Attach()
    {
        IsHitTestVisible = false;
        textView.InsertLayer(this, KnownLayer.Text, LayerInsertionPosition.Above);
        textView.ScrollOffsetChanged += Redraw;
        textView.VisualLinesChanged += Redraw;
        return this;
    }

    public void Detach()
    {
        textView.ScrollOffsetChanged -= Redraw;
        textView.VisualLinesChanged -= Redraw;
        textView.Layers.Remove(this);
    }

    private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

    private double LowerSeparatorStroke(TableRowElement row, double lineHeight)
    {
        var face = row.TextRunProperties.Typeface;
        double size = row.TextRunProperties.FontRenderingEmSize;
        if (_separatorStroke is { } cached && cached.Face == face && cached.Size == size && cached.Height == lineHeight)
            return cached.Offset;

        FormattedText Measure(string glyph) => new(glyph, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, face, size, null);
        var equals = Measure("=");
        double dashHeight = Measure("-").Extent;
        // Use the bottom of the glyph's ink and the font's horizontal stroke thickness.
        // The text run centres the glyph's line box inside the grid line.
        double offset = equals.Extent > dashHeight && dashHeight > 0
            ? Math.Clamp((lineHeight + equals.Height) / 2 + equals.OverhangAfter - dashHeight / 2, 0, lineHeight)
            : lineHeight * 0.65;
        _separatorStroke = (face, size, lineHeight, offset);
        return offset;
    }

    public override void Render(DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        var pen = new Pen(view.Palette.Accent, 1.5);
        double lh = textView.DefaultLineHeight;
        static TableRowElement? Row(VisualLine? v) => v is { Elements.Count: 1 } && v.Elements[0] is TableRowElement e ? e : null;
        // The middle of drawn line `index` of a grid row (the run draws its lines one line height apart from the top).
        double Middle(VisualLine v, int index) => v.VisualTop - textView.ScrollOffset.Y + (index + 0.5) * lh;
        void Frame(int line, int cell)
        {
            var visual = textView.GetVisualLine(line);
            if (Row(visual) is not { } el || el.CellFrame(cell, textView.WideSpaceWidth, lh) is not { } frame) return;
            int firstText = el.Lines.FindIndex(l => l.Cells is not null), lastText = el.Lines.FindLastIndex(l => l.Cells is not null);
            // The +---+ lines around the text: this row's own, or the neighbouring row's (measured where it really is,
            // so the frames of neighbouring rows meet on the same line).
            double top = Middle(visual!, firstText) - lh;
            if (firstText > 0) top = Middle(visual!, firstText - 1);
            else if (textView.GetVisualLine(line - 1) is { } previous && Row(previous) is { } p)
            {
                top = Middle(previous, p.Lines.Count - 1);
                if (p.Lines[^1].Text.Contains('='))
                    top += LowerSeparatorStroke(p, lh) - lh / 2;
            }
            double bottom = lastText + 1 < el.Lines.Count ? Middle(visual!, lastText + 1)
                : textView.GetVisualLine(line + 1) is { } next && Row(next) is not null ? Middle(next, 0)
                : Middle(visual!, lastText) + lh;
            double x0 = visual!.GetVisualPosition(0, VisualYPosition.LineTop).X - textView.ScrollOffset.X;
            drawingContext.DrawRectangle(null, pen, new Rect(x0 + frame.X, top, frame.Width, bottom - top));
        }
        // Selected cells (several), then the current one; neighbours share their edges, as the grid's cells do.
        if (view.TablesAsGrid && view.GridSelection.CellBlock is { } block)
            foreach (int line in block.Lines)
                for (int cell = block.FirstCell; cell <= block.LastCell; cell++) Frame(line, cell);
        if (view.CurrentCell is { } current) Frame(current.Line, current.Cell);
    }
}

internal sealed class RuleGenerator(MarkdownView view) : VisualLineElementGenerator
{
    private int _offset = -1, _length;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        var line = context.VisualLine.FirstDocumentLine;
        bool rule = line.Length > 0 && view.RenderFor(line).Kind == MdLineKind.Rule;
        (_offset, _length) = rule ? (line.Offset, line.Length) : (-1, 0);
    }

    public override int GetFirstInterestedOffset(int startOffset) => _offset >= startOffset ? _offset : -1;

    public override VisualLineElement? ConstructElement(int offset) => offset == _offset ? new RuleElement(_length) : null;
}

/// <summary>The characters of a rule: nothing is drawn, the line keeps its height; one caret stop.</summary>
internal sealed class RuleElement(int documentLength) : WholeElement(documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new ZeroWidthRun(TextRunProperties, context.TextView.DefaultLineHeight, RunLength(startVisualColumn));
}

/// <summary>Draws every horizontal rule across the whole width of the view (in the accent colour on the caret's line).</summary>
internal sealed class RuleRenderer(MarkdownView view) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        int caretLine = view.Editor.TextArea.Caret.Line;
        foreach (var visual in textView.VisualLines)
        {
            var line = visual.FirstDocumentLine;
            if (line.Length == 0 || view.RenderFor(line).Kind != MdLineKind.Rule) continue;
            double x = visual.GetVisualPosition(0, VisualYPosition.LineTop).X - textView.ScrollOffset.X;
            double y = Math.Round(visual.VisualTop - textView.ScrollOffset.Y + visual.Height / 2) + 0.5;
            bool atCaret = line.LineNumber == caretLine;
            var pen = new Pen(atCaret ? view.Palette.Accent : view.Palette.Dim, atCaret ? 2 : 1);
            drawingContext.DrawLine(pen, new Point(Math.Max(0, x), y), new Point(textView.Bounds.Width - 6, y));
        }
    }
}

