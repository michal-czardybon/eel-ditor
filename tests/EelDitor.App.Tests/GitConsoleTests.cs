using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Core.Git;

namespace EelDitor.App.Tests;

/// <summary>Stage / Unstage on the selected files, Push and a Pull that never merges, and the git console.</summary>
public class GitConsoleTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task Git(string dir, params string[] args)
    {
        var r = await GitService.Run(dir, args, CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Error}");
    }

    private static async Task<string> Init(string dir)
    {
        await Git(dir, "init", "-q", "-b", "main");
        await Git(dir, "config", "user.email", "t@example.invalid");
        await Git(dir, "config", "user.name", "T");
        return dir;
    }

    private static MainWindow Open(string file)
    {
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [file]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        w.SideTabs.SelectedItem = w.GitTab;
        return w;
    }

    /// <summary>Runs the UI until <paramref name="done"/> holds (git runs in the background).</summary>
    private static void Until(Func<bool> done, Func<string>? what = null, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Assert.True(done(), "timed out: " + what?.Invoke());
    }

    private static GitPanel Panel(MainWindow w) => w.GetVisualDescendants().OfType<GitPanel>().Single();

    private static IEnumerable<GitRow> Rows(MainWindow w, bool staged) =>
        Panel(w).GetVisualDescendants().OfType<ListBox>().ElementAt(staged ? 1 : 0).ItemsSource as IEnumerable<GitRow> ?? [];

    private static void Click(MainWindow w, string label) =>
        Panel(w).GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == label)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task StageAndUnstage_TheSelectedFiles_AndTheConsoleShowsTheCommands()
    {
        if (!GitService.IsAvailable) return;
        string root = await Init(NewDir());
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        await Git(root, "add", "a.txt");
        await Git(root, "commit", "-q", "-m", "init");
        File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(root, "b.txt"), "new\n");

        var w = Open(Path.Combine(root, "a.txt"));
        Until(() => Rows(w, staged: false).Count() == 2);
        var panel = Panel(w);
        Dispatcher.UIThread.RunJobs();
        // Each section has its own toolbar.
        var bars = panel.GetVisualDescendants().OfType<WrapPanel>().Where(p => p.Children.FirstOrDefault() is Button)
            .Select(p => string.Join(' ', p.Children.OfType<Button>().Select(b => b.Content)));
        Assert.Equal(["Rescan Stage Pull", $"Unstage Commit Push {Loc.T("Git.Console")}"], bars);
        Assert.False(panel.StageButton.IsEnabled);                     // nothing selected
        panel.SelectRows(staged: false, r => r.Name == "a.txt");
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.StageButton.IsEnabled);
        Click(w, "Stage");
        Until(() => Rows(w, staged: true).Any(r => r.Name == "a.txt"));
        Assert.Equal(["b.txt"], Rows(w, staged: false).Select(r => r.Name));   // only the selected one

        panel.SelectRows(staged: true, r => r.Name == "a.txt");
        Dispatcher.UIThread.RunJobs();
        Click(w, "Unstage");
        Until(() => !Rows(w, staged: true).Any());

        Click(w, Loc.T("Git.Console"));
        Dispatcher.UIThread.RunJobs();
        string text = w.GitConsole!.Text;
        Assert.Contains("$ git add -- a.txt", text);
        Assert.Contains("$ git restore --staged -- a.txt", text);
        Assert.Contains(Loc.T("Git.ConsoleDone"), text);
        w.Close();
    }

    [AvaloniaFact]
    public async Task PushAndPull_ShowGitsAnswer_AndPullNeverMerges()
    {
        if (!GitService.IsAvailable) return;
        string remote = NewDir();
        await Git(remote, "init", "-q", "--bare", "-b", "main");
        string mine = await Init(NewDir());
        await Git(mine, "remote", "add", "origin", remote);
        File.WriteAllText(Path.Combine(mine, "a.txt"), "one\n");
        await Git(mine, "add", "a.txt");
        await Git(mine, "commit", "-q", "-m", "init");
        await Git(mine, "push", "-q", "-u", "origin", "main");
        string theirs = NewDir();
        await Git(theirs, "clone", "-q", remote, ".");
        await Git(theirs, "config", "user.email", "t@example.invalid");
        await Git(theirs, "config", "user.name", "T");

        var w = Open(Path.Combine(mine, "a.txt"));
        Until(() => Panel(w).Root is not null, () => "root");

        // Push: the console opens with what git said.
        File.WriteAllText(Path.Combine(mine, "b.txt"), "mine\n");
        await Git(mine, "add", "b.txt");
        await Git(mine, "commit", "-q", "-m", "mine");
        Click(w, "Push");
        Until(() => w.GitConsole?.Text.Contains(Loc.T("Git.ConsoleDone")) == true, () => "push: " + w.GitConsole?.Text);
        Assert.Contains("$ git push", w.GitConsole!.Text);

        // Pull fast-forwards: the other clone's commit arrives and the open file is reloaded.
        await Git(theirs, "pull", "-q");
        File.WriteAllText(Path.Combine(theirs, "a.txt"), "theirs\n");
        await Git(theirs, "commit", "-q", "-am", "theirs");
        await Git(theirs, "push", "-q");
        Click(w, "Pull");
        // Line ends as core.autocrlf checks them out.
        Until(() => File.ReadAllText(Path.Combine(mine, "a.txt")).Trim() == "theirs", () => "pull: " + w.GitConsole?.Text);
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FileName == "a.txt");
        Until(() => doc.Document.Text.Trim() == "theirs", () => "reload: " + doc.Document.Text);

        // Both sides have new commits: the pull changes nothing (no merge, no conflict markers) and says what to do.
        File.WriteAllText(Path.Combine(theirs, "a.txt"), "theirs again\n");
        await Git(theirs, "commit", "-q", "-am", "theirs again");
        await Git(theirs, "push", "-q");
        File.WriteAllText(Path.Combine(mine, "c.txt"), "mine again\n");
        await Git(mine, "add", "c.txt");
        await Git(mine, "commit", "-q", "-m", "mine again");
        int failedBefore = w.GitConsole!.Text.Split(Loc.T("Git.PullHint")).Length;
        Click(w, "Pull");
        Until(() => w.GitConsole?.Text.Split(Loc.T("Git.PullHint")).Length > failedBefore);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(mine, "a.txt")).Trim());
        Assert.Contains(Loc.T("Git.ConsoleFailed", 128).Split(' ')[0], w.GitConsole.Text);
        w.Close();
    }
}
