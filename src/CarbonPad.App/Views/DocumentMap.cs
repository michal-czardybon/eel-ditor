using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CarbonPad.App.Editor;

namespace CarbonPad.App.Views;

/// <summary>
/// Miniature view of the current document (like Notepad++ "Document Map"). Shares the editor's document,
/// shows the visible part of the editor as a rectangle, and scrolls the editor on click and drag.
/// Only the lines on screen are rendered, so it is cheap even for big files.
/// </summary>
public sealed class DocumentMap : Border
{
    private const double MapFontSize = 2.6;
    private readonly TextEditor _map;
    private readonly TextEditor _editor;
    private readonly ViewportRenderer _viewport;
    private bool _dragging;

    public DocumentMap(TextEditor editor)
    {
        _editor = editor;
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
        _map.TextArea.TextView.BackgroundRenderers.Add(_viewport);
        _map.TextArea.Caret.CaretBrush = Brushes.Transparent;
        Child = _map;

        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync();
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => _map.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        editor.DocumentChanged += (_, _) => SetDocument(editor.Document);
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
        _map.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>First and last document lines visible in the editor.</summary>
    private (int First, int Last) EditorViewport()
    {
        var tv = _editor.TextArea.TextView;
        if (!tv.VisualLinesValid || tv.VisualLines.Count == 0 || _editor.Document is null) return (1, 1);
        return (tv.VisualLines[0].FirstDocumentLine.LineNumber, tv.VisualLines[^1].LastDocumentLine.LineNumber);
    }

    /// <summary>Scrolls the map proportionally so the editor's viewport is always inside it.</summary>
    private void Sync()
    {
        var doc = _map.Document;
        if (doc is null) return;
        var mtv = _map.TextArea.TextView;
        double lineHeight = mtv.DefaultLineHeight;
        double extent = doc.LineCount * lineHeight;
        double viewport = Bounds.Height;
        var (first, _) = EditorViewport();
        var etv = _editor.TextArea.TextView;
        double editorExtent = Math.Max(1, etv.DocumentHeight - etv.Bounds.Height);
        double fraction = Math.Clamp(etv.VerticalOffset / editorExtent, 0, 1);
        double offset = extent > viewport ? fraction * (extent - viewport) : 0;
        _map.SetVerticalOffset(offset);
        mtv.InvalidateLayer(KnownLayer.Background);
    }

    private void ScrollEditorTo(Point p)
    {
        var doc = _editor.Document;
        if (doc is null) return;
        var mtv = _map.TextArea.TextView;
        int line = Math.Clamp((int)((p.Y + mtv.VerticalOffset) / mtv.DefaultLineHeight) + 1, 1, doc.LineCount);
        var etv = _editor.TextArea.TextView;
        double target = etv.GetVisualTopByDocumentLine(line) - etv.Bounds.Height / 2;
        _editor.SetVerticalOffset(Math.Max(0, target));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        ScrollEditorTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) ScrollEditorTo(e.GetPosition(this));
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

    /// <summary>Paints the part of the document that is visible in the editor.</summary>
    private sealed class ViewportRenderer(DocumentMap map) : IBackgroundRenderer
    {
        public IBrush Brush { get; set; } = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (textView.Document is null) return;
            var (first, last) = map.EditorViewport();
            double h = textView.DefaultLineHeight;
            double top = (first - 1) * h - textView.VerticalOffset;
            double height = Math.Max(h * 4, (last - first + 1) * h);
            dc.FillRectangle(Brush, new Rect(0, top, textView.Bounds.Width, height));
        }
    }
}
