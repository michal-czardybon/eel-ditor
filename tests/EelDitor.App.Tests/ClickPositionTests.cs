using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>
/// A click on a visible character must land on that character, also after hidden markup (link targets, **, `),
/// task boxes, image previews and words drawn in Cyrillic. (Elements drawn in place of several characters used to
/// shift every click after them to the right.)
/// </summary>
public class ClickPositionTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static MainWindow Open(string text, bool wrap)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "k.png"), Convert.FromBase64String(OnePixelPng));
        string path = Path.Combine(dir, "f.md");
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    /// <summary>Clicks on every character of the line that is shown as itself; returns the ones that missed.</summary>
    private static List<string> Misses(MainWindow w, int lineNumber, Func<int, bool> shownAsItself)
    {
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var doc = w.Editor.Document;
        var line = doc.GetLineByNumber(lineNumber);
        var visual = tv.GetOrConstructVisualLine(line);
        string text = doc.GetText(line);
        var misses = new List<string>();
        for (int col = 0; col < line.Length; col++)
        {
            if (!shownAsItself(col)) continue;
            var x = visual.GetVisualPosition(visual.GetVisualColumn(col), VisualYPosition.LineMiddle);
            var next = visual.GetVisualPosition(visual.GetVisualColumn(col + 1), VisualYPosition.LineMiddle);
            if (Math.Abs(next.Y - x.Y) > 1 || next.X - x.X < 2) continue; // at a wrap or a zero-width position
            if (tv.GetPosition(new Point((x.X + next.X) / 2 - 1, x.Y)) is not { } hit) { misses.Add($"{col}:null"); continue; }
            int got = doc.GetOffset(hit.Location) - line.Offset;
            if (got != col && got != col + 1) misses.Add($"'{text[col]}'@{col}->{got}");
        }
        return misses;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClicksLandOnTheCharacter_AfterHiddenMarkupTaskBoxesAndImages(bool wrap)
    {
        string text = "# T\n\nHow to ask `inflect` see [grammar.md](grammar.md). Miała **matka** syna, syna jedynego, chciała go wychować.\n\n"
            + "- [ ] zadanie i tekst po polu\n\nIkona ![k](k.png) i tekst po obrazku.\n";
        var w = Open(text, wrap);
        var model = EelDitor.Markdown.MarkdownDocument.Parse(w.Editor.Document.Text);
        bool Visible(int line, int col) => !model.GetLine(line - 1).IsHidden(col);

        Assert.Empty(Misses(w, 3, col => Visible(3, col)));
        Assert.Empty(Misses(w, 5, col => col >= model.GetLine(4).TaskColumn + 3));
        int afterImage = w.Editor.Document.GetText(w.Editor.Document.GetLineByNumber(7)).IndexOf(" i tekst", StringComparison.Ordinal);
        Assert.Empty(Misses(w, 7, col => col > afterImage));
    }

    [AvaloniaFact]
    public void ClicksLandOnTheWord_InTheCyrillicView()
    {
        var w = Open("Litwo ojczyzno moja ty jesteś jak zdrowie\n", wrap: false);
        w.RunCommand("view.cyr.plA");
        Dispatcher.UIThread.RunJobs();
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var line = w.Editor.Document.GetLineByNumber(1);
        var visual = tv.GetOrConstructVisualLine(line);
        // A click at the start of each word's drawn Cyrillic lands at that word.
        string text = w.Editor.Document.GetText(line);
        foreach (int start in new[] { 6, 15, 20, 23, 30, 34 })
        {
            var p = visual.GetVisualPosition(visual.GetVisualColumn(start), VisualYPosition.LineMiddle);
            var hit = tv.GetPosition(new Point(p.X + 1, p.Y))!.Value;
            int got = w.Editor.Document.GetOffset(hit.Location) - line.Offset;
            Assert.True(got >= start && got <= start + 8, $"{text[start..]}: {got}");
        }
    }
}
