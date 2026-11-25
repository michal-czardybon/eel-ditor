using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.App.Editor;

namespace EelDitor.App.Views;

/// <summary>
/// Miniature view of the current document (like Notepad++ "Document Map"). Shares the editor's document,
/// shows the visible part of the editor as a rectangle, and scrolls the editor on click and drag.
/// Only the lines on screen are rendered, so it is cheap even for big files.
/// </summary>
public sealed class DocumentMap : Border
{
    private const double MapFontSize = 2.6;
    private readonly TextEditor _map;
    private readonly TextEditor _home;
    private TextEditor _editor;
    private readonly ViewportRenderer _viewport;
    private readonly ChangeRenderer _changes;
    private bool _dragging;

    /// <summary>
    /// While comparing: the changed lines of the followed editor's document (first line from 0, line count — 0 for lines
    /// removed there — and the kind), drawn on the map; null otherwise.
    /// </summary>
    public Func<IEnumerable<(int Line, int Count, Core.Diff.DiffKind Kind)>>? ChangedLines { get; private set; }

    /// <summary>
    /// The map follows another editor (a comparison's side or word view) and marks its changed lines; null: back to
    /// the main editor, no marks.
    /// </summary>
    public void Follow(TextEditor? editor, Func<IEnumerable<(int Line, int Count, Core.Diff.DiffKind Kind)>>? changedLines = null)
    {
        Unhook(_editor);
        _editor = editor ?? _home;
        ChangedLines = editor is null ? null : changedLines;
        Hook(_editor);
        SetDocument(_editor.Document);
    }

    /// <summary>The changes were computed again: the marks are redrawn.</summary>
    public void RefreshChanges() => _map.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

    private void OnEditorScrolled(object? sender, EventArgs e) => Sync();

    private void OnEditorDocumentChanged(object? sender, EventArgs e) => SetDocument(_editor.Document);

    private void Hook(TextEditor editor)
    {
        editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolled;
        // Heights in the editor change as lines are measured (tables, wrapped paragraphs): the frame follows.
        editor.TextArea.TextView.VisualLinesChanged += OnEditorScrolled;
        editor.DocumentChanged += OnEditorDocumentChanged;
    }

    private void Unhook(TextEditor editor)
    {
        editor.TextArea.TextView.ScrollOffsetChanged -= OnEditorScrolled;
        editor.TextArea.TextView.VisualLinesChanged -= OnEditorScrolled;
        editor.DocumentChanged -= OnEditorDocumentChanged;
    }

    public DocumentMap(TextEditor editor)
    {
        _editor = _home = editor;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        _map = new TextEditor
        {
            FontSize = MapFontSize,
            FontFamily = editor.FontFamily,
            ShowLineNumbers = false,
            IsReadOnly = true,
            IsHitTestVisible = false,
            Focusable = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            WordWrap = false,
            Padding = new Thickness(2, 0, 0, 0),
        };
        _map.Options.EnableHyperlinks = false;
        _map.Options.HighlightCurrentLine = false;
        _viewport = new ViewportRenderer(this);
        _changes = new ChangeRenderer(this);
        // The changes over the frame of the visible part, so a change on screen is still marked.
        _map.TextArea.TextView.BackgroundRenderers.Add(_viewport);
        _map.TextArea.TextView.BackgroundRenderers.Add(_changes);
        _map.TextArea.Caret.CaretBrush = Brushes.Transparent;
        Child = _map;

        Hook(editor);
        SizeChanged += (_, _) => Sync();
        SetDocument(editor.Document);
    }

    public void SetDocument(TextDocument? document)
    {
        _map.Document = document ?? new TextDocument();
        Sync();
    }

