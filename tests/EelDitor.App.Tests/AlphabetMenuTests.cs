using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

public class AlphabetMenuTests
{
    private static (MainWindow W, EditorDocument Doc, AppSettings Settings) Open(string text)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var settings = new AppSettings();
        var w = new MainWindow(settings, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path), settings);
    }

    private static MenuItem Sub(ItemsControl parent, string key) =>
        parent.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T(key));

    [AvaloniaFact]
    public void ToCyrillic_ListsTheLanguages_WithPolishAAndBLast()
    {
        var (w, _, _) = Open("x");
        var edit = Sub(w.MainMenu, "Menu.Edit");
        var toCyr = Sub(Sub(edit, "Cyrillic.Menu"), "Cyrillic.ToCyrillicMenu");
        var labels = toCyr.Items.OfType<MenuItem>().Select(i => (string?)i.Header).ToList();
        Assert.Equal(new[] { "Rosyjska", "Ukraińska", "Serbska", "Międzysłowiańska", "Polska A", "Polska B" }, labels);
        Assert.DoesNotContain(labels, l => l!.Contains('('));
    }

    [AvaloniaFact]
    public void Conversion_ChangesTheWholeFile_AndCanBeUndone()
    {
        var (w, doc, _) = Open("Beograd i Ljubljana");
        w.RunCommand("edit.toCyr.sr");
        Assert.Equal("Београд и Љубљана", doc.Document.Text);
        w.RunCommand("edit.toLat.sr");
        Assert.Equal("Beograd i Ljubljana", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("Београд и Љубљана", doc.Document.Text);
    }

    [AvaloniaFact]
    public void DiffLinePanel_HeightIsRemembered()
    {
        var (w, doc, settings) = Open("ala\n");
        w.OpenDiff("HEAD", "ola\n", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        var panel = w.DiffLineHost.Child as DiffLinePanel;
        Assert.NotNull(panel);
        Assert.Null(panel.PanelHeight);
        panel.PanelHeight = 300;
        Assert.Equal(300, panel.PanelHeight);
        w.CloseDiff();
        settings.DiffLinePanelHeight = 300;
        w.OpenDiff("HEAD", "ola\n", "working", doc.Document, true, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, ((DiffLinePanel)w.DiffLineHost.Child!).PanelHeight);
        w.CloseDiff();
    }
}
