using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>The HTML / Markdown preview drawn by Eel-ditor.</summary>
public class HtmlPreviewTests
{
    private static (Window Window, HtmlPreview Preview, List<string> Opened) Preview(string html, string? baseDirectory = null)
    {
        var preview = new HtmlPreview();
        var window = new Window { Width = 700, Height = 600, Content = preview };
        window.Show();
        var opened = new List<string>();
        preview.LinkActivated += opened.Add;
        preview.Show(html, baseDirectory, EditorPalette.Dark, 14, new FontFamily("Consolas"));
        Dispatcher.UIThread.RunJobs();
        return (window, preview, opened);
    }

    private static List<SelectableTextBlock> Blocks(Control root) =>
        root.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.Inlines is { Count: > 0 }).ToList();

    private static string Text(SelectableTextBlock b) => b.Inlines!.Text ?? "";

    /// <summary>The text as the layout holds it (independent of how the renderer counts positions).</summary>
    private static string LayoutText(TextLayout layout)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in layout.TextLines)
            foreach (var run in line.TextRuns)
                sb.Append(run is ShapedTextRun shaped ? shaped.Text.ToString() : new string('￼', run.Length));
        return sb.ToString();
    }

    private static void Click(Window w, Control target, Point local)
    {
        var p = target.TranslatePoint(local, w)!.Value;
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void RendersHeadingsParagraphsListsTablesAndCode()
    {
        var (w, preview, _) = Preview("""
            <!DOCTYPE html><html><head><title>T</title><style>p{color:red}</style><script>alert(1)</script></head>
            <body><h1>Tytuł</h1><p>Zwykły   tekst <b>gruby</b>
            i <i>kursywa</i>.</p>
            <ul><li>jeden<li>dwa</ul>
            <table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>
            <pre>  kod
              wcięty</pre></body></html>
            """);
        var texts = Blocks(preview.Content).Select(Text).ToList();
        Assert.Contains("Tytuł", texts);
        Assert.Contains("Zwykły tekst gruby i kursywa.", texts);
        Assert.Contains("jeden", texts);
        Assert.Contains("dwa", texts);
        Assert.Contains("A", texts);
        Assert.Contains("2", texts);
        Assert.DoesNotContain(texts, t => t.Contains("alert") || t.Contains("color:red") || t == "T");
        var code = preview.Content.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text?.Contains("kod") == true);
        Assert.Equal("  kod\n  wcięty", code.Text!.Replace("            ", ""));
        var bullets = preview.Content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "•");
        Assert.Equal(2, bullets.Count());
        w.Close();
    }

    [AvaloniaFact]
    public void ClickOnALink_AfterALineBreak_OpensIt_AndAnAnchorScrollsInstead()
    {
        var (w, preview, opened) = Preview("<p>pierwsza linia<br>druga <a href=\"https://example.org/x\">LINK</a> koniec</p><p><a href=\"#cel\">do celu</a></p>"
            + string.Concat(Enumerable.Repeat("<p>wypełnienie</p>", 60)) + "<h2 id=\"cel\">Cel</h2>");
        var block = Blocks(preview.Content).First(b => Text(b).Contains("LINK"));
        int start = Text(block).IndexOf("LINK", StringComparison.Ordinal);
        Assert.Equal('L', LayoutText(block.TextLayout)[start]); // the renderer's positions match the layout's
        var rect = block.TextLayout.HitTestTextPosition(start + 1);
        Click(w, block, rect.Center);
        Assert.Equal(["https://example.org/x"], opened);

        // A click on plain text opens nothing.
        Click(w, block, block.TextLayout.HitTestTextPosition(1).Center);
        Assert.Single(opened);

        var anchorLink = Blocks(preview.Content).First(b => Text(b) == "do celu");
        Click(w, anchorLink, anchorLink.TextLayout.HitTestTextPosition(2).Center);
        Assert.Single(opened); // "#cel" is not passed on; the page scrolls to the heading
        var scroll = preview.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroll.Offset.Y > 0);
        w.Close();
    }

    [AvaloniaFact]
    public void Images_LocalAreShown_RemoteAreNotFetched()
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.png"), Convert.FromBase64String(MarkdownWidgetTests.OnePixelPng));
        var (w, preview, _) = Preview("<p><img src=\"a.png\" alt=\"lokalny\"> <img src=\"https://example.org/b.png\" alt=\"zdalny\"></p>", dir);
        Assert.Single(preview.Content.GetVisualDescendants().OfType<Image>());
        Assert.Contains(preview.Content.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "[zdalny]");
        w.Close();
    }

    internal static (MainWindow W, EditorDocument Doc) Open(string name, string text, string? dir = null)
    {
        dir ??= TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WindowWidth = 1200 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    [AvaloniaTheory]
    [InlineData("a.html", "<h1>Nagłówek</h1><p>treść</p>", "Nagłówek")]
    [InlineData("a.md", "# Nagłówek\n\ntreść **gruba**\n", "Nagłówek")]
    public void Preview_ShowsHtmlAndMarkdown_AndFollowsTyping(string name, string text, string heading)
    {
        var (w, doc) = Open(name, text);
        Assert.False(w.PreviewVisible);
        w.RunCommand("view.preview");
        Assert.True(w.PreviewVisible);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Blocks(w.Preview!.Content), b => Text(b) == heading);

        // Live: the change shows after the next background turn of the dispatcher, without a pause in typing.
        doc.Document.Insert(doc.Document.TextLength, name.EndsWith(".md") ? "\nnowy akapit\n" : "<p>nowy akapit</p>");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Blocks(w.Preview.Content), b => Text(b) == "nowy akapit");
        // The next change, a moment later, too (the pause after a rendering is twice its time, at least a frame).
        Thread.Sleep(300);
        doc.Document.Insert(doc.Document.TextLength, name.EndsWith(".md") ? "\ndrugi akapit\n" : "<p>drugi akapit</p>");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Blocks(w.Preview.Content), b => Text(b) == "drugi akapit");

        w.RunCommand("view.preview");
        Assert.False(w.PreviewVisible);
    }

    [AvaloniaFact]
    public void Preview_ClosesWithItsCross()
    {
        var (w, _) = Open("a.md", "# A\n");
        w.RunCommand("view.preview");
        Assert.True(w.PreviewVisible);
        w.Preview!.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.False(w.PreviewVisible);
    }

    [AvaloniaTheory]
    [InlineData("a.md", "# Tytuł\n\ntekst **gruby**\n", "<h1>Tytuł</h1>")]
    [InlineData("a.txt", "zwykły <tekst>\n", "zwykły &lt;tekst&gt;")]
    [InlineData("a.html", "<html><head><title>T</title></head><body><p>akapit</p></body></html>", "<p>akapit</p>")]
    public void PrintPage_OpensThePrintDialog_AndKeepsTheContent(string name, string text, string expected)
    {
        var (_, doc) = Open(name, text);
        string page = MainWindow.PrintHtml(doc);
        Assert.Contains(expected, page);
        Assert.Contains(EelDitor.Markdown.MarkdownHtml.PrintScript, page);
        Assert.True(page.IndexOf(EelDitor.Markdown.MarkdownHtml.PrintScript, StringComparison.Ordinal) < page.LastIndexOf("</body>", StringComparison.Ordinal));
        Assert.Contains("<base href=\"file:///", page); // relative images and links resolve to the file's folder
        if (!name.EndsWith(".html")) Assert.Contains("script-src 'unsafe-inline'", page);
        Assert.DoesNotContain("window.print", MainWindow.ExportHtml(doc)); // the export stays without scripts
    }

    [AvaloniaFact]
    public void Preview_OfPlainText_SaysItIsNotAvailable()
    {
        var (w, _) = Open("a.txt", "<b>nie HTML</b>");
        w.RunCommand("view.preview");
        Dispatcher.UIThread.RunJobs();
        var texts = w.Preview!.Content.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal([Loc.T("Preview.NotAvailable")], texts);
    }
}
