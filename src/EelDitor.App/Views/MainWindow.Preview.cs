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
    private DispatcherTimer? _previewTimer;

    private void SetupPreview()
    {
        _preview = new HtmlPreview();
        _preview.LinkActivated += url => { if (_active is { } doc) _ = OpenLinkAsync(url, doc); };
        PreviewHost.Child = _preview;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreviewNow(); };
        Editor.TextChanged += (_, _) => SchedulePreview();
        Editor.DocumentChanged += (_, _) => SchedulePreview();
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

    private void SchedulePreview()
    {
        if (_previewTimer is null || !PreviewVisible) return;
        _previewTimer.Stop();
        _previewTimer.Start();
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
        _previewTimer?.Stop();
        var palette = Palette;
        if (_active is not { } doc) { _preview.ShowMessage(Loc.T("Preview.NoFile"), palette); return; }
        if (doc.Document.TextLength > PreviewMaxLength) { _preview.ShowMessage(Loc.T("Preview.TooLarge"), palette); return; }
        if (PreviewHtml(doc) is not { } html) { _preview.ShowMessage(Loc.T("Preview.NotAvailable"), palette); return; }
        _preview.Show(html, doc.DirectoryPath, palette, Editor.FontSize, new FontFamily(_settings.FontFamily));
    }
}
