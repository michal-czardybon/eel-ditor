using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Core.Text;

namespace EelDitor.App.Tests;

public class ParagraphWrapUiTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string name, string text, WrapMode mode)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { ParagraphWrap = mode }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static void Type(MainWindow w, string text)
    {
        foreach (char c in text) w.Editor.TextArea.PerformTextInput(c.ToString());
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Sentences_EachNewSentenceStartsALine()
    {
        var (w, doc) = Open("s.md", "# T\n\n", WrapMode.Sentences);
        w.Editor.CaretOffset = doc.Document.TextLength;
        Type(w, "Pierwsze zdanie, np. Kowalski. Drugie zdanie! Trzecie.");
        Assert.Equal("# T\n\nPierwsze zdanie, np. Kowalski.\nDrugie zdanie!\nTrzecie.", doc.Document.Text);
    }

    [AvaloniaFact]
    public void Width_BreaksNewText_ButLeavesAnExistingLongLine()
    {
        string existing = string.Join(' ', Enumerable.Repeat("stare", 30));
        var (w, doc) = Open("w.md", existing + "\n\n", WrapMode.Width80);
        w.Editor.CaretOffset = existing.Length;
        Type(w, " dalej");
        Assert.StartsWith(existing + " dalej\n", doc.Document.Text);   // the old one-line paragraph is not rewritten

        w.Editor.CaretOffset = doc.Document.TextLength;
        Type(w, string.Join(' ', Enumerable.Repeat("nowe", 30)));
        var newLines = doc.Document.Text.Split('\n').Skip(2).ToList();
        Assert.True(newLines.Count >= 2);
        Assert.All(newLines, l => Assert.True(l.Length <= 80, l));
    }

    [AvaloniaFact]
    public void CodeAndPlainSourceFiles_AreNeverBroken()
    {
        var (w, doc) = Open("a.cs", "", WrapMode.Sentences);
        Type(w, "// Pierwsze. Drugie");
        Assert.Equal("// Pierwsze. Drugie", doc.Document.Text);
    }

    [AvaloniaFact]
    public void Reflow_FormatsTheParagraphAtTheCaret()
    {
        var (w, doc) = Open("r.md", "# T\n\nJedno zdanie. Drugie\nzdanie. Trzecie.\n\nInny akapit. Bez zmian.\n", WrapMode.Sentences);
        w.Editor.CaretOffset = 8;
        w.RunCommand("wrap.reflow");
        Assert.Equal("# T\n\nJedno zdanie.\nDrugie zdanie.\nTrzecie.\n\nInny akapit. Bez zmian.\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("# T\n\nJedno zdanie. Drugie\nzdanie. Trzecie.\n\nInny akapit. Bez zmian.\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void LongLinesNotice_SplitsSentences_AndShowsThatNoWordChanged()
    {
        string para = string.Join(' ', Enumerable.Range(1, 150).Select(i => $"Zdanie numer {i}."));
        var (w, doc) = Open("long.md", "# T\n\n" + para + "\n", WrapMode.OneLine);
        Assert.True(w.InfoBar.IsVisible);
        var split = w.InfoBarButtons.Children.OfType<Button>().First(b => (string?)b.Content == Loc.T("Info.WrapSentences"));
        split.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(152, doc.Document.LineCount - 1);
        Assert.Contains("Zdanie numer 1.\nZdanie numer 2.\n", doc.Document.Text);

        // The notice offers the word diff, which shows that only line breaks changed.
        var show = w.InfoBarButtons.Children.OfType<Button>().First(b => (string?)b.Content == Loc.T("Info.ShowWordDiff"));
        show.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (w.CurrentDiffView is null && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Assert.True(w.CurrentDiffView!.WordMode);
        Assert.Empty(w.CurrentDiffView.WordDiffResult!.Spans);
        w.CloseDiff();
    }

    [AvaloniaFact]
    public void WordDiffButton_OpensAComparisonInWordMode()
    {
        var (w, doc) = Open("x.txt", "ala ma kota\n", WrapMode.OneLine);
        doc.Document.Replace(7, 4, "psa");
        w.RunCommand("diff.words");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (w.CurrentDiffView is null && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Assert.True(w.CurrentDiffView!.WordMode);
        Assert.Equal("ala ma kotapsa\n", w.CurrentDiffView.WordsEditor.Document.Text);
        w.CloseDiff();
    }
}
