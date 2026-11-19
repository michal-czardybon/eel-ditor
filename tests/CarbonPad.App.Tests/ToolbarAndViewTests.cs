using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CarbonPad.App.Documents;
using CarbonPad.App.Services;
using CarbonPad.App.Views;

namespace CarbonPad.App.Tests;

public class ToolbarAndViewTests
{
    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    }

    private static (MainWindow W, EditorDocument Doc) Open(string name, string text)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Pump(200);
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path);
        return (w, doc);
    }

    private static Control? ToolButton(MainWindow w, string tooltip) =>
        w.Toolbar.Children.OfType<Control>().FirstOrDefault(c => ToolTip.GetTip(c) is string t && t.StartsWith(tooltip, StringComparison.Ordinal));

    [AvaloniaFact]
    public void Title_StartsWithTheAppName_AndTheDotGoesAwayWhenTheTextMatchesTheFile()
    {
        var (w, doc) = Open("a.txt", "abc");
        Assert.StartsWith(AppInfo.Name + " — ", w.Title);
        doc.Document.Insert(3, "x");
        Assert.True(doc.IsDirty);
        doc.Document.Remove(3, 1); // typed and deleted by hand, not undone
        Pump(700);
        Assert.False(doc.IsDirty);
        Assert.DoesNotContain("●", w.Title);
    }

    [AvaloniaFact]
    public void FileHistory_IsDisabledOutsideGit()
    {
        var (w, _) = Open("a.txt", "abc");
        var compare = w.MainMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Compare"));
        compare.Open();
        Dispatcher.UIThread.RunJobs();
        var history = compare.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Compare.History"));
        Assert.False(history.IsEnabled);
        compare.Close();
    }

    [AvaloniaFact]
    public void VeryLongLines_ShowANotice_AndCanBeWrapped()
    {
        string paragraph = string.Join(' ', Enumerable.Repeat("słowo", 300));
        var (w, doc) = Open("long.md", "# Tytuł\n\n" + paragraph + "\n");
        Assert.True(w.InfoBar.IsVisible);
        var wrap = w.InfoBarButtons.Children.OfType<Button>().First(b => (string?)b.Content == Loc.T("Info.WrapTo", 80));
        wrap.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.All(doc.Document.Lines, l => Assert.True(l.Length <= 80));
        Assert.StartsWith("# Tytuł\n\nsłowo słowo", doc.Document.Text);
        Assert.False(w.InfoBar.IsVisible);
        doc.Document.UndoStack.Undo();
        Assert.Contains(paragraph, doc.Document.Text);
    }

    [AvaloniaFact]
    public void CyrillicView_ShowsCyrillicWithoutChangingTheFile()
    {
        var (w, doc) = Open("p.txt", "Litwo, Ojczyzno moja!");
        var encoding = w.MainMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Encoding"));
        encoding.Open();
        Dispatcher.UIThread.RunJobs();
        var view = encoding.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Cyrillic.ViewMenu"));
        var a = view.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Cyrillic.ViewA"));
        a.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        encoding.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Editor.IsReadOnly);
        Assert.Equal("Litwo, Ojczyzno moja!", doc.Document.Text);
        var line = w.Editor.TextArea.TextView.GetOrConstructVisualLine(doc.Document.GetLineByNumber(1));
        Assert.Contains(line.Elements, e => e is AvaloniaEdit.Rendering.FormattedTextElement);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact]
    public void WordDiff_ShowsOneTextWithRemovedAndAddedWords_AndCanBeSwitchedBack()
    {
        var (w, doc) = Open("w.txt", "ala ma psa\nbez zmian\n");
        w.OpenDiff("HEAD", "ala ma kota\nbez zmian\n", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        var view = w.CurrentDiffView!;
        Assert.False(view.WordMode);
        w.RunCommand("diff.words");
        Assert.True(view.WordMode);
        Assert.True(view.WordsEditor.IsVisible);
        Assert.False(view.RightEditor.IsVisible);
        Assert.Equal("ala ma kotapsa\nbez zmian\n", view.WordsEditor.Document.Text);
        Assert.True(view.WordsEditor.IsReadOnly);
        Assert.False(ToolButton(w, Loc.T("Diff.RevertChange"))!.IsEnabled); // no reverting in word mode

        doc.Document.Insert(0, "Hej ");  // the working copy changes: the word diff follows
        Pump(700);
        Assert.StartsWith("Hej ala ma", view.WordsEditor.Document.Text);

        w.RunCommand("diff.words");
        Assert.False(view.WordMode);
        Assert.True(view.RightEditor.IsVisible);
        w.CloseDiff();
    }

    [AvaloniaFact]
    public void TableButtons_AppearOnlyInATable_AndDiffButtonsOnlyWhileComparing()
    {
        var (w, doc) = Open("t.md", "tekst\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        string rowBelow = Loc.T("Menu.Md.Table.RowBelow");
        string next = Loc.T("Menu.Compare.NextChange");
        w.Editor.CaretOffset = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.False(ToolButton(w, rowBelow)!.IsVisible);
        w.Editor.CaretOffset = doc.Document.Text.IndexOf("1 |", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolButton(w, rowBelow)!.IsVisible);

        Assert.False(ToolButton(w, next)!.IsVisible);
        w.Editor.CaretOffset = 1; // the comparison opens with the caret outside the table
        Dispatcher.UIThread.RunJobs();
        w.OpenDiff("HEAD", "inny", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolButton(w, next)!.IsVisible);
        Assert.True(ToolButton(w, Loc.T("Diff.RevertChange"))!.IsVisible);   // left is a base version
        Assert.False(ToolButton(w, Loc.T("Menu.Compare.CopyLeft"))!.IsVisible);
        Assert.False(ToolButton(w, rowBelow)!.IsVisible);
        w.CloseDiff();
        Assert.False(ToolButton(w, next)!.IsVisible);

        // After the comparison, putting the caret into the table shows the buttons already enabled.
        w.Editor.CaretOffset = 1;
        Dispatcher.UIThread.RunJobs();
        w.Editor.CaretOffset = doc.Document.Text.IndexOf("1 |", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolButton(w, rowBelow)!.IsVisible);
        Assert.True(ToolButton(w, rowBelow)!.IsEnabled);
    }
}
