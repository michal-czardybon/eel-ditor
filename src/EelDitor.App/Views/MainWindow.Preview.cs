using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

/// <summary>The HTML / Markdown preview on the right of the text.</summary>
public partial class MainWindow
{
    /// <summary>Longer documents are not previewed (building controls for them would block typing).</summary>
    internal const int PreviewMaxLength = 3_000_000;

    private HtmlPreview? _preview;
    private TextEditor? _previewEditor;
    private string? _previewDirectory;
    private bool _previewHasContent, _syncingPreviewScroll;
    private double? _previewDrivenEditorOffset;
    private int _previewRenderVersion;

    // Live updates (see SchedulePreview): a flag rather than a queue, since only the newest text matters.
    private bool _previewDirty, _previewPosted;
    private DateTime _previewNextAllowed = DateTime.MinValue;
    private DispatcherTimer? _previewWait;

    /// <summary>Shortest pause between two renderings (about a frame).</summary>
    private static readonly TimeSpan PreviewMinInterval = TimeSpan.FromMilliseconds(16);

    private void SetupPreview()
    {
        _preview = new HtmlPreview();
        _preview.LinkActivated += url => _ = OpenLinkAsync(url, _previewDirectory);
        _preview.ScrollOffsetChanged += PreviewScrolled;
        _preview.CloseRequested += () => SetPreviewVisible(false);
        PreviewHost.Child = _preview;
        _previewWait = new DispatcherTimer(DispatcherPriority.Background);
        _previewWait.Tick += (_, _) => { _previewWait.Stop(); PostPreview(); };
        Editor.TextChanged += OnPreviewTextChanged;
        Editor.DocumentChanged += (_, _) => SchedulePreview();
        FollowPreviewEditor();
        // A minimized window draws nothing: the preview catches up when the window is shown again.
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized) SchedulePreview(); };
        if (_settings.PreviewVisible) SetPreviewVisible(true);
    }

    internal bool PreviewVisible => EditorArea.ColumnDefinitions[2].Width.Value > 0;

    /// <summary>The preview control (tests).</summary>
    internal HtmlPreview? Preview => _preview;

    private void TogglePreview() => SetPreviewVisible(!PreviewVisible);

    private void OnPreviewTextChanged(object? sender, EventArgs e) => SchedulePreview();

    private void FollowPreviewEditor()
    {
        var editor = _diffView?.VisibleEditor ?? Editor;
        if (ReferenceEquals(_previewEditor, editor)) return;
        if (_previewEditor is not null) _previewEditor.TextArea.TextView.ScrollOffsetChanged -= PreviewEditorScrolled;
        _previewEditor = editor;
        _previewDrivenEditorOffset = null;
        editor.TextArea.TextView.ScrollOffsetChanged += PreviewEditorScrolled;
        Dispatcher.UIThread.Post(SyncPreviewFromEditor, DispatcherPriority.Background);
    }

    private double PreviewEditorScrollRange()
    {
        var scroll = (ILogicalScrollable)_previewEditor!.TextArea.TextView;
        return Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
    }

    private double SourceLineAtEditorOffset(double offset)
    {
        var tv = _previewEditor!.TextArea.TextView;
        var line = tv.GetDocumentLineByVisualTop(offset);
        if (line is null) return 1;
        if (_diffView is { WordMode: true } diff) return diff.NewLineOfWordsOffset(line.Offset) + 1;
        double top = tv.GetVisualTopByDocumentLine(line.LineNumber);
        double bottom = line.NextLine is { } next ? tv.GetVisualTopByDocumentLine(next.LineNumber) : tv.DocumentHeight;
        return line.LineNumber + Math.Clamp((offset - top) / Math.Max(1, bottom - top), 0, 1);
    }

    private double EditorOffsetOfSourceLine(double sourceLine)
    {
        var editor = _previewEditor!;
        var tv = editor.TextArea.TextView;
        if (_diffView is { WordMode: true } diff)
        {
            int offset = diff.WordsOffsetOfNewLine((int)sourceLine - 1);
            return tv.GetVisualTopByDocumentLine(editor.Document.GetLineByOffset(offset).LineNumber);
        }
        int line = Math.Clamp((int)sourceLine, 1, editor.Document.LineCount);
        double top = tv.GetVisualTopByDocumentLine(line);
        double bottom = line < editor.Document.LineCount ? tv.GetVisualTopByDocumentLine(line + 1) : tv.DocumentHeight;
        return top + (sourceLine - (int)sourceLine) * (bottom - top);
    }

    private void PreviewEditorScrolled(object? sender, EventArgs e)
    {
        if (_previewDrivenEditorOffset is double expected)
        {
            _previewDrivenEditorOffset = null;
            if (_previewEditor is not null && Math.Abs(_previewEditor.TextArea.TextView.VerticalOffset - expected) < 1) return;
        }
        SyncPreviewFromEditor();
    }

    private void SyncPreviewFromEditor()
    {
        if (!PreviewVisible || !_previewHasContent || _syncingPreviewScroll || _previewEditor is null || _preview is null) return;
        double range = PreviewEditorScrollRange();
        if (range <= 0 || _preview.ScrollRange <= 0) return;
        double editorOffset = _previewEditor.TextArea.TextView.VerticalOffset;
        double offset = editorOffset < 0.5 ? 0 : editorOffset >= range - 0.5 ? _preview.ScrollRange
            : _preview.OffsetOfSourceLine(SourceLineAtEditorOffset(editorOffset)) ?? editorOffset / range * _preview.ScrollRange;
        _syncingPreviewScroll = true;
        try { _preview.VerticalOffset = offset; }
        finally { _syncingPreviewScroll = false; }
    }

    private void PreviewScrolled()
    {
        if (!PreviewVisible || !_previewHasContent || _syncingPreviewScroll || _previewEditor is null || _preview is null) return;
        double range = PreviewEditorScrollRange();
        if (range <= 0 || _preview.ScrollRange <= 0) return;
        double previewOffset = _preview.VerticalOffset;
        double offset = previewOffset < 0.5 ? 0 : previewOffset >= _preview.ScrollRange - 0.5 ? range
            : _preview.SourceLineAtOffset(previewOffset) is double line ? EditorOffsetOfSourceLine(line)
            : previewOffset / _preview.ScrollRange * range;
        offset = Math.Clamp(offset, 0, range);
        if (Math.Abs(_previewEditor.TextArea.TextView.VerticalOffset - offset) < 0.5) return;
        _previewDrivenEditorOffset = offset;
        _syncingPreviewScroll = true;
        try { _previewEditor.SetVerticalOffset(offset); }
        finally { _syncingPreviewScroll = false; }
    }

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
            _previewHasContent = false;
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
        : IsHtmlPath(doc.SyntaxPath);

    private static bool IsHtmlPath(string? path) =>
        Path.GetExtension(path)?.ToLowerInvariant() is ".html" or ".htm" or ".xhtml";

    /// <summary>The HTML to preview for a document, or null when it is neither HTML nor Markdown.</summary>
    internal static string? PreviewHtml(EditorDocument doc) =>
        IsHtml(doc) ? doc.Document.Text : doc.IsMarkdown ? MarkdownHtml.Body(doc.Document.Text) : null;

    internal void UpdatePreviewNow()
    {
        if (_preview is null || !PreviewVisible) return;
        _previewDirty = false;
        _previewHasContent = false;
        int version = ++_previewRenderVersion;
        var palette = Palette;
        var source = _diffView?.RightEditor.Document ?? _active?.Document;
        if (source is null) { _preview.ShowMessage(Loc.T("Preview.NoFile"), palette); return; }
        if (source.TextLength > PreviewMaxLength) { _preview.ShowMessage(Loc.T("Preview.TooLarge"), palette); return; }
        var doc = _diffView is null ? _active : _files.Documents.FirstOrDefault(d => ReferenceEquals(d.Document, source));
        bool rawHtml = doc is not null ? IsHtml(doc) : IsHtmlPath(_diffPath);
        bool markdown = doc?.IsMarkdown ?? EditorDocument.IsMarkdownPath(_diffPath);
        if (!rawHtml && !markdown) { _preview.ShowMessage(Loc.T("Preview.NotAvailable"), palette); return; }
        string html = rawHtml ? source.Text : MarkdownHtml.Body(source.Text, trackSourceLines: true);
        _previewDirectory = doc?.DirectoryPath ?? Path.GetDirectoryName(_diffPath);
        _preview.Show(html, _previewDirectory, palette, Editor.FontSize, new FontFamily(_settings.FontFamily),
            element => rawHtml ? source.GetLineByOffset(element.SourceOffset).LineNumber
                : int.TryParse(element.Attr("data-source-line"), out int line) ? line : null,
            source.LineCount);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _previewRenderVersion || !PreviewVisible) return;
            _previewHasContent = true;
            SyncPreviewFromEditor();
        }, DispatcherPriority.Background);
    }
}
