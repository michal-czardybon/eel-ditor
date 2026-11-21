using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Core.Git;

namespace EelDitor.App.Tests;

public class SidebarAndMenuTests
{
    private static MainWindow Open(params string[] files)
    {
        SettingsStore.UseDirectory(Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N")));
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), files);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static MenuItem TopMenu(MainWindow w, string key) =>
        w.MainMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T(key));

    private static MenuItem Child(MenuItem menu, string key) =>
        menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T(key));

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    }

    [AvaloniaFact]
    public void CompareCommands_AreDisabledWithoutAComparison()
    {
        var w = Open();
        w.NewDocument();
        var compare = TopMenu(w, "Menu.Compare");
        compare.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Child(compare, "Menu.Compare.NextChange").IsEnabled);
        Assert.False(Child(compare, "Menu.Compare.Close").IsEnabled);
        compare.Close();

        var doc = ((OpenFilesModel)w.DataContext!).Documents.Last();
        doc.Document.Text = "a\nb";
        w.OpenDiff("HEAD", "a\nc", "working", doc.Document, true, null);
        compare.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Child(compare, "Menu.Compare.NextChange").IsEnabled);
        Assert.True(Child(compare, "Menu.Compare.Close").IsEnabled);
        compare.Close();
        w.CloseDiff();
    }

    [AvaloniaFact]
    public void EncodingSubmenus_StayOpen()
    {
        var w = Open();
        var encoding = TopMenu(w, "Menu.Encoding");
        encoding.Open();
        Dispatcher.UIThread.RunJobs();
        var central = Child(encoding, "Encoding.Group.Central");
        central.Open();
        Dispatcher.UIThread.RunJobs();
        // Opening a nested submenu must not rebuild (and so close) the parent's items.
        Assert.Contains(central, encoding.Items.OfType<MenuItem>());
        Assert.True(central.IsSubMenuOpen);
        Assert.True(central.Items.Count >= 3);
        encoding.Close();
    }

    [AvaloniaFact]
    public void EditorContextMenu_OffersTableOperationsInsideATable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string md = Path.Combine(dir, "t.md");
        File.WriteAllText(md, "text\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        var w = Open(md);
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == md);
        w.Activate(doc);
        Dispatcher.UIThread.RunJobs();
        var menu = w.Editor.ContextMenu!;

        w.Editor.CaretOffset = 1; // in "text"
        w.Editor.RaiseEvent(new ContextRequestedEventArgs()); // what a right click does
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.Items.Count > 0);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => (string?)i.Header == Loc.T("Menu.Md.Table.RowBelow"));
        menu.Close();

        w.Editor.CaretOffset = doc.Document.Text.IndexOf("1 |", StringComparison.Ordinal);
        w.Editor.RaiseEvent(new ContextRequestedEventArgs()); // what a right click does
        Dispatcher.UIThread.RunJobs();
        var rowBelow = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Md.Table.RowBelow"));
        rowBelow.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Assert.Equal(7, doc.Document.LineCount);
    }

    [AvaloniaFact]
    public void ShortcutsPopup_GroupsAlignsAndClosesOnEscape()
    {
        var w = Open();
        w.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        var popup = w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First(p => p.IsOpen);
        var grid = ((Control)popup.Child!).GetVisualDescendants().OfType<ScrollViewer>().Select(v => v.Content).OfType<Grid>().First();
        // Every shortcut row has its keys in the second column of one shared grid, so the columns line up.
        Assert.True(grid.Children.Count(c => Grid.GetColumn(c) == 1) > 30);
        Assert.Equal("Ctrl++", ShortcutsPopup.Pretty("Ctrl+OemPlus"));
        Assert.Equal("Ctrl+K, Ctrl+C", ShortcutsPopup.Pretty("Ctrl+K, Ctrl+C"));
        popup.Child!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.False(popup.IsOpen);
    }

    [AvaloniaFact]
    public void DocumentMap_FollowsTheActiveDocument()
    {
        var w = Open();
        w.NewDocument();
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Last();
        doc.Document.Text = string.Join("\n", Enumerable.Range(1, 500).Select(i => "line " + i));
        Dispatcher.UIThread.RunJobs();
        w.SideTabs.SelectedItem = w.MapTab;
        Dispatcher.UIThread.RunJobs();
        var map = w.GetVisualDescendants().OfType<DocumentMap>().Single();
        var mapEditor = map.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();
        Assert.Same(doc.Document, mapEditor.Document);
    }

    [AvaloniaFact]
    public async Task GitTab_ListsUnstagedAndStagedChanges()
    {
        if (!GitService.IsAvailable) return;
        string root = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Git(params string[] a) => Assert.Equal(0, (await GitService.Run(root, a, CancellationToken.None)).ExitCode);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.invalid");
        await Git("config", "user.name", "T");
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        await Git("add", "a.txt");
        await Git("commit", "-q", "-m", "init");
        File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(root, "b.txt"), "new\n");
        await Git("add", "b.txt");

        var w = Open(Path.Combine(root, "a.txt"));
        var tabs = w.SideTabs;
        tabs.SelectedItem = w.GitTab;
        var panel = w.GetVisualDescendants().OfType<GitPanel>().Single();
        List<ListBox> lists = [];
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            Pump(100);
            lists = panel.GetVisualDescendants().OfType<ListBox>().ToList();
            if (lists.Count == 2 && lists.All(l => l.ItemsSource is IEnumerable<GitRow> rows && rows.Any())) break;
        }
        Assert.Equal(2, lists.Count);
        Assert.Equal(["a.txt"], ((IEnumerable<GitRow>)lists[0].ItemsSource!).Select(r => r.Name));
        Assert.Equal(["b.txt"], ((IEnumerable<GitRow>)lists[1].ItemsSource!).Select(r => r.Name));
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FileName == "a.txt");
        Assert.Equal("M", doc.GitBadge);
        Assert.True(doc.GitModified);
        Assert.Contains(Loc.T("Git.Status.Modified"), doc.Tooltip);

        // Selecting a row must not reload the list under it (SelectionChanged bubbles up to the tab control).
        // (It used to throw "Cannot change source while update is in progress".)
        lists[0].SelectedIndex = 0;
        Pump(200);
    }
}