    public void ApplyPalette(EditorPalette palette)
    {
        Background = palette.Background;
        _map.Background = palette.Background;
        _map.Foreground = palette.LineNumbers;
        _viewport.Brush = palette.Selection;
        _changes.Palette = palette;
        _map.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>
    /// A point of the document in the other view: the line at <paramref name="y"/> (document coordinates of
    /// <paramref name="from"/>) and how far down it, at the same place of that line in <paramref name="to"/>. Lines have
    /// different heights in the two views (wrapped paragraphs and tables as grids in the editor, one thin line each in
    /// the map), so the mapping goes line by line; a single default line height for both put the frame in the wrong
    /// place and kept the end of the document out of reach.
    /// </summary>
    private static double MapY(TextView from, TextView to, double y)
    {
        var doc = from.Document;
        if (doc is null || doc.LineCount == 0) return 0;
        y = Math.Clamp(y, 0, Math.Max(0, from.DocumentHeight));
        var line = from.GetDocumentLineByVisualTop(y) ?? doc.GetLineByNumber(doc.LineCount);
        double top = from.GetVisualTopByDocumentLine(line.LineNumber);
        double bottom = line.LineNumber < doc.LineCount ? from.GetVisualTopByDocumentLine(line.LineNumber + 1) : from.DocumentHeight;
        double fraction = bottom > top ? Math.Clamp((y - top) / (bottom - top), 0, 1) : 0;
        double toTop = to.GetVisualTopByDocumentLine(line.LineNumber);
        double toBottom = line.LineNumber < doc.LineCount ? to.GetVisualTopByDocumentLine(line.LineNumber + 1) : to.DocumentHeight;
        return toTop + fraction * Math.Max(0, toBottom - toTop);
    }

    private TextView EditorView => _editor.TextArea.TextView;

    private TextView MapView => _map.TextArea.TextView;

    /// <summary>The part of the document visible in the editor, in the map's document coordinates.</summary>
    internal (double Top, double Height) Frame()
    {
        var etv = EditorView;
        if (_editor.Document is null || !ReferenceEquals(_editor.Document, _map.Document)) return (0, 0);
        double top = MapY(etv, MapView, etv.VerticalOffset);
        double bottom = MapY(etv, MapView, etv.VerticalOffset + etv.Bounds.Height);
        return (top, Math.Max(bottom - top, 2 * MapView.DefaultLineHeight));
    }

    /// <summary>How far the editor is scrolled, from 0 (top) to 1 (bottom).</summary>
    private double EditorFraction()
    {
        var etv = EditorView;
        double range = etv.DocumentHeight - etv.Bounds.Height;
        return range > 0 ? Math.Clamp(etv.VerticalOffset / range, 0, 1) : 0;
    }

    /// <summary>
    /// Scrolls the map with the editor, like a scrollbar: the frame is at the top of the map when the editor shows the
    /// start of the document and at the bottom when it shows the end, always inside the map.
    /// </summary>
    private void Sync()
    {
        if (_map.Document is null) return;
        var mtv = MapView;
        double viewport = Bounds.Height, extent = mtv.DocumentHeight;
        double offset = 0;
        if (extent > viewport && viewport > 0)
        {
            var (top, height) = Frame();
            offset = Math.Clamp(top - EditorFraction() * (viewport - height), 0, extent - viewport);
        }
        _map.SetVerticalOffset(offset);
        mtv.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>Where the pointer holds the frame (from its top) while dragging.</summary>
    private double _grab;

    /// <summary>
    /// Scrolls the editor so the frame's top is at <paramref name="frameTop"/> in the map's own coordinates (from the
    /// top of the visible map): proportionally when the map scrolls too, so dragging reaches both ends; directly when
    /// the whole document fits in the map.
    /// </summary>
    private void MoveFrameTo(double frameTop)
    {
        var etv = EditorView;
        var mtv = MapView;
        double viewport = Bounds.Height;
        if (mtv.DocumentHeight > viewport)
        {
            double travel = viewport - Frame().Height;
            double fraction = travel > 0 ? Math.Clamp(frameTop / travel, 0, 1) : 0;
            _editor.SetVerticalOffset(fraction * Math.Max(0, etv.DocumentHeight - etv.Bounds.Height));
        }
        else _editor.SetVerticalOffset(Math.Max(0, MapY(mtv, etv, frameTop)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _editor.Document is null) return;
        _dragging = true;
        e.Pointer.Capture(this);
        double y = e.GetPosition(this).Y;
        var (top, height) = Frame();
        double screenTop = top - MapView.VerticalOffset;
        if (y >= screenTop && y <= screenTop + height) _grab = y - screenTop;
        else
        {
            // Outside the frame: that place comes to the middle of the editor, and dragging goes on from there.
            var etv = EditorView;
            double target = MapY(MapView, etv, y + MapView.VerticalOffset) - etv.Bounds.Height / 2;
            _editor.SetVerticalOffset(Math.Clamp(target, 0, Math.Max(0, etv.DocumentHeight - etv.Bounds.Height)));
            _grab = height / 2;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) MoveFrameTo(e.GetPosition(this).Y - _grab);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _editor.SetVerticalOffset(Math.Max(0, _editor.VerticalOffset - e.Delta.Y * _editor.TextArea.TextView.DefaultLineHeight * 3));
        e.Handled = true;
    }

    /// <summary>
    /// While comparing: changed lines as bands across the map (added green, removed red, changed amber) with a solid
    /// strip at the left edge, so they stand out even where a line is one pixel high; removed lines as a red line.
    /// </summary>
    private sealed class ChangeRenderer(DocumentMap map) : IBackgroundRenderer
    {
        public EditorPalette? Palette { get; set; }

        public KnownLayer Layer => KnownLayer.Background;

        private static IBrush Strong(IBrush brush, byte alpha) =>
            brush is ISolidColorBrush s ? new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B)) : brush;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (map.ChangedLines?.Invoke() is not { } changes || Palette is not { } p || textView.Document is not { } doc) return;
            double width = textView.Bounds.Width, scroll = textView.VerticalOffset;
            double Top(int line) => line >= doc.LineCount ? textView.DocumentHeight : textView.GetVisualTopByDocumentLine(line + 1);
            foreach (var (line, count, kind) in changes)
            {
                var color = kind switch
                {
                    Core.Diff.DiffKind.Inserted => p.DiffAddedInline,
                    Core.Diff.DiffKind.Deleted => p.DiffRemovedInline,
                    _ => p.DiffChanged,
                };
                double top = Top(Math.Max(0, line)) - scroll;
                double height = count == 0 ? 0 : Math.Max(2, Top(line + count) - Top(line));
                if (count == 0) { dc.FillRectangle(Strong(color, 0xE0), new Rect(0, top - 1, width, 2)); continue; }
                dc.FillRectangle(Strong(color, 0x70), new Rect(0, top, width, height));
                dc.FillRectangle(Strong(color, 0xFF), new Rect(0, top, 3, height));
            }
        }
    }

    /// <summary>Paints the part of the document that is visible in the editor.</summary>
    private sealed class ViewportRenderer(DocumentMap map) : IBackgroundRenderer
    {
        public IBrush Brush { get; set; } = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (textView.Document is null) return;
            var (top, height) = map.Frame();
            if (height <= 0) return;
            dc.FillRectangle(Brush, new Rect(0, top - textView.VerticalOffset, textView.Bounds.Width, height));
        }
    }
}
