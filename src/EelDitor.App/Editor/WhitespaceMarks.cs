using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace EelDitor.App.Editor;

/// <summary>
/// Draws the marks of "Show all characters" — · for spaces, → for tabs, ␍␊ / ␊ / ␍ at line ends — beside the
/// text, outside its layout, instead of AvaloniaEdit's ShowSpaces / ShowTabs / ShowEndOfLine. Those are elements in
/// the line, and they changed it: ␍ ␊ are missing from the editor fonts (Consolas…), so a fallback font with a taller
/// line made line endings taller; a space mark has the width of a space in the normal size, so a heading (drawn larger)
/// got narrower. Drawn here, the marks change no line height, width or wrapping. Not drawn on table rows shown as grids
/// or on hidden markup.
/// </summary>
public sealed class WhitespaceMarks : IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private bool _spaces, _lineEnds;

    public WhitespaceMarks(TextEditor editor)
    {
        _editor = editor;
        var o = editor.Options;
        o.ShowSpaces = o.ShowTabs = o.ShowEndOfLine = false;
        editor.TextArea.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>The brush of the marks (the whitespace colour).</summary>
    public IBrush Brush { get; set; } = Brushes.Gray;

    /// <summary>Marks for spaces and tabs.</summary>
    public bool Spaces
    {
        get => _spaces;
        set { if (_spaces != value) { _spaces = value; Invalidate(); } }
    }

    /// <summary>Marks for line endings.</summary>
    public bool LineEnds
    {
        get => _lineEnds;
        set { if (_lineEnds != value) { _lineEnds = value; Invalidate(); } }
    }

    private void Invalidate() => _editor.TextArea.TextView.InvalidateLayer(Layer);

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!_spaces && !_lineEnds || textView.Document is not { } doc || !textView.VisualLinesValid) return;
        var typeface = new Typeface(_editor.FontFamily);
        double size = _editor.FontSize;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        FormattedText Glyph(string text) => new(text, culture, FlowDirection.LeftToRight, typeface, size, Brush);
        FormattedText? dot = null, arrow = null;
        double viewTop = textView.ScrollOffset.Y, viewBottom = viewTop + textView.Bounds.Height;

        foreach (var visual in textView.VisualLines)
        {
            // A table row drawn as a grid replaces the whole line with one element: no characters to mark.
            if (visual.Elements.Count == 1 && visual.Elements[0] is TableRowElement) continue;
            var first = visual.FirstDocumentLine;

            if (_spaces)
            {
                foreach (var textLine in visual.TextLines)
                {
                    double top = visual.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) + visual.VisualTop;
                    if (top > viewBottom || top + textLine.Height < viewTop) continue;
                    int startColumn = visual.GetTextLineVisualStartColumn(textLine);
                    int endColumn = startColumn + textLine.Length;
                    for (int column = startColumn; column < endColumn; column++)
                    {
                        int relative = visual.GetRelativeOffset(column);
                        int offset = first.Offset + relative;
                        if (offset >= doc.TextLength) break;
                        char c = doc.GetCharAt(offset);
                        if (c is not (' ' or '\t')) continue;
                        // Only characters shown as text (not hidden markup, padding or widgets).
                        if (ElementAt(visual, relative) is not VisualLineText) continue;
                        var left = visual.GetVisualPosition(column, VisualYPosition.TextTop) - textView.ScrollOffset;
                        var right = visual.GetVisualPosition(column + 1, VisualYPosition.TextTop) - textView.ScrollOffset;
                        var bottom = visual.GetVisualPosition(column, VisualYPosition.TextBottom) - textView.ScrollOffset;
                        double width = Math.Abs(right.Y - left.Y) < 0.5 ? right.X - left.X : textView.WideSpaceWidth;
                        var glyph = c == ' ' ? dot ??= Glyph("·") : arrow ??= Glyph("→");
                        double x = c == ' ' ? left.X + (width - glyph.Width) / 2 : left.X + Math.Max(0, Math.Min(width, textView.WideSpaceWidth * 2) - glyph.Width) / 2;
                        drawingContext.DrawText(glyph, new Point(x, (left.Y + bottom.Y - glyph.Height) / 2));
                    }
                }
            }

            if (_lineEnds)
            {
                var line = visual.LastDocumentLine;
                if (line.DelimiterLength == 0) continue;
                string delimiter = doc.GetText(line.EndOffset, line.DelimiterLength);
                var mark = Glyph(delimiter switch { "\r\n" => "␍␊", "\n" => "␊", _ => "␍" });
                var end = visual.GetVisualPosition(visual.VisualLength, VisualYPosition.TextTop) - textView.ScrollOffset;
                var bottom = visual.GetVisualPosition(visual.VisualLength, VisualYPosition.TextBottom) - textView.ScrollOffset;
                // Centred on the text row, whatever the height of the font the glyphs come from.
                drawingContext.DrawText(mark, new Point(end.X + 1, (end.Y + bottom.Y - mark.Height) / 2));
            }
        }
    }

    private static VisualLineElement? ElementAt(VisualLine visual, int relativeOffset)
    {
        foreach (var e in visual.Elements)
            if (relativeOffset >= e.RelativeTextOffset && relativeOffset < e.RelativeTextOffset + e.DocumentLength) return e;
        return null;
    }
}
