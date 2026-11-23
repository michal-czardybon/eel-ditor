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

    /// <summary>
    /// The page to print: Markdown and plain text as in the export, an HTML file as it is (with its folder as the base
    /// for relative links and images); in both, a script opens the browser's print dialog when the page has loaded.
    /// </summary>
    internal static string PrintHtml(EditorDocument doc)
    {
        string? baseUri = doc.DirectoryPath is { Length: > 0 } dir ? new Uri(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar).AbsoluteUri : null;
        if (!IsHtml(doc))
        {
            string text = doc.Document.Text;
            string body = doc.IsMarkdown ? MarkdownHtml.Body(text) : MarkdownHtml.PlainTextBody(text);
            return MarkdownHtml.Page(Path.GetFileNameWithoutExtension(doc.FileName), body, baseUri, print: true);
        }
        string html = doc.Document.Text;
        if (baseUri is not null && html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
        {
            string tag = "<base href=\"" + System.Net.WebUtility.HtmlEncode(baseUri) + "\">";
            int head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            int close = head < 0 ? -1 : html.IndexOf('>', head);
            html = close < 0 ? tag + html : html.Insert(close + 1, tag);
        }
        int bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyEnd < 0 ? html + MarkdownHtml.PrintScript : html.Insert(bodyEnd, MarkdownHtml.PrintScript);
    }

    /// <summary>
    /// File → Print: the page goes to a temporary file opened in the default browser, which shows its print dialog
    /// (print preview, printer, margins, "Save as PDF"). Avalonia has no printing of its own.
    /// </summary>
    private void PrintInBrowser()
    {
        if (_active is not { } doc) return;
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "Eel-ditor", "print");
            Directory.CreateDirectory(folder);
            // Pages printed earlier are of no use any more.
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles("*.html").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                try { old.Delete(); } catch (IOException) { }
            string name = string.Concat(Path.GetFileNameWithoutExtension(doc.FileName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(folder, name + ".print.html");
            File.WriteAllText(path, PrintHtml(doc), new System.Text.UTF8Encoding(true));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            SetStatusMessage(Loc.T("Print.Opened"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatusMessage(Loc.T("Export.Failed", ex.Message));
        }
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
