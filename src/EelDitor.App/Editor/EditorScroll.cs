using Avalonia;
using Avalonia.Controls;
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
}
