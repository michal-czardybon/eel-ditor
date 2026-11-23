using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

/// <summary>The HTML / Markdown preview on the right of the text.</summary>
public partial class MainWindow
{
    /// <summary>Longer documents are not previewed (building controls for them would block typing).</summary>
    internal const int PreviewMaxLength = 3_000_000;

    private HtmlPreview? _preview;

    // Live updates (see SchedulePreview): a flag rather than a queue, since only the newest text matters.
    private bool _previewDirty, _previewPosted;
    private DateTime _previewNextAllowed = DateTime.MinValue;
    private DispatcherTimer? _previewWait;

    /// <summary>Shortest pause between two renderings (about a frame).</summary>
    private static readonly TimeSpan PreviewMinInterval = TimeSpan.FromMilliseconds(16);

    private void SetupPreview()
    {
        _preview = new HtmlPreview();
        _preview.LinkActivated += url => { if (_active is { } doc) _ = OpenLinkAsync(url, doc); };
        _preview.CloseRequested += () => SetPreviewVisible(false);
        PreviewHost.Child = _preview;
        _previewWait = new DispatcherTimer(DispatcherPriority.Background);
        _previewWait.Tick += (_, _) => { _previewWait.Stop(); PostPreview(); };
        Editor.TextChanged += (_, _) => SchedulePreview();
        Editor.DocumentChanged += (_, _) => SchedulePreview();
        // A minimized window draws nothing: the preview catches up when the window is shown again.
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized) SchedulePreview(); };
        if (_settings.PreviewVisible) SetPreviewVisible(true);
    }

    internal bool PreviewVisible => EditorArea.ColumnDefinitions[2].Width.Value > 0;

    /// <summary>The preview control (tests).</summary>
    internal HtmlPreview? Preview => _preview;

    private void TogglePreview() => SetPreviewVisible(!PreviewVisible);

    private void SetPreviewVisible(bool visible)
    {
        var columns = EditorArea.ColumnDefinitions;
        if (visible)
        {
            double available = Math.Max(400, EditorArea.Bounds.Width);
            columns[1].Width = new GridLength(4);
            columns[2].Width = new GridLength(Math.Clamp(_settings.PreviewWidth, 200, Math.Max(200, available - 200)));
            PreviewSplitter.IsVisible = true;
            UpdatePreviewNow();
        }
        else
        {
            if (columns[2].ActualWidth > 0) _settings.PreviewWidth = columns[2].ActualWidth;
            columns[1].Width = new GridLength(0);
            columns[2].Width = new GridLength(0);
            PreviewSplitter.IsVisible = false;
        }
        _settings.PreviewVisible = visible;
        RefreshCommandStates();
    }

    /// <summary>
    /// Live preview after every change, without blocking typing or burning a core:
    /// - a change only marks the preview dirty; at most one rendering is pending, and it takes the newest text;
    /// - the rendering runs at background priority, after keyboard input and the editor's own layout;
    /// - the pause before the next rendering is twice the time the last one took (at least a frame), so a small file
    ///   follows every keystroke and a large one slows down by itself, never taking more than about a third of a core;
    /// - nothing is rendered while the preview is hidden or the window minimized.
    /// </summary>
    private void SchedulePreview()
    {
        if (_preview is null || !PreviewVisible) return;
        _previewDirty = true;
        if (_previewPosted || _previewWait?.IsEnabled == true) return;
        var wait = _previewNextAllowed - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            _previewWait!.Interval = wait;
            _previewWait.Start();
        }
        else PostPreview();
    }

    private void PostPreview()
    {
        if (_previewPosted) return;
        _previewPosted = true;
        Dispatcher.UIThread.Post(RenderPreviewIfDirty, DispatcherPriority.Background);
    }

    private void RenderPreviewIfDirty()
    {
        _previewPosted = false;
        if (!_previewDirty || !PreviewVisible || WindowState == WindowState.Minimized) return;
        var started = DateTime.UtcNow;
        UpdatePreviewNow();
        var took = DateTime.UtcNow - started;
        _previewNextAllowed = DateTime.UtcNow + (took * 2 > PreviewMinInterval ? took * 2 : PreviewMinInterval);
    }

    /// <summary>Whether a document is HTML (by extension or "Display as").</summary>
    internal static bool IsHtml(EditorDocument doc) => doc.SyntaxOverride is { } o
        ? o is "html" or "text.html.basic"
        : doc.FilePath is not null && Path.GetExtension(doc.FilePath).ToLowerInvariant() is ".html" or ".htm" or ".xhtml";

    /// <summary>The HTML to preview for a document, or null when it is neither HTML nor Markdown.</summary>
    internal static string? PreviewHtml(EditorDocument doc) =>
        IsHtml(doc) ? doc.Document.Text : doc.IsMarkdown ? MarkdownHtml.Body(doc.Document.Text) : null;

    internal void UpdatePreviewNow()
    {
        if (_preview is null || !PreviewVisible) return;
        _previewDirty = false;
        var palette = Palette;
        if (_active is not { } doc) { _preview.ShowMessage(Loc.T("Preview.NoFile"), palette); return; }
        if (doc.Document.TextLength > PreviewMaxLength) { _preview.ShowMessage(Loc.T("Preview.TooLarge"), palette); return; }
        if (PreviewHtml(doc) is not { } html) { _preview.ShowMessage(Loc.T("Preview.NotAvailable"), palette); return; }
        _preview.Show(html, doc.DirectoryPath, palette, Editor.FontSize, new FontFamily(_settings.FontFamily));
    }
}
