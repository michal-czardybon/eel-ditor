using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Core.Git;

namespace EelDitor.App.Tests;

public class ConflictUiTests
{
    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    }

    /// <summary>Waits (pumping the UI) until the condition holds; git can be slow to start on a busy machine.</summary>
    private static void Until(Func<bool> condition, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    }

    private static MainWindow Open(string file)
    {
        SettingsStore.UseDirectory(Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N")));
        EelDitor.App.Services.SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [file]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Pump(300);
        return w;
    }

    [AvaloniaFact]
    public void ConflictMarkers_AreFound_AndResolvedOneByOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!GitService.IsAvailable) return;
        GitService.Run(dir, ["init", "-q"], CancellationToken.None).GetAwaiter().GetResult(); // conflicts are a git feature
        string path = Path.Combine(dir, "c.txt");
        File.WriteAllText(path, "top\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> other\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\n>>>>>>> other\nend\n");
        var w = Open(path);
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path);
        Assert.Equal(2, w.CurrentConflicts.Count);
        Assert.True(w.ConflictBar.IsVisible);

        w.Editor.TextArea.Caret.Line = 3; // inside the first conflict
        w.ResolveConflict(ConflictChoice.Incoming);
        Assert.Single(w.CurrentConflicts);
        Assert.StartsWith("top\ntheirs\nmiddle\n", doc.Document.Text);

        w.Editor.TextArea.Caret.Line = w.CurrentConflicts[0].Start + 1;
        w.ResolveConflict(ConflictChoice.Both);
        Assert.Equal("top\ntheirs\nmiddle\nA\nB\nend\n", doc.Document.Text);
        Assert.Empty(w.CurrentConflicts);
        Assert.False(w.ConflictBar.IsVisible); // not a git conflict, so nothing left to do

        doc.Document.UndoStack.Undo();
        Pump(400);
        Assert.Single(w.CurrentConflicts);
    }

    [AvaloniaFact]
    public async Task GitConflict_CanBeMarkedResolvedAfterTheMarkersAreGone()
    {
        if (!GitService.IsAvailable) return;
        string root = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task<GitService.Result> Git(params string[] a) => await GitService.Run(root, a, CancellationToken.None);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.invalid");
        await Git("config", "user.name", "T");
        string file = Path.Combine(root, "f.txt");
        File.WriteAllText(file, "base\n");
        await Git("add", "f.txt"); await Git("commit", "-q", "-m", "base");
        await Git("checkout", "-q", "-b", "other");
        File.WriteAllText(file, "theirs\n");
        await Git("commit", "-q", "-am", "theirs");
        await Git("checkout", "-q", "main");
        File.WriteAllText(file, "mine\n");
        await Git("commit", "-q", "-am", "mine");
        Assert.NotEqual(0, (await Git("merge", "other")).ExitCode); // conflict

        var w = Open(file);
        w.SideTabs.SelectedItem = w.GitTab; // loads the git status
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == file);
        Until(() => doc.GitConflict && w.CurrentConflicts.Count == 1);
        Assert.True(doc.GitConflict);
        Assert.Single(w.CurrentConflicts);

        w.Editor.TextArea.Caret.Line = 2;
        w.ResolveConflict(ConflictChoice.Current);
        Pump(300);
        Assert.Equal("mine\n", doc.Document.Text.Replace("\r\n", "\n"));
        Assert.True(w.ConflictBar.IsVisible); // git still reports the conflict
        var mark = w.ConflictBarButtons.Children.OfType<Button>().Single();
        mark.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => !doc.GitConflict);
        var status = await GitService.StatusAsync(root);
        Assert.DoesNotContain(status!.Entries, e => e.IsConflict);
        Assert.False(doc.IsDirty);
    }
}
