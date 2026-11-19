using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CarbonPad.Markdown;

namespace CarbonPad.App.Editor;

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

        // Padding must come before hidden markup: both can be interested in the same offset and
        // the padding element is zero-length (see VisualLine construction rules). Widgets (task boxes,
        // image previews) replace characters that the hidden-markup generator would otherwise hide.
        var tv = editor.TextArea.TextView;
        tv.ElementGenerators.Insert(0, _widgets);
        tv.ElementGenerators.Insert(1, _padding);
        tv.ElementGenerators.Insert(2, _hidden);
        tv.LineTransformers.Add(_colorizer);
        _document.Changed += OnDocumentChanged;
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        tv.PointerHover += OnPointerHover;
        tv.PointerHoverStopped += OnPointerHoverStopped;
        tv.Redraw();
    }

    /// <summary>Directory that relative image paths and links are resolved against (the file's folder).</summary>
    public string? BaseDirectory { get; }

    internal TextEditor Editor => _editor;

    /// <summary>Ctrl+click on a link (the URL or path as written).</summary>
    public event Action<string>? LinkActivated;

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
        ModelChanged?.Invoke(this, EventArgs.Empty);
        _editor.TextArea.TextView.Redraw();
    }

    public LineRender RenderFor(DocumentLine line) => Model.GetLine(line.LineNumber - 1);

    /// <summary>AvaloniaEdit's VisualLine.LENGTH_LIMIT: longer lines get no element generators (no zero-width elements, no padding).</summary>
    public const int GeneratorLineLimit = 3000;

    public void Dispose()
    {
        _document.Changed -= OnDocumentChanged;
        var tv = _editor.TextArea.TextView;
        tv.ElementGenerators.Remove(_widgets);
        tv.ElementGenerators.Remove(_padding);
        tv.ElementGenerators.Remove(_hidden);
        _editor.TextArea.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
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
    private int _lineOffset;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _ranges.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        _lineOffset = line.Offset;
        var render = view.RenderFor(line);
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
            if (_lineOffset + r.Start == offset) return new HiddenElement(r.Length);
        return null;
    }
}

internal sealed class HiddenElement(int documentLength) : VisualLineElement(1, documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
        new ZeroWidthRun(TextRunProperties, context.TextView.DefaultLineHeight);
}

internal sealed class ZeroWidthRun(TextRunProperties properties, double height) : DrawableTextRun
{
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
        foreach (var image in render.Images)
            if (image.AnchorStart + image.AnchorLength <= line.Length)
                _specs.Add(new Spec(line.Offset + image.AnchorStart, image.AnchorLength, line.LineNumber, false, false, image.Path));
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
public sealed class TaskBoxElement(int documentLength, bool isChecked, EditorPalette palette) : VisualLineElement(1, documentLength)
{
    public bool IsChecked { get; } = isChecked;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var tv = context.TextView;
        return new TaskBoxRun(TextRunProperties, tv.WideSpaceWidth * DocumentLength, tv.DefaultLineHeight,
            TextRunProperties.FontRenderingEmSize, IsChecked, palette);
    }
}

internal sealed class TaskBoxRun(TextRunProperties properties, double width, double height, double fontSize, bool isChecked, EditorPalette palette) : DrawableTextRun
{
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
