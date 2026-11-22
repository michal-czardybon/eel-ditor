using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>
/// Selecting the shown text of a table drawn as a grid (word wrap on), where the editor's own selection can only take
/// whole rows: dragging inside one cell selects its text, dragging over several cells selects those cells. Copy puts
/// the shown text in the clipboard (several cells: tab-separated, one line per row). The source is not changed.
/// </summary>
internal sealed class GridTextSelection : IBackgroundRenderer, IDisposable
{
    /// <summary>A point in a grid: the document line of the row, the cell and the position in the cell's shown text.</summary>
    public readonly record struct Point(int Line, int Cell, int Offset);

    private readonly MarkdownView _view;
    private bool _dragging, _movingCaret;

    public GridTextSelection(MarkdownView view)
    {
        _view = view;
        var area = view.Editor.TextArea;
        area.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        area.Caret.PositionChanged += OnCaretMoved;
        view.Document.Changed += OnDocumentChanged;
        area.TextView.BackgroundRenderers.Add(this);
    }

    public Point? Anchor { get; private set; }

    public Point? Active { get; private set; }

    /// <summary>Something is selected (not just a click).</summary>
    public bool HasSelection => Anchor is { } a && Active is { } b && a != b;

    /// <summary>Redraws the selection: the text highlight (below the text) and the cell frames (above it).</summary>
    private void Invalidate()
    {
        var tv = _view.Editor.TextArea.TextView;
        tv.InvalidateLayer(Layer);
        _view.InvalidateCellFrames();
    }

    public void Clear()
    {
        if (Anchor is null) return;
        Anchor = Active = null;
        _dragging = false;
        Invalidate();
    }

    /// <summary>Selects from <paramref name="anchor"/> to <paramref name="active"/> (tests and the mouse).</summary>
    public void Select(Point anchor, Point active)
    {
        Anchor = anchor;
        Active = active;
        Invalidate();
    }

    private TableRowElement? RowElement(VisualLine? visual) =>
        visual is { Elements.Count: 1 } && visual.Elements[0] is TableRowElement el ? el : null;

    /// <summary>The grid point under the pointer, or null when it is not over a grid row.</summary>
    private Point? HitTest(PointerEventArgs e)
    {
        if (!_view.TablesAsGrid) return null;
        var tv = _view.Editor.TextArea.TextView;
        if (!tv.VisualLinesValid) return null;
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        var visual = tv.GetVisualLineFromVisualTop(p.Y);
        if (RowElement(visual) is not { } el) return null;
        double x0 = visual!.GetVisualPosition(0, VisualYPosition.LineTop).X;
        double column = (p.X - x0) / Math.Max(1, tv.WideSpaceWidth);
        int drawn = (int)Math.Floor((p.Y - visual.VisualTop) / Math.Max(1, tv.DefaultLineHeight));
        return el.Hit(column, drawn) is { } hit ? new Point(visual.FirstDocumentLine.LineNumber, hit.Cell, hit.Offset) : null;
    }

