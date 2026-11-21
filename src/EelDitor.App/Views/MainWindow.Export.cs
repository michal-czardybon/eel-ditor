using Avalonia.Platform.Storage;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

/// <summary>Export of the current document to HTML or PDF (Markdown formatted, other text as it is).</summary>
public partial class MainWindow
{
    /// <summary>The page of a document: Markdown formatted with the editor's parser, plain text preformatted.</summary>
    internal static string ExportHtml(EditorDocument doc)
    {
        string text = doc.Document.Text;
        string body = doc.IsMarkdown ? MarkdownHtml.Body(text) : MarkdownHtml.PlainTextBody(text);
        string? baseUri = doc.DirectoryPath is { Length: > 0 } dir ? new Uri(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar).AbsoluteUri : null;
        return MarkdownHtml.Page(Path.GetFileNameWithoutExtension(doc.FileName), body, baseUri);
    }

    private async Task ExportAsync(bool pdf)
    {
        if (_active is not { } doc) return;
        if (pdf && PdfExport.FindBrowser() is null)
        {
            await Dialogs.Info(this, Loc.T("Export.Pdf"), Loc.T("Export.NoBrowser"));
            return;
        }
        var start = doc.DirectoryPath is { Length: > 0 } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
        string extension = pdf ? ".pdf" : ".html";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(pdf ? "Export.Pdf" : "Export.Html"),
            SuggestedFileName = Path.GetFileNameWithoutExtension(doc.FileName) + extension,
            SuggestedStartLocation = start,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(pdf ? "PDF" : "HTML") { Patterns = [pdf ? "*.pdf" : "*.html"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        string html = ExportHtml(doc);
        try
        {
            if (!pdf)
            {
                await File.WriteAllTextAsync(path, html);
                SetStatusMessage(Loc.T("Export.Done", Path.GetFileName(path)));
                return;
            }
            SetStatusMessage(Loc.T("Export.Printing"));
            string? problem = await PdfExport.PrintAsync(html, path);
            if (problem is null) SetStatusMessage(Loc.T("Export.Done", Path.GetFileName(path)));
            else await Dialogs.Info(this, Loc.T("Export.Pdf"), Loc.T("Export.Failed", problem));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Info(this, Loc.T(pdf ? "Export.Pdf" : "Export.Html"), Loc.T("Export.Failed", ex.Message));
        }
    }
}
