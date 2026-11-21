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
    private readonly LineHeightPrimer _primer;
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

        tv.LineTransformers.Add(_colorizer);
        _document.Changed += OnDocumentChanged;
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        tv.PointerHover += OnPointerHover;
        tv.PointerHoverStopped += OnPointerHoverStopped;
        tv.Redraw();

        // The real heights of tall lines (tables as grids, images…), so the scrollbar is right from the start.
        _primer = new LineHeightPrimer(editor, this);
        tv.PropertyChanged += OnTextViewPropertyChanged;
        editor.PropertyChanged += OnEditorPropertyChanged;
        _primer.All();
    }

    internal TextDocument Document => _document;

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
    }

    /// <summary>
    /// Typing over a selection keeps the style markers whole; typing right after a styled word extends the style;
    /// afterwards neighbouring spans of the same style are joined.
    /// </summary>
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _editor.IsReadOnly) return;
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
    public event Action<int, int>? CellEditRequested;

    /// <summary>Tables are drawn as grids with wrapping cells (the editor wraps lines).</summary>
    public bool TablesAsGrid => _editor.WordWrap;

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
    private (int Line, int Cell)? GridCellAt(PointerEventArgs e)
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
        return (visual.FirstDocumentLine.LineNumber, cell);
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
        if (_editor.GetPositionFromPoint(e.GetPosition(_editor)) is not { } pos) return null;
        var line = _document.GetLineByNumber(pos.Line);
        int col = _document.GetOffset(pos.Location) - line.Offset;
        foreach (var s in RenderFor(line).Styles)
            if (s.Url is not null && col >= s.Start && col <= s.End && (s.Style & (MdStyle.Link | MdStyle.Footnote | MdStyle.Image)) != 0)
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
            _editor.TextArea.Caret.Offset = _document.GetLineByNumber(cell.Line).Offset;
            CellEditRequested?.Invoke(cell.Line, cell.Cell);
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

    public MarkdownDocument Model { get; private set; }

    public event EventHandler? ModelChanged;

    public EditorPalette Palette
    {
        get => _colorizer.Palette;
        set { _colorizer.Palette = value; _editor.TextArea.TextView.Redraw(); }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        // Markdown files are small enough to reparse on every change; this keeps rendering exact.
        Model = MarkdownDocument.Parse(Source);
        ClearPendingStyle();
        ModelChanged?.Invoke(this, EventArgs.Empty);
        _editor.TextArea.TextView.Redraw();
        // An edited table can change the column widths, and so the height of rows out of view.
        if (TablesAsGrid && Model.TableAtLine(_document.GetLineByOffset(Math.Min(e.Offset, _document.TextLength)).LineNumber - 1) is { } table)
            _primer.Lines(table.FirstLine + 1, table.LastLine + 1);
    }

    public LineRender RenderFor(DocumentLine line) => Model.GetLine(line.LineNumber - 1);

    /// <summary>AvaloniaEdit's VisualLine.LENGTH_LIMIT: longer lines get no element generators (no zero-width elements, no padding).</summary>
    public const int GeneratorLineLimit = 3000;

    public void Dispose()
    {
        _disposed = true;
        _document.Changed -= OnDocumentChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        var tv = _editor.TextArea.TextView;
        _primer.Dispose();
        tv.PropertyChanged -= OnTextViewPropertyChanged;
        _editor.PropertyChanged -= OnEditorPropertyChanged;
        tv.ElementGenerators.Remove(_grid);

        tv.ElementGenerators.Remove(_widgets);
        tv.ElementGenerators.Remove(_padding);
        tv.ElementGenerators.Remove(_hidden);
        _editor.TextArea.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _editor.TextArea.TextEntering -= OnTextEntering;
        _editor.TextArea.TextEntered -= OnTextEntered;
        tv.PointerHover -= OnPointerHover;
        tv.PointerHoverStopped -= OnPointerHoverStopped;
        tv.LineTransformers.Remove(_colorizer);
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
/// on those lines <see cref="MarkdownColorizer"/> collapses the markup instead.
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
        _images = render.Images;
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
            });
        }

        if (line.Length > MarkdownView.GeneratorLineLimit)
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
        int available = Math.Max(20, (int)((tv.Bounds.Width - 12) / Math.Max(1, tv.WideSpaceWidth)));
        // The end-of-line mark (Show all characters) comes after the grid, on the same line: leave room for it, or
        // it wraps onto a line of its own below every row.
        if (tv.Options.ShowEndOfLine) available = Math.Max(20, available - 3);
        if (!ReferenceEquals(_model, view.Model) || available != _available)
        {
            _layouts.Clear();
            _model = view.Model;
            _available = available;
        }
        if (!_layouts.TryGetValue(table.FirstLine, out var layout))
        {
            var natural = TableWrap.NaturalWidths(table, view.Source, view.Model.GetLine);
            _layouts[table.FirstLine] = layout = new TableGridLayout(TableWrap.ColumnWidths(natural, available), available);
        }
        return layout;
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _lineOffset = -1;
        if (!view.TablesAsGrid) return;
        var line = context.VisualLine.FirstDocumentLine;
        if (line.Length == 0 || line.Length > MarkdownView.GeneratorLineLimit) return;
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
        var cells = new List<List<StyledLine>>();
        var aligns = new List<ColumnAlignment>();
        for (int c = 0; c < layout.Widths.Length; c++)
        {
            var (cellText, styles) = c < _row.Cells.Count && _row.Kind != MdLineKind.TableSeparator
                ? TableWrap.StyledCellText(text, _row.Cells[c], render) : ("", []);
            cells.Add(TableWrap.WrapRanges(cellText, layout.Widths[c])
                .Select(r => new StyledLine(cellText.Substring(r.Start, r.Length), styles[r.Start..(r.Start + r.Length)])).ToList());
            aligns.Add(c < _table.Alignments.Count ? _table.Alignments[c] : ColumnAlignment.None);
        }
        bool first = _row.LineIndex == _table.FirstLine, last = _row.LineIndex == _table.LastLine;
        return new TableRowElement(_lineLength, layout, cells, aligns, _row.Kind, first, last, view.Palette);
    }
}