    private MarkdownTable? TableOf(int lineNumber) => _view.Model.TableAtLine(lineNumber - 1);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(_view.Editor.TextArea).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount != 1 || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None) return;
        if (HitTest(e) is not { } hit) { Clear(); return; }
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && Anchor is { } anchor && ReferenceEquals(TableOf(anchor.Line), TableOf(hit.Line)))
            Select(anchor, hit);
        else Select(hit, hit);
        _dragging = true;
        MoveCaretInto(hit);
        _view.Editor.TextArea.Focus();
        e.Pointer.Capture(_view.Editor.TextArea);
        e.Handled = true;
    }

    /// <summary>
    /// The caret goes into the text of the cell at <paramref name="hit"/> (that cell becomes the current one); the
    /// grid selection is kept.
    /// </summary>
    private void MoveCaretInto(Point hit)
    {
        var area = _view.Editor.TextArea;
        _movingCaret = true;
        try
        {
            area.ClearSelection();
            var line = _view.Document.GetLineByNumber(hit.Line);
            int column = TableOf(hit.Line)?.RowAtLine(hit.Line - 1) is { } row && hit.Cell < row.Cells.Count
                ? row.Cells[hit.Cell].ContentStart(_view.Document.GetText(line)) : 0;
            area.Caret.Offset = line.Offset + column;
        }
        finally { _movingCaret = false; }
    }

    /// <summary>
    /// A right click on a grid row: the clicked cell becomes the current one, so the context menu's table commands act
    /// on it. A click inside the grid selection keeps it (for Copy). False when the point is not over a grid row.
    /// </summary>
    internal bool OnContextClick(PointerEventArgs e)
    {
        if (HitTest(e) is not { } hit) return false;
        if (!(HasSelection && Contains(hit))) { Clear(); MoveCaretInto(hit); }
        return true;
    }

    /// <summary>Whether a cell is inside the selected block (one cell: that cell).</summary>
    private bool Contains(Point p)
    {
        if (Anchor is not { } a || Active is not { } b) return false;
        int top = Math.Min(a.Line, b.Line), bottom = Math.Max(a.Line, b.Line);
        int left = Math.Min(a.Cell, b.Cell), right = Math.Max(a.Cell, b.Cell);
        return p.Line >= top && p.Line <= bottom && p.Cell >= left && p.Cell <= right;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Anchor is not { } anchor) return;
        if (HitTest(e) is { } hit && ReferenceEquals(TableOf(anchor.Line), TableOf(hit.Line)) && hit != Active)
            Select(anchor, hit);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Anchor is null || e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
        bool copy = (e.Key is Key.C or Key.Insert) && e.KeyModifiers == KeyModifiers.Control;
        if (!copy) Clear();
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (!_movingCaret && _keep == 0) Clear();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_keep == 0) Clear();
    }

    private int _keep;

    /// <summary>
    /// Keeps the selection while a command restyles the selected cells: the shown text and the cells stay the same
    /// (and the selection is held in cells and shown characters, not in source offsets).
    /// </summary>
    public IDisposable KeepWhileStyling()
    {
        _keep++;
        return new Release(() => { _keep--; Invalidate(); });
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>The shown text of the cells of a row (from the model, so rows out of view work too).</summary>
    private List<string> CellTexts(int lineNumber)
    {
        var line = _view.Document.GetLineByNumber(lineNumber);
        string text = _view.Document.GetText(line);
        var row = TableOf(lineNumber)?.RowAtLine(lineNumber - 1);
        if (row is null || row.Kind == MdLineKind.TableSeparator) return [];
        var render = _view.RenderFor(line);
        return row.Cells.Select(c => TableWrap.StyledCellText(text, c, render).Text).ToList();
    }

    /// <summary>Within one cell: the selected range of its text; null when the selection spans several cells.</summary>
    private (int Line, int Cell, int From, int To)? InCell =>
        Anchor is { } a && Active is { } b && a.Line == b.Line && a.Cell == b.Cell
            ? (a.Line, a.Cell, Math.Min(a.Offset, b.Offset), Math.Max(a.Offset, b.Offset)) : null;

    /// <summary>The selected cells when several are selected (rows, a column, a block), else null.</summary>
    internal (List<int> Lines, int FirstCell, int LastCell)? CellBlock => HasSelection ? Block : null;

    /// <summary>Rows (document lines, separators left out) and cells covered by a selection over several cells.</summary>
    private (List<int> Lines, int FirstCell, int LastCell)? Block
    {
        get
        {
            if (Anchor is not { } a || Active is not { } b || InCell is not null || TableOf(a.Line) is not { } table) return null;
            var lines = Enumerable.Range(Math.Min(a.Line, b.Line), Math.Abs(a.Line - b.Line) + 1)
                .Where(n => table.RowAtLine(n - 1) is { Kind: not MdLineKind.TableSeparator }).ToList();
            return (lines, Math.Min(a.Cell, b.Cell), Math.Max(a.Cell, b.Cell));
        }
    }

    /// <summary>
    /// The selection as source ranges (line number, start and end column), for commands that change the text (styles,
    /// clear formatting): part of one cell maps back through the hidden markup; several cells are their whole contents.
    /// Null when nothing is selected.
    /// </summary>
    public List<(int Line, int Start, int End)>? SourcePieces()
    {
        if (!HasSelection) return null;
        var doc = _view.Document;
        var pieces = new List<(int, int, int)>();
        if (InCell is { } c)
        {
            if (TableOf(c.Line)?.RowAtLine(c.Line - 1) is not { } row || c.Cell >= row.Cells.Count) return null;
            string text = doc.GetText(doc.GetLineByNumber(c.Line));
            var map = TableWrap.ShownToSource(text, row.Cells[c.Cell], _view.Model.GetLine(c.Line - 1));
            if (c.To <= c.From || c.From >= map.Count) return null;
            pieces.Add((c.Line, map[c.From], map[Math.Min(c.To, map.Count) - 1] + 1));
            return pieces;
        }
        if (Block is not { } block) return null;
        foreach (int n in block.Lines)
        {
            if (TableOf(n)?.RowAtLine(n - 1) is not { } row) continue;
            string text = doc.GetText(doc.GetLineByNumber(n));
            for (int i = block.FirstCell; i <= block.LastCell && i < row.Cells.Count; i++)
            {
                int cs = row.Cells[i].ContentStart(text), ce = row.Cells[i].ContentEnd(text);
                if (ce > cs) pieces.Add((n, cs, ce));
            }
        }
        return pieces;
    }

    /// <summary>The selected shown text, or null when nothing is selected.</summary>
    public string? Text
    {
        get
        {
            if (!HasSelection) return null;
            if (InCell is { } c)
            {
                var texts = CellTexts(c.Line);
                if (c.Cell >= texts.Count) return null;
                string t = texts[c.Cell];
                return t[Math.Min(c.From, t.Length)..Math.Min(c.To, t.Length)].Replace("\n", Environment.NewLine);
            }
            if (Block is not { } block) return null;
            return string.Join(Environment.NewLine, block.Lines.Select(n =>
            {
                var texts = CellTexts(n);
                return string.Join("\t", Enumerable.Range(block.FirstCell, block.LastCell - block.FirstCell + 1)
                    .Select(i => i < texts.Count ? texts[i].Replace('\n', ' ') : ""));
            }));
        }
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!HasSelection || !_view.TablesAsGrid || !textView.VisualLinesValid) return;
        double cw = textView.WideSpaceWidth, lh = textView.DefaultLineHeight;
        void Paint(int lineNumber, Func<TableRowElement, IEnumerable<(int Column, int Line, int Width)>> spans)
        {
            var visual = textView.GetVisualLine(lineNumber);
            if (RowElement(visual) is not { } el) return;
            double x0 = visual!.GetVisualPosition(0, VisualYPosition.LineTop).X - textView.ScrollOffset.X;
            double y0 = visual.VisualTop - textView.ScrollOffset.Y;
            foreach (var (column, line, width) in spans(el))
                drawingContext.FillRectangle(_view.Palette.Selection, new Rect(x0 + column * cw, y0 + line * lh, Math.Max(width, 1) * cw, lh));
        }
        if (InCell is { } c) { Paint(c.Line, el => el.Spans(c.Cell, c.From, c.To)); return; }
        // Several selected cells are outlined by CurrentCellRenderer (above the text, on the grid's lines).
    }

    public void Dispose()
    {
        var area = _view.Editor.TextArea;
        area.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        area.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        area.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        area.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        area.Caret.PositionChanged -= OnCaretMoved;
        _view.Document.Changed -= OnDocumentChanged;
        area.TextView.BackgroundRenderers.Remove(this);
    }
}
