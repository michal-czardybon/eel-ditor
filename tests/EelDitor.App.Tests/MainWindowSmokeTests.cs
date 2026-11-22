using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

public class MainWindowSmokeTests
{
    private static MainWindow Open()
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void OpensWithAnUntitledDocument()
    {
        SettingsStore.UseDirectory(Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N")));
        var w = Open();
        Assert.Equal("nowy 1", ((EelDitor.App.Documents.OpenFilesModel)w.DataContext!).Documents.Single().FileName.Replace("nowy ", "nowy ").Split(' ')[0] + " 1");
        Assert.Contains("Eel-ditor", w.Title);
    }

    [AvaloniaFact]
    public void ShortcutsDoNotThrow()
    {
        var w = Open();
        Press(w, Key.N, RawInputModifiers.Control);
        Press(w, Key.F, RawInputModifiers.Control);
        Assert.True(w.FindBarControl!.IsVisible);
        Press(w, Key.Escape);
        Assert.False(w.FindBarControl.IsVisible);
        Press(w, Key.H, RawInputModifiers.Control);
        Assert.True(w.FindBarControl.ReplaceVisible);
        Press(w, Key.Escape);
        Press(w, Key.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        var files = Assert.Single(w.OwnedWindows.OfType<FindWindow>());
        Assert.True(files.IsVisible);
        files.Hide();
        Press(w, Key.P, RawInputModifiers.Control);
        Press(w, Key.Escape);
        Press(w, Key.Tab, RawInputModifiers.Control);
        Press(w, Key.Z, RawInputModifiers.Alt);
        Press(w, Key.D8, RawInputModifiers.Control | RawInputModifiers.Shift);
        Press(w, Key.K, RawInputModifiers.Control);
        Press(w, Key.C, RawInputModifiers.Control);
        Press(w, Key.F2, RawInputModifiers.Control);
        Press(w, Key.D, RawInputModifiers.Control);
        Assert.Equal(2, ((EelDitor.App.Documents.OpenFilesModel)w.DataContext!).Documents.Count);
    }
}

public class DiffInWindowTests
{
    [AvaloniaFact]
    public void SwitchingDocumentWhileDiffIsOpen_ThenClosing_DoesNotThrow()
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var model = (EelDitor.App.Documents.OpenFilesModel)w.DataContext!;
        var a = model.Documents.Single();
        a.Document.Text = "a\nb\nc";
        w.NewDocument();
        var b = model.Documents.Last();
        b.Document.Text = "x\ny";
        w.Activate(a);
        Dispatcher.UIThread.RunJobs();
        w.OpenDiff("HEAD", "a\nB\nc", "working", a.Document, true, @"C:\x\a.cs");
        Dispatcher.UIThread.RunJobs();
        w.Activate(b);
        Dispatcher.UIThread.RunJobs();
        w.CloseDiff();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("x\ny", w.Editor.Document.Text);
        Assert.True(w.Editor.IsVisible);
        w.Activate(a);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a\nb\nc", w.Editor.Document.Text);
    }
}

public class DiffKeyboardTests
{
    [AvaloniaFact]
    public void AltRight_CopiesBlock_AndEditingShortcutsDoNotTouchTheHiddenEditor()
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var doc = ((EelDitor.App.Documents.OpenFilesModel)w.DataContext!).Documents.Single();
        doc.Document.Text = "a\nX\nc";
        w.OpenDiff("HEAD", "a\nb\nc", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        // Ctrl+D (duplicate line) is an editor command: it must not run on the hidden main editor.
        w.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a\nX\nc", doc.Document.Text);
        w.KeyPress(Key.Right, RawInputModifiers.Alt, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a\nb\nc", doc.Document.Text);
        w.CloseDiff();
    }
}

public class ThemeTests
{
    [AvaloniaFact]
    public void SwitchingFromDarkToSystem_RepaintsEditorBackground()
    {
        App.ApplyTheme(AppTheme.Dark);
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            App.ApplyTheme(AppTheme.System);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, w.ActualThemeVariant); // headless platform reports light
            var bg = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(w.Editor.Background);
            Assert.True(bg.Color.R > 200, $"editor background {bg.Color}");
            var area = w.Editor.TextArea.Background as Avalonia.Media.ISolidColorBrush;
            Assert.True(area is null || area.Color.A == 0 || area.Color.R > 200, $"text area background {area?.Color}");
        }
        finally { App.ApplyTheme(AppTheme.Dark); }
    }
}

