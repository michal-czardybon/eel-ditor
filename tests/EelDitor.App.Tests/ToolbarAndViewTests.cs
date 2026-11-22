using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

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
    public void VeryLongLines_CanBeWrapped_FromTheStatusBar()
    {
        string paragraph = string.Join(' ', Enumerable.Repeat("słowo", 300));
        var (w, doc) = Open("long.md", "# Tytuł\n\n" + paragraph + "\n");
        Assert.False(w.InfoBar.IsVisible);
        Assert.True(w.FileConventionButton!.IsVisible);
        Assert.Equal(Loc.T("Status.Longest", 1799.ToString("N0")), w.StatusLongest.Text);
        w.RunCommand("wrap.split80");
        Assert.All(doc.Document.Lines, l => Assert.True(l.Length <= 80));
        Assert.StartsWith("# Tytuł\n\nsłowo słowo", doc.Document.Text);
        Assert.Contains(Loc.T("Info.ShowWordDiff"), w.InfoBarButtons.Children.OfType<Button>().Select(b => (string?)b.Content)); // the result notice
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
        Assert.Contains(line.Elements, e => e.GetType().Name == "CyrillicWordElement");
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
    public void WordDiff_CanRevertAllChanges()
    {
        var (w, doc) = Open("w.txt", "ala ma psa\nbez zmian\n");
        w.OpenDiff("HEAD", "ala ma kota\nbez zmian\n", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        var view = w.CurrentDiffView!;
        w.RunCommand("diff.words");
        Assert.True(ToolButton(w, Loc.T("Diff.RevertAll"))!.IsEnabled);
        w.RunCommand("diff.revertAll");
        Assert.Equal("ala ma kota\nbez zmian\n", doc.Document.Text);
        Assert.Equal("ala ma kota\nbez zmian\n", view.WordsEditor.Document.Text);
        w.CloseDiff();
    }

    [AvaloniaFact]
    public void Reload_IsOnTheToolbar_AndOnF5()
    {
        var (w, doc) = Open("r.txt", "stare");
        Assert.NotNull(ToolButton(w, Loc.T("Menu.File.Reload")));
        Assert.Contains("F5", w.CommandGestures()["file.reload"]);
        File.WriteAllText(doc.FilePath!, "nowe");
        w.RunCommand("file.reload");
        Pump(300);
        Assert.Equal("nowe", doc.Document.Text);
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevertAll_IsUndoneByCtrlZ(bool words)
    {
        var (w, doc) = Open("u.txt", "ala ma psa\nbez zmian\n");
        w.OpenDiff("HEAD", "ala ma kota\nbez zmian\n", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        var view = w.CurrentDiffView!;
        if (words) w.RunCommand("diff.words");
        (words ? view.WordsEditor : view.RightEditor).TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        w.RunCommand("diff.revertAll");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ala ma kota\nbez zmian\n", doc.Document.Text);
        w.KeyPress(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, "z");
        w.KeyRelease(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, "z");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ala ma psa\nbez zmian\n", doc.Document.Text);
        w.CloseDiff();
    }

    [AvaloniaFact]
    public void Menus_TableOnItsOwn_HeadingsInPlace_PlainTextViewInView()
    {
        var (w, _) = Open("m.md", "# T\n");
        MenuItem Top(string key) => w.MainMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T(key));
        IEnumerable<string?> Headers(MenuItem m) => m.Items.OfType<MenuItem>().Select(i => (string?)i.Header);
        var markdown = Top("Menu.Markdown");
        Assert.Contains(Loc.T("Menu.Md.H1"), Headers(markdown));                 // no Heading submenu to open
        var headers = Headers(markdown).ToList();
        Assert.Equal(headers.IndexOf(Loc.T("Menu.Md.H0")) + 1, headers.IndexOf(Loc.T("Menu.Md.H1"))); // Normal text first
        Assert.DoesNotContain(Loc.T("Menu.Md.Table"), Headers(markdown));
        Assert.DoesNotContain(Loc.T("Menu.Md.Raw"), Headers(markdown));
        Assert.Contains(Loc.T("Menu.Md.Table.Split"), Headers(Top("Menu.Md.Table")));
        Assert.Contains(Loc.T("Menu.Md.Raw"), Headers(Top("Menu.View")));
    }

    [AvaloniaFact]
    public void WhileComparing_ClosingTheFile_ClosesTheComparisonFirst()
    {
        var (w, doc) = Open("c.txt", "ala\n");
        w.OpenDiff("HEAD", "ola\n", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.DocClose.IsVisible);                                      // only the comparison's ✕
        w.RunCommand("file.close");
        Pump(200);
        Assert.Null(w.CurrentDiffView);
        Assert.Contains(doc, ((OpenFilesModel)w.DataContext!).Documents);        // the file is still open
        Assert.True(w.DocClose.IsVisible);
        w.RunCommand("file.close");
        Pump(200);
        Assert.DoesNotContain(doc, ((OpenFilesModel)w.DataContext!).Documents);
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
