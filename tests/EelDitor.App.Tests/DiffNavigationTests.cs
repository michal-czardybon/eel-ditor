using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Previous / Next change in the comparison, side by side and word by word.</summary>
public class DiffNavigationTests
{
    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    private static (MainWindow W, EditorDocument Doc) Open(string text)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "d.txt");
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    [AvaloniaFact]
    public void WordMode_NextAndPrevious()
    {
        var (w, doc) = Open("a b c\nd e f\ng h i\nj k l\n");
        w.OpenDiff("HEAD", "a X c\nd e f\ng h i\nj Y l\n", "working", doc.Document, true, null);
        Pump(200);
        w.RunCommand("diff.words");
        Pump(200);
        var words = w.CurrentDiffView!.WordsEditor;
        var changes = w.CurrentDiffView.WordDiffResult!.Changes;
        Assert.Equal(2, changes.Count);
        w.RunCommand("diff.next");
        Pump(100);
        int first = words.CaretOffset;
        w.RunCommand("diff.next");
        Pump(100);
        int second = words.CaretOffset;
        Assert.Equal(changes, new[] { first, second }.Order().ToArray());
        w.RunCommand("diff.prev");
        Pump(100);
        Assert.Equal(first, words.CaretOffset);
        w.RunCommand("diff.close");
        Pump(100);
    }

    private static string LongLine(string word) => string.Join(" ", Enumerable.Range(0, 300).Select(i => i == 250 ? word : "w" + i));

    [AvaloniaFact]
    public void Lines_AChangeFarRightInALongLine_IsScrolledTo()
    {
        var (w, doc) = Open("a\n" + LongLine("NEW") + "\nb\n");
        w.OpenDiff("HEAD", "a\n" + LongLine("OLD") + "\nb\n", "working", doc.Document, true, null);
        Pump(300);
        var right = w.CurrentDiffView!.RightEditor;
        Assert.Equal(2, right.TextArea.Caret.Line);
        Assert.True(right.TextArea.Caret.Column > 1000);                  // on the changed word, not at the line's start
        Assert.True(right.HorizontalOffset > 0);
        w.RunCommand("diff.close");
        Pump(100);
    }

    [AvaloniaFact]
    public void Words_AChangeInALongParagraph_IsOnScreenAndKeptWhenSwitching()
    {
        var lines = Enumerable.Range(0, 60).Select(i => "line " + i).ToList();
        lines[40] = LongLine("NEW");
        var old = lines.ToList();
        old[40] = LongLine("OLD");
        var (w, doc) = Open(string.Join("\n", lines) + "\n");
        w.OpenDiff("HEAD", string.Join("\n", old) + "\n", "working", doc.Document, true, null);
        Pump(300);
        w.RunCommand("diff.words");
        Pump(300);
        var view = w.CurrentDiffView!;
        var words = view.WordsEditor;
        Assert.True(words.WordWrap);
        Assert.Equal(view.WordDiffResult!.Changes[0], words.CaretOffset);    // the same change as in the side view
        var tv = words.TextArea.TextView;
        double y = tv.GetVisualPosition(words.TextArea.Caret.Position, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle).Y;
        Assert.InRange(y, tv.VerticalOffset, tv.VerticalOffset + tv.Bounds.Height);
        w.RunCommand("diff.words");
        Pump(300);
        Assert.Equal(41, view.RightEditor.TextArea.Caret.Line);
        w.RunCommand("diff.close");
        Pump(100);
    }
}
