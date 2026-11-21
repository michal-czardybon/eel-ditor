using EelDitor.Core.Git;

namespace EelDitor.Core.Tests;

/// <summary>Runs real git in a temporary repository (skipped when git is not installed).</summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EelDitor.GitTests", Guid.NewGuid().ToString("N"));

    public GitServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task Git(params string[] args)
    {
        var r = await GitService.Run(_root, args, CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Error}");
    }

    private async Task<string> InitRepoAsync()
    {
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "user.name", "Test");
        await Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
        await Git("add", "a.txt");
        await Git("commit", "-q", "-m", "first");
        return _root;
    }

    [Fact]
    public async Task Status_SeparatesStagedAndUnstaged_AndCommitClearsIt()
    {
        if (!GitService.IsAvailable) return;
        var root = await InitRepoAsync();
        File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(root, "new.txt"), "x\n");

        var st = await GitService.StatusAsync(root);
        Assert.NotNull(st);
        Assert.Equal("main", st!.Branch);
        var a = st.Entries.Single(e => e.RelativePath == "a.txt");
        Assert.True(a.HasUnstaged);
        Assert.False(a.HasStaged);
        Assert.True(st.Entries.Single(e => e.RelativePath == "new.txt").IsUntracked);

        Assert.Equal(0, (await GitService.StageAsync(root, ["a.txt", "new.txt"])).ExitCode);
        st = await GitService.StatusAsync(root);
        Assert.All(st!.Entries, e => Assert.True(e.HasStaged && !e.HasUnstaged));
        Assert.Equal("A", st.Files[Path.Combine(root, "new.txt")]);

        Assert.Equal(0, (await GitService.UnstageAsync(root, ["a.txt"])).ExitCode);
        st = await GitService.StatusAsync(root);
        Assert.False(st!.Entries.Single(e => e.RelativePath == "a.txt").HasStaged);

        await GitService.StageAsync(root, ["a.txt"]);
        var commit = await GitService.CommitAsync(root, "second ż");
        Assert.True(commit.ExitCode == 0, commit.Error);
        st = await GitService.StatusAsync(root);
        Assert.Empty(st!.Entries);
        Assert.Equal("second ż", (await GitService.LogAsync(root, "a.txt"))[0].Subject);
    }

    [Fact]
    public async Task Discard_RestoresTheWorkTree_AndUntrackKeepsTheFile()
    {
        if (!GitService.IsAvailable) return;
        var root = await InitRepoAsync();
        string a = Path.Combine(root, "a.txt");
        File.WriteAllText(a, "changed\n");
        Assert.Equal(0, (await GitService.DiscardAsync(root, ["a.txt"])).ExitCode);
        Assert.Equal("one\n", File.ReadAllText(a).Replace("\r\n", "\n"));

        Assert.True(await GitService.IsTrackedAsync(root, "a.txt"));
        Assert.Equal(0, (await GitService.UntrackAsync(root, ["a.txt"])).ExitCode);
        Assert.True(File.Exists(a));
        Assert.False(await GitService.IsTrackedAsync(root, "a.txt"));
    }
}
