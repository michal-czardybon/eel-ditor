using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>
/// Measures, in the background, the lines whose height differs from the default one (table rows drawn as grids,
/// image previews, styled text, long lines when wrapping). AvaloniaEdit knows a line's height only once the line
/// has been drawn and counts every other line as one default line, so in a Markdown file the document grows while
/// it is being scrolled and the scrollbar thumb falls behind the mouse. Measured in small steps (one visual line
/// each, built and thrown away), the whole height is right from the start. The first visible line stays where it
/// is while the lines above it change height.
/// </summary>
internal sealed class LineHeightPrimer
{
    private const int StepMilliseconds = 12;

    private readonly TextEditor _editor;
    private readonly MarkdownView _view;
    private readonly DispatcherTimer _start;
    private readonly List<(int From, int To)> _queue = [];
    private bool _posted, _disposed;

    public LineHeightPrimer(TextEditor editor, MarkdownView view)
    {
        _editor = editor;
        _view = view;
        // Debounced: resizing the window or loading changes the heights many times in a row.
        _start = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => { _start!.Stop(); Post(); });
    }

    /// <summary>Measures every line again (after loading, a width or font change, word wrap on or off).</summary>
    public void All()
    {
        _queue.Clear();
        _queue.Add((1, int.MaxValue));
        _start.Stop();
        _start.Start();
    }

    /// <summary>Measures these lines (1-based, inclusive) again, e.g. all rows of an edited table.</summary>
    public void Lines(int from, int to)
    {
        _queue.Add((from, to));
        _start.Stop();
        _start.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _start.Stop();
        _queue.Clear();
    }

    private void Post()
    {
        if (_posted || _disposed) return;
        _posted = true;
        Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
    }

    private void Step()
    {
        _posted = false;
        if (_disposed || _queue.Count == 0) return;
        var tv = _editor.TextArea.TextView;
        var doc = _editor.Document;
        if (!ReferenceEquals(doc, _view.Document) || !tv.VisualLinesValid || tv.Bounds.Width <= 0)
        {
            _start.Start(); // not laid out yet: try again a little later
            return;
        }
        // The first visible line and how far it is scrolled, to keep it in place.
        var top = tv.GetDocumentLineByVisualTop(tv.VerticalOffset);
        double topBefore = top is null ? 0 : tv.GetVisualTopByDocumentLine(top.LineNumber);
        double within = tv.VerticalOffset - topBefore;

        var sw = Stopwatch.StartNew();
        while (_queue.Count > 0 && sw.ElapsedMilliseconds < StepMilliseconds)
        {
            var (from, to) = _queue[0];
            to = Math.Min(to, doc.LineCount);
            int n = Math.Max(1, from);
            for (; n <= to && sw.ElapsedMilliseconds < StepMilliseconds; n++)
            {
                var line = doc.GetLineByNumber(n);
                if (NeedsMeasuring(line)) tv.GetOrConstructVisualLine(line);
            }
            if (n > to) _queue.RemoveAt(0);
            else _queue[0] = (n, to);
        }

        if (top is not null && !top.IsDeleted && Math.Abs(tv.GetVisualTopByDocumentLine(top.LineNumber) - topBefore) > 0.01)
        {
            // Lines above the view changed height: scroll by as much, so the text in view does not move.
            tv.InvalidateMeasure();
            _editor.UpdateLayout();
            _editor.SetVerticalOffset(tv.GetVisualTopByDocumentLine(top.LineNumber) + within);
        }
        else if (_queue.Count == 0)
        {
            tv.InvalidateMeasure(); // the new height reaches the scrollbar
        }
        if (_queue.Count > 0) Post();
    }

    /// <summary>A line that may be taller (or shorter) than a default line.</summary>
    private bool NeedsMeasuring(DocumentLine line)
    {
        if (line.Length == 0) return false;
        var tv = _editor.TextArea.TextView;
        if (_editor.WordWrap && line.Length * tv.WideSpaceWidth > WrapWidth.Of(tv) - 4 * tv.WideSpaceWidth) return true;
        if (_view.TablesAsGrid && _view.Model.TableAtLine(line.LineNumber - 1) is not null) return true;
        var render = _view.RenderFor(line);
        return render.Kind != MdLineKind.Normal || render.Hidden.Count > 0 || render.Styles.Count > 0;
    }
}
