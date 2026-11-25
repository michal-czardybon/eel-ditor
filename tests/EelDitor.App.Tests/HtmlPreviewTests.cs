using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
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
    public void Preview_FollowsTheRightDiffSnapshotAndItsDirectory()
    {
        var (w, _) = Open("active.md", "# Aktywny plik\n");
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.png"), Convert.FromBase64String(MarkdownWidgetTests.OnePixelPng));
        w.RunCommand("view.preview");
        w.OpenDiff("HEAD", "# Wersja przed\n", "working",
            DiffView.ReadOnlyDocument("# Wersja po\n\n![obraz](a.png)\n"), false, Path.Combine(dir, "snapshot.md"));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Blocks(w.Preview!.Content), b => Text(b) == "Wersja po");
        Assert.DoesNotContain(Blocks(w.Preview.Content), b => Text(b) == "Aktywny plik");
        Assert.Single(w.Preview.Content.GetVisualDescendants().OfType<Image>());

        w.CurrentDiffView!.WordMode = true;
        w.CurrentDiffView.RightEditor.Document.Insert(0, "# Zmieniona\n\n");
        for (int i = 0; i < 100 && !Blocks(w.Preview.Content).Any(b => Text(b) == "Zmieniona"); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Contains(Blocks(w.Preview.Content), b => Text(b) == "Zmieniona");
        Assert.DoesNotContain(Blocks(w.Preview.Content), b => Text(b) == "Wersja przed");
        w.CloseDiff();
        w.UpdatePreviewNow();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Blocks(w.Preview.Content), b => Text(b) == "Aktywny plik");
        w.Close();
    }

    [AvaloniaFact]
    public void Preview_ScrollsToTheSameSourceLineInBothDirections()
    {
        string source = string.Concat(Enumerable.Range(1, 100).Select(i =>
            i <= 40 ? $"# Sekcja {i}\n\n" : $"Sekcja {i}\n\n"));
        var (w, doc) = Open("long.md", source);
        w.RunCommand("view.preview");
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var preview = w.Preview!;
        var tv = w.Editor.TextArea.TextView;
        double PreviewTop(string text) =>
            Blocks(preview.Content).Single(block => Text(block) == text).TranslatePoint(default, preview.Content)!.Value.Y
            + preview.Content.Margin.Top;
        int Line(string text) => doc.Document.GetLineByOffset(source.IndexOf(text, StringComparison.Ordinal)).LineNumber;
        double firstOffset = tv.GetVisualTopByDocumentLine(Line("Sekcja 55"));
        w.Editor.SetVerticalOffset(firstOffset);
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(preview.VerticalOffset, PreviewTop("Sekcja 55") - 1, PreviewTop("Sekcja 55") + 1);

        preview.VerticalOffset = PreviewTop("Sekcja 28");
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(tv.VerticalOffset, tv.GetVisualTopByDocumentLine(Line("Sekcja 28")) - 1,
            tv.GetVisualTopByDocumentLine(Line("Sekcja 28")) + 1);
        var scrollable = (ILogicalScrollable)tv;
        preview.VerticalOffset = preview.ScrollRange;
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        double bottom = scrollable.Extent.Height - scrollable.Viewport.Height;
        Assert.InRange(tv.VerticalOffset, bottom - 1, bottom + 1);
        Assert.InRange(preview.VerticalOffset, preview.ScrollRange - 1, preview.ScrollRange);
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiffScroll_TracksMatchingLinesAndBothEnds_WithOrWithoutHtmlPreview(bool withPreview)
    {
        var lines = Enumerable.Range(1, 160).Select(i => $"## Sekcja {i}\n\nTreść sekcji {i}.\n\n").ToList();
        string before = string.Concat(lines);
        lines.InsertRange(19, Enumerable.Range(1, 12).Select(i => $"# Wstawiony {i}\n\n"));
        lines.RemoveRange(105, 11);
        string after = string.Concat(lines).TrimEnd();
        var (w, doc) = Open("compare.md", after);
        w.OpenDiff("HEAD", before, "working", doc.Document, true, doc.FilePath);
        if (withPreview) w.RunCommand("view.preview");
        Settle();
        var view = w.CurrentDiffView!;
        var left = view.LeftEditor;
        var right = view.RightEditor;
        var preview = w.Preview!;

        void Settle()
        {
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        static double End(TextEditor pane)
        {
            var scroll = (ILogicalScrollable)pane.TextArea.TextView;
            return scroll.Extent.Height - scroll.Viewport.Height;
        }
        static int Line(TextEditor pane, int section) =>
            pane.Document.GetLineByOffset(pane.Document.Text.IndexOf($"## Sekcja {section}\n", StringComparison.Ordinal)).LineNumber;
        static void Near(double actual, double expected) => Assert.InRange(actual, expected - 1, expected + 1);
        static double Top(TextEditor pane, int line) => pane.TextArea.TextView.GetVisualTopByDocumentLine(line);
        static double Offset(TextEditor pane) => pane.TextArea.TextView.VerticalOffset;
        void Move(TextEditor pane, double y)
        {
            pane.SetVerticalOffset(y);
            Settle();
        }
        void AtSection(int section)
        {
            Near(Offset(left), Top(left, Line(left, section)));
            Near(Offset(right), Top(right, Line(right, section)));
            if (withPreview) Near(preview.VerticalOffset, preview.OffsetOfSourceLine(Line(right, section))!.Value);
        }

        Move(left, Top(left, Line(left, 45)));
        AtSection(45);
        Move(right, Top(right, Line(right, 115)));
        AtSection(115);
        if (withPreview)
        {
            preview.VerticalOffset = preview.OffsetOfSourceLine(Line(right, 73))!.Value;
            Settle();
            AtSection(73);
        }

        view.SyncScroll = false;
        double independentRight = Offset(right);
        Move(left, Top(left, Line(left, 37)));
        Near(Offset(right), independentRight);
        view.SyncScroll = true;
        Move(left, End(left) + 1000);
        Near(Offset(left), End(left));
        Near(Offset(right), End(right));
        if (withPreview) Near(preview.VerticalOffset, preview.ScrollRange);
        Move(right, 0);
        Near(Offset(left), 0);
        Near(Offset(right), 0);
        if (withPreview) Near(preview.VerticalOffset, 0);

        view.WordMode = true;
        Settle();
        var words = view.WordsEditor;
        int line = words.Document.GetLineByOffset(view.WordsOffsetOfNewLine(Line(right, 63) - 1)).LineNumber;
        Move(words, Top(words, line));
        if (withPreview)
        {
            Near(preview.VerticalOffset, preview.OffsetOfSourceLine(Line(right, 63))!.Value);
            preview.VerticalOffset = preview.ScrollRange;
            Settle();
            Near(Offset(words), End(words));
        }
        Move(words, End(words) + 1000);
        Near(Offset(words), End(words));
        foreach (var pane in new[] { left, right, words })
            Assert.False(pane.Options.AllowScrollBelowDocument);
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkdownEditor_ScrollsToTheLastLineAndNoFurther(bool withPreview)
    {
        var (w, _) = Open("long.md", string.Join("\n\n", Enumerable.Repeat("Wiersz.", 150)));
        if (withPreview) w.RunCommand("view.preview");
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var tv = w.Editor.TextArea.TextView;
        var scroll = (ILogicalScrollable)tv;
        double bottom = tv.DocumentHeight - scroll.Viewport.Height;
        Assert.True(bottom > 0);
        Assert.Equal(tv.DocumentHeight, scroll.Extent.Height, 2);
        w.Editor.SetVerticalOffset(bottom + scroll.Viewport.Height);
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(tv.VerticalOffset, bottom - 1, bottom + 1);
        if (withPreview) Assert.Equal(w.Preview!.ScrollRange, w.Preview.VerticalOffset, 2);
        w.Close();
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