public class SessionAndViewTests
{
    private static string TempFile(string name, string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"), "sub");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void OpenFiles_AreSavedWithoutClosing_AndRestoredByTheNextWindow()
    {
        SettingsStore.UseDirectory(Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N")));
        string a = TempFile("a.txt", "aaa"), b = TempFile("b.md", "# b");
        var w1 = new MainWindow(new AppSettings(), [a, b]);
        w1.Show();
        Pump(1600); // open + the 1 s autosave
        Assert.Equal(2, ((EelDitor.App.Documents.OpenFilesModel)w1.DataContext!).Documents.Count(d => d.FilePath is not null));
        string sessionFile = Path.Combine(EelDitor.App.Services.SettingsStore.Directory, "session.json");
        Assert.True(File.Exists(sessionFile), "session not saved: " + sessionFile);
        Assert.Contains("a.txt", File.ReadAllText(sessionFile));
        var w2 = new MainWindow(new AppSettings(), []);
        w2.Show();
        Pump(300);
        var paths = ((EelDitor.App.Documents.OpenFilesModel)w2.DataContext!).Documents.Select(d => d.FilePath).ToList();
        Assert.Contains(a, paths);
        Assert.Contains(b, paths);
    }

    [AvaloniaFact]
    public void CtrlWheel_ChangesFontSize()
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        double before = w.Editor.FontSize;
        var p = w.Editor.TranslatePoint(new Avalonia.Point(100, 50), w)!.Value;
        w.MouseWheel(p, new Avalonia.Vector(0, 1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before + 1, w.Editor.FontSize);
        w.MouseWheel(p, new Avalonia.Vector(0, -1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, w.Editor.FontSize);
    }

    [AvaloniaFact]
    public void RecentFiles_ShowNamesWithPathInTooltip()
    {
        string a = TempFile("notes.txt", "x");
        var settings = new AppSettings { RecentFiles = [a] };
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(settings, []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var file = (MenuItem)w.MainMenu.Items[0]!;
        var recent = file.Items.OfType<MenuItem>().Single(i => (string?)i.Header == EelDitor.App.Services.Loc.T("Menu.File.Recent"));
        // Other tests share the settings directory, so look the entry up by its tooltip.
        var item = recent.Items.OfType<MenuItem>().Single(i => (string?)ToolTip.GetTip(i) == a);
        Assert.Equal("notes.txt", item.Header);
    }

    [AvaloniaFact]
    public void DiffLinePanel_ShowsBothVersionsOfTheCurrentLine()
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var doc = ((EelDitor.App.Documents.OpenFilesModel)w.DataContext!).Documents.Single();
        doc.Document.Text = "a\nfoo bar\nc";
        w.OpenDiff("HEAD", "a\nfoo baz\nc", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.DiffLineHost.IsVisible);
        var texts = w.DiffLineHost.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(t => string.Concat(t.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text))).ToList();
        Assert.Equal(["foo baz", "foo bar"], texts);
        w.CloseDiff();
        Assert.False(w.DiffLineHost.IsVisible);
    }
}


public class FindBarTests
{
    private static (MainWindow W, EelDitor.App.Documents.EditorDocument Doc) Open(string text)
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        // Wait until the shared test session has been restored, then work in a fresh document.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        w.NewDocument();
        var doc = ((EelDitor.App.Documents.OpenFilesModel)w.DataContext!).Documents.Last();
        w.Activate(doc);
        doc.Document.Text = text;
        w.Editor.CaretOffset = 0;
        return (w, doc);
    }

    private static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TypingSearchesIncrementally_AndEnterMovesToTheNextMatch()
    {
        var (w, _) = Open("foo bar foo baz foo");
        Press(w, Key.F, RawInputModifiers.Control);
        w.FindBarControl!.FindText = "foo";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, w.Editor.SelectionStart);
        Assert.Equal(3, w.Editor.SelectionLength);
        Press(w, Key.Enter);
        Assert.Equal(8, w.Editor.SelectionStart);
        Press(w, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal(0, w.Editor.SelectionStart);
        Press(w, Key.F3);
        Assert.Equal(8, w.Editor.SelectionStart);
    }

    [AvaloniaFact]
    public void ReplaceAll_InSelectionScope_ChangesOnlyTheSelectedLines()
    {
        var (w, doc) = Open("a x\nb x\nc x\nd x");
        w.Editor.Select(4, 8); // "b x\nc x\n"
        Press(w, Key.H, RawInputModifiers.Control);
        var bar = w.FindBarControl!;
        Assert.Equal(SearchScope.Selection, bar.Scope);
        bar.FindText = "x";
        bar.ReplaceText = "Y";
        Press(w, Key.A, RawInputModifiers.Alt);
        Assert.Equal("a x\nb Y\nc Y\nd x", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("a x\nb x\nc x\nd x", doc.Document.Text);
    }

    [AvaloniaFact]
    public void InvalidRegex_IsReportedInTheBar()
    {
        var (w, _) = Open("abc");
        Press(w, Key.F, RawInputModifiers.Control);
        var bar = w.FindBarControl!;
        Press(w, Key.E, RawInputModifiers.Alt);
        bar.FindText = "a(";
        Dispatcher.UIThread.RunJobs();
        string expected = EelDitor.App.Services.Loc.T("Find.InvalidRegexShort");
        Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == expected);
    }

    [AvaloniaFact]
    public void EolConversionCommand_ConvertsTheDocument()
    {
        var (w, doc) = Open("a\r\nb\nc");
        var edit = (MenuItem)w.MainMenu.Items[1]!;
        var eol = edit.Items.OfType<MenuItem>().Single(i => (string?)i.Header == EelDitor.App.Services.Loc.T("Menu.Edit.Eol"));
        var toLf = eol.Items.OfType<MenuItem>().Single(i => (string?)i.Header == EelDitor.App.Services.Loc.T("Menu.Edit.EolLf"));
        toLf.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("a\nb\nc", doc.Document.Text);
        Assert.Equal(EelDitor.Core.Text.EolKind.Lf, doc.Eol);
        Assert.False(doc.EolMixed);
    }

    [AvaloniaFact]
    public void LanguageMenuIsGone()
    {
        var (w, _) = Open("");
        Assert.DoesNotContain(w.MainMenu.Items.OfType<MenuItem>(), i => (string?)i.Header == "Język");
    }
}

public class ClosingTests
{
    [AvaloniaFact]
    public void MainWindowCloses_WhileTheFilesSearchWindowIsOpen()
    {
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), []);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(w.OwnedWindows.OfType<FindWindow>());
        bool closed = false;
        w.Closed += (_, _) => closed = true;
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
    }
}