/// <summary>One wrapped line of a cell: its text and the Markdown style of every character.</summary>
internal sealed record StyledLine(string Text, MdStyle[] Styles);

internal sealed class TableRowElement(int documentLength, TableGridLayout layout, List<List<StyledLine>> cells,
    List<ColumnAlignment> aligns, MdLineKind kind, bool first, bool last, EditorPalette palette) : VisualLineElement(1, documentLength)
{
    public TableGridLayout Layout { get; } = layout;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new TableRowRun(TextRunProperties, context.TextView.WideSpaceWidth, context.TextView.DefaultLineHeight,
            BoxLines.For(Layout.Widths, cells, aligns, kind, first, last), kind, palette);
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
    MdLineKind kind, EditorPalette palette) : DrawableTextRun
{
    public override TextRunProperties Properties { get; } = properties;

    public override Size Size => new((lines.Count == 0 ? 1 : lines.Max(l => l.Text.Length)) * charWidth, Math.Max(1, lines.Count) * lineHeight);

    public override double Baseline => lineHeight * 0.8;

    public override void Draw(DrawingContext context, Point origin)
    {
        var tf = Properties.Typeface;
        double size = Properties.FontRenderingEmSize;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var bold = new Typeface(tf.FontFamily, tf.Style, FontWeight.Bold, tf.Stretch);
        // Not the run's brush: the Markdown colorizer dims the pipes, which are part of this element.
        var textBrush = kind == MdLineKind.TableHeader ? palette.Accent : palette.Foreground;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            double y = origin.Y + i * lineHeight;
            var frame = new FormattedText(line.Text, culture, FlowDirection.LeftToRight, tf, size, palette.Dim);
            double dy = (lineHeight - frame.Height) / 2;
            context.DrawText(frame, new Point(origin.X, y + dy));
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
                    var face = new Typeface(tf.FontFamily, style.HasFlag(MdStyle.Italic) || style.HasFlag(MdStyle.Image) ? FontStyle.Italic : tf.Style,
                        isBold ? FontWeight.Bold : tf.Weight, tf.Stretch);
                    IBrush brush = style.HasFlag(MdStyle.Code) ? palette.CodeForeground
                        : style.HasFlag(MdStyle.Link) || style.HasFlag(MdStyle.Footnote) ? palette.Accent
                        : style.HasFlag(MdStyle.TaskDone) || style.HasFlag(MdStyle.Image) ? palette.Dim
                        : textBrush;
                    var rect = new Rect(origin.X + x * charWidth, y, width * charWidth, lineHeight);
                    if (style.HasFlag(MdStyle.Code)) context.FillRectangle(palette.CodeBackground, rect);
                    if (style.HasFlag(MdStyle.Highlight)) context.FillRectangle(palette.Highlight, rect);
                    var ft = new FormattedText(run, culture, FlowDirection.LeftToRight, face, size, brush);
                    if (style.HasFlag(MdStyle.Strike)) ft.SetTextDecorations(TextDecorations.Strikethrough);
                    else if (style.HasFlag(MdStyle.Link)) ft.SetTextDecorations(TextDecorations.Underline);
                    context.DrawText(ft, new Point(rect.X, y + dy));
                    x += width;
                    k = j;
                }
            }
        }
    }
}
