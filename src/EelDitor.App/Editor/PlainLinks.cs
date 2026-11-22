using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>
/// Web addresses in text that is not rendered as Markdown (plain text, code, Markdown in raw mode): drawn underlined
/// in the accent colour, opened by Ctrl+click. Display only; the Markdown view does the same for its own links.
/// </summary>
public sealed class PlainLinks : DocumentColorizingTransformer
{
    /// <summary>Longer lines are not searched (a minified file would be scanned on every redraw).</summary>
    private const int MaxLineLength = 20_000;

    private readonly TextEditor _editor;
    private readonly Func<bool> _active;
    private readonly Func<IBrush> _accent;

    /// <summary>Ctrl+click on an address (as it should be opened: "www." gets "https://").</summary>
    public event Action<string>? LinkActivated;

    public PlainLinks(TextEditor editor, Func<bool> active, Func<IBrush> accent)
    {
        _editor = editor;
        _active = active;
        _accent = accent;
        editor.TextArea.TextView.LineTransformers.Add(this);
        editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_active() || line.Length > MaxLineLength) return;
        string text = CurrentContext.Document.GetText(line);
        var brush = _accent();
        foreach (var url in UrlFinder.FindAll(text))
            ChangeLinePart(line.Offset + url.Start, line.Offset + url.End, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
    }

    /// <summary>The address at a document offset, or null.</summary>
    public static FoundUrl? UrlAt(TextDocument doc, int offset)
    {
        var line = doc.GetLineByOffset(offset);
        if (line.Length > MaxLineLength) return null;
        int col = offset - line.Offset;
        foreach (var url in UrlFinder.FindAll(doc.GetText(line)))
            if (col >= url.Start && col <= url.End) return url;
        return null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_active() || e.KeyModifiers != KeyModifiers.Control || !e.GetCurrentPoint(_editor.TextArea).Properties.IsLeftButtonPressed) return;
        if (_editor.GetPositionFromPoint(e.GetPosition(_editor)) is not { } pos) return;
        if (UrlAt(_editor.Document, _editor.Document.GetOffset(pos.Location)) is not { } url) return;
        LinkActivated?.Invoke(url.Url);
        e.Handled = true;
    }
}
