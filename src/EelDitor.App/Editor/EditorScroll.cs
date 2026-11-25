using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace EelDitor.App.Editor;

/// <summary>
/// Scrolling that works: in AvaloniaEdit 11.4 <c>TextEditor.ScrollToVerticalOffset</c> / <c>ScrollToHorizontalOffset</c>
/// do nothing, while setting the offset of the editor's own ScrollViewer does.
/// </summary>
public static class EditorScroll
{
    private static ScrollViewer? Viewer(TextEditor editor) =>
        editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    public static void SetVerticalOffset(this TextEditor editor, double y)
    {
        if (Viewer(editor) is { } v) v.Offset = new Vector(v.Offset.X, Math.Max(0, y));
        else editor.ScrollToVerticalOffset(y);
    }

    public static void SetHorizontalOffset(this TextEditor editor, double x)
    {
        if (Viewer(editor) is { } v) v.Offset = new Vector(Math.Max(0, x), v.Offset.Y);
        else editor.ScrollToHorizontalOffset(x);
    }

    internal static void SetScrollOffset(this TextEditor editor, Vector offset)
    {
        if (Viewer(editor) is { } v) v.Offset = offset;
        else ((ILogicalScrollable)editor.TextArea.TextView).Offset = offset;
    }

    /// <summary>
    /// Keeps the view on the text: when the text gets shorter (a wider window makes table rows and paragraphs wrap
    /// less), the editor's ScrollViewer moves its own offset back to the end, but the text view keeps the old one and
    /// shows empty space past the last line — up to a whole screen — until the user scrolls. Setting the ScrollViewer
    /// does nothing then (it already has that offset), so the text view's offset is set directly.
    /// </summary>
    public static void KeepOnText(TextEditor editor)
    {
        var tv = editor.TextArea.TextView;
        bool posted = false;
        void Check()
        {
            posted = false;
            if (tv.Bounds.Height <= 0 || !tv.VisualLinesValid) return;
            // The extent includes the intentional space below the last line when that option is on.
            var scrollable = (ILogicalScrollable)tv;
            double last = Math.Max(0, scrollable.Extent.Height - scrollable.Viewport.Height);
            if (tv.VerticalOffset <= last + 1) return;
            scrollable.Offset = new Vector(scrollable.Offset.X, last);
            if (Viewer(editor) is { } v && Math.Abs(v.Offset.Y - last) > 0.5) v.Offset = new Vector(v.Offset.X, last);
        }
        void Post(object? sender, EventArgs e)
        {
            if (posted) return;
            posted = true;
            Dispatcher.UIThread.Post(Check, DispatcherPriority.Background);
        }
        tv.VisualLinesChanged += Post;
        tv.LayoutUpdated += Post;
        tv.ScrollOffsetChanged += Post;
    }
}
