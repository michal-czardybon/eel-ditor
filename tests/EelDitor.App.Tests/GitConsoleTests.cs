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

    private static MainWindow Open(params string[] files)
    {
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), files);
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
    public async Task RepositoryPicker_SwitchesStatusAndCommitDraftWithoutSwitchingFiles()
    {
        if (!GitService.IsAvailable) return;
        string first = await Init(NewDir()), second = await Init(NewDir());
        string a = Path.Combine(first, "a.txt"), b = Path.Combine(second, "b.txt");
        File.WriteAllText(a, "one\n");
        File.WriteAllText(b, "two\n");
        var w = Open(a, b);
        Until(() => Panel(w).Root == second);
        var picker = Panel(w).GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "GitRepositories");
        Assert.Equal(new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase), picker.Items.Cast<string>());
        Panel(w).CommitMessage = "second draft";
        picker.SelectedItem = first;
        Until(() => Panel(w).Root == first && Rows(w, staged: false).Any(r => r.Name == "a.txt"));
        Assert.Equal(b, ((OpenFilesModel)w.DataContext!).Active!.FilePath);
        Assert.Equal("", Panel(w).CommitMessage);
        Panel(w).CommitMessage = "first draft";
        picker.SelectedItem = second;
        Until(() => Panel(w).Root == second);
        Assert.Equal("second draft", Panel(w).CommitMessage);
        w.Close();
    }

    [AvaloniaFact]
    public async Task RepositoryLaunch_OpensGitWithoutOpeningFilesOrReplacingTheSavedSession()
    {
        if (!GitService.IsAvailable) return;
        string root = await Init(NewDir());
        string file = Path.Combine(root, "a.txt");
        File.WriteAllText(file, "one\n");
        await Git(root, "add", "a.txt");
        SettingsStore.UseDirectory(TestDirs.New());
        SettingsStore.SaveSession(new Session { ActivePath = file, Files = [new SessionFile { Path = file }] });

        var w = new MainWindow(new AppSettings(), [SingleInstance.GitArgumentPrefix + root]);
        w.Show();
        Until(() => Panel(w).Root == root && w.StartupCompleted);
        Assert.Same(w.GitTab, w.SideTabs.SelectedItem);
        var docs = ((OpenFilesModel)w.DataContext!).Documents;
        Assert.Empty(docs);
        Panel(w).SelectRows(staged: true, _ => true);
        Until(() => w.CurrentDiffView is not null);
        Assert.Empty(docs);
        w.Close();
        Assert.Equal(file, SettingsStore.LoadSession().ActivePath);
    }

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
        Assert.Equal(["Rescan Stage Pull Log", $"Unstage Commit Push {Loc.T("Git.Console")}"], bars);
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
    public async Task Log_ShowsCommitAuthorsAndChangesFilesWhenAnotherCommitIsSelected()
    {
        if (!GitService.IsAvailable) return;
        string root = await Init(NewDir());
        string path = Path.Combine(root, "a.txt");
        File.WriteAllText(path, "first\n");
        await Git(root, "add", "a.txt");
        await Git(root, "commit", "-q", "--author", "Anna <anna@example.invalid>", "-m", "first commit");
        File.WriteAllText(Path.Combine(root, "b.txt"), "second\n");
        await Git(root, "add", "b.txt");
        await Git(root, "commit", "-q", "--author", "Jan <jan@example.invalid>", "-m", "second commit");

        var w = Open(path);
        Until(() => Panel(w).Root is not null);
        Click(w, "Log");
        var log = Assert.Single(w.OwnedWindows.OfType<GitLogWindow>());
        var tables = log.GetVisualDescendants().OfType<ListBox>().ToArray();
        Assert.Equal(2, tables.Length);
        var commits = tables[0];
        var files = tables[1];
        Until(() => (files.ItemsSource as IEnumerable<GitCommitFile>)?.Any() == true);
        Assert.Equal(["Jan", "Anna"], commits.ItemsSource!.Cast<GitCommit>().Select(c => c.Author));
        Assert.Equal("b.txt", Assert.Single(files.ItemsSource!.Cast<GitCommitFile>()).Path);
        commits.SelectedIndex = 1;
        Until(() => files.ItemsSource?.Cast<GitCommitFile>().SingleOrDefault()?.Path == "a.txt");
        log.Close();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_RefreshesTheOpenHeadComparison(bool stagedComparison)
    {
        if (!GitService.IsAvailable) return;
        string root = await Init(NewDir());
        string path = Path.Combine(root, "a.txt");
        File.WriteAllText(path, "original\n");
        await Git(root, "add", "a.txt");
        await Git(root, "commit", "-q", "-m", "init");
        File.WriteAllText(path, "staged\n");
        await Git(root, "add", "a.txt");
        File.WriteAllText(path, "working\n");

        var w = Open(path);
        Until(() => Rows(w, staged: true).Any());
        if (stagedComparison) Panel(w).SelectRows(staged: true, _ => true);
        else w.RunCommand("diff.head");
        Until(() => w.CurrentDiffView is not null);
        var diff = w.CurrentDiffView!;
        Assert.Equal("original", diff.LeftEditor.Text.Trim());
        var right = diff.RightEditor.Document;
        Assert.Equal(stagedComparison ? "staged" : "working", right.Text.Trim());
        Assert.False(diff.Diff.Identical);

        Panel(w).CommitMessage = "update a";
        Click(w, "Commit");
        Until(() => diff.LeftEditor.Text.Trim() == "staged" && diff.Diff.Identical == stagedComparison);
        Assert.Same(diff, w.CurrentDiffView);
        Assert.Same(right, diff.RightEditor.Document);
        Assert.Equal(stagedComparison ? "staged" : "working", right.Text.Trim());
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
