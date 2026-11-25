using System.Diagnostics;
using System.Text;

namespace EelDitor.Core.Git;

public sealed record GitStatus(string Root, string Branch, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>Number of changed (including untracked) files.</summary>
    public int ChangeCount => Files.Count;

    /// <summary>Every changed path with its index (staged) and work-tree (unstaged) state.</summary>
    public IReadOnlyList<GitEntry> Entries { get; init; } = [];

    /// <summary>Commits not yet pushed / not yet pulled (0 when there is no upstream).</summary>
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool HasUpstream { get; init; }
}

/// <summary>One line of <c>git status --porcelain</c>: X = index (staged), Y = work tree (unstaged).</summary>
public sealed record GitEntry(string FullPath, string RelativePath, char Index, char WorkTree, string? OriginalPath = null)
{
    public bool IsUntracked => Index == '?' && WorkTree == '?';
    public bool IsConflict => Index == 'U' || WorkTree == 'U' || (Index == 'A' && WorkTree == 'A') || (Index == 'D' && WorkTree == 'D');
    public bool HasStaged => !IsUntracked && !IsConflict && Index is not ' ' and not '?';
    public bool HasUnstaged => IsUntracked || IsConflict || WorkTree is not ' ';
}

public sealed record GitCommit(string Hash, string Author, DateTimeOffset Date, string Subject, string PathAtCommit)
{
    public string ShortHash => Hash.Length > 8 ? Hash[..8] : Hash;
}

public sealed record GitCommitFile(string Status, string Path, string? OriginalPath = null);

/// <summary>
/// Thin wrapper over the <c>git</c> command line. Runs at below-normal priority so it never competes with
/// typing, and works with any git installation on PATH (no native libgit2 dependency).
/// </summary>
public static class GitService
{
    private static bool? _available;

    private static Task<bool>? _probe;

    /// <summary>Whether git can be run; probed once on a background thread (starting a process can be slow).</summary>
    public static Task<bool> AvailableAsync() => _probe ??= Task.Run(() => _available ??= Probe());

    /// <summary>Blocking check; do not call on the UI thread (use <see cref="AvailableAsync"/> or <see cref="KnownAvailable"/>).</summary>
    public static bool IsAvailable => AvailableAsync().GetAwaiter().GetResult();

    /// <summary>False only once git is known to be missing; true while the probe is still running.</summary>
    public static bool KnownAvailable => _available ?? true;

    private static bool Probe()
    {
        try { return Run(Environment.CurrentDirectory, ["--version"], CancellationToken.None).GetAwaiter().GetResult().ExitCode == 0; }
        catch (Exception) { return false; }
    }

    /// <summary>The working tree root containing <paramref name="path"/>, or null.</summary>
    public static string? FindRoot(string path)
    {
        string? dir = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : Path.GetFullPath(path);
        while (dir is not null)
        {
            string git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    public static async Task<GitStatus?> StatusAsync(string root, CancellationToken ct = default)
    {
        if (!await AvailableAsync().ConfigureAwait(false)) return null;
        var r = await Run(root, ["status", "--porcelain=v1", "-z", "--branch", "--untracked-files=all"], ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return null;
        string output = Encoding.UTF8.GetString(r.Output);
        var files = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var list = new List<GitEntry>();
        string branch = "";
        int ahead = 0, behind = 0;
        bool upstream = false;
        var entries = output.Split('\0');
        for (int i = 0; i < entries.Length; i++)
        {
            string e = entries[i];
            if (e.Length < 3) continue;
            if (e.StartsWith("## "))
            {
                branch = e[3..];
                var track = System.Text.RegularExpressions.Regex.Match(branch, @"\[(?:ahead (\d+))?(?:, )?(?:behind (\d+))?\]$");
                if (track.Success)
                {
                    if (track.Groups[1].Success) ahead = int.Parse(track.Groups[1].Value);
                    if (track.Groups[2].Success) behind = int.Parse(track.Groups[2].Value);
                }
                int dots = branch.IndexOf("...", StringComparison.Ordinal);
                if (dots >= 0) { branch = branch[..dots]; upstream = true; }
                if (branch.StartsWith("No commits yet on ")) branch = branch["No commits yet on ".Length..];
                continue;
            }
            char x = e[0], y = e[1];
            string rel = e[3..];
            string? original = null;
            if (x is 'R' or 'C') { i++; original = i < entries.Length ? entries[i] : null; } // the original path follows
            string full = Path.GetFullPath(Path.Combine(root, rel));
            files[full] = Letter(x, y);
            list.Add(new GitEntry(full, rel, x, y, original));
        }
        return new GitStatus(root, branch, files) { Entries = list, Ahead = ahead, Behind = behind, HasUpstream = upstream };
    }

    private static string Letter(char x, char y)
    {
        if (x == '?' && y == '?') return "?";
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return "U";
        if (x == 'R' || y == 'R') return "R";
        if (x == 'A') return "A";
        if (x == 'D' || y == 'D') return "D";
        return "M";
    }

    /// <summary>Human-readable state of a file for tooltips (the UI translates the keys).</summary>
    public static string StatusKey(string letter) => letter switch
    {
        "M" => "Git.Status.Modified",
        "A" => "Git.Status.Added",
        "?" => "Git.Status.Untracked",
        "D" => "Git.Status.Deleted",
        "R" => "Git.Status.Renamed",
        "U" => "Git.Status.Conflict",
        _ => "Git.Status.Clean",
    };

    /// <summary>True when git tracks the file (it is in the index or HEAD).</summary>
    public static async Task<bool> IsTrackedAsync(string root, string relativePath, CancellationToken ct = default)
    {
        var r = await Run(root, ["ls-files", "--error-unmatch", "--", relativePath], ct).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    public static Task<Result> StageAsync(string root, IEnumerable<string> paths) => RunLogged(root, ["add", "--", .. paths]);

    public static Task<Result> UnstageAsync(string root, IEnumerable<string> paths) => RunLogged(root, ["restore", "--staged", "--", .. paths]);

    /// <summary>Discards unstaged changes (the work tree gets the staged/HEAD version back).</summary>
    public static Task<Result> DiscardAsync(string root, IEnumerable<string> paths) => RunLogged(root, ["restore", "--worktree", "--", .. paths]);

    /// <summary>Stops tracking the files but keeps them on disk.</summary>
    public static Task<Result> UntrackAsync(string root, IEnumerable<string> paths) => RunLogged(root, ["rm", "--cached", "--quiet", "--", .. paths]);

    public static Task<Result> CommitAsync(string root, string message) => RunLogged(root, ["commit", "-m", message]);

    public static Task<Result> PushAsync(string root, CancellationToken ct = default) => RunLogged(root, ["push"], ct);

    /// <summary>
    /// Pull that never merges: only a fast-forward (<c>--ff-only</c>). When the local branch and the remote one both
    /// have new commits, nothing changes and git says so; no merge commit, no conflicts in the files.
    /// </summary>
    public static Task<Result> PullAsync(string root, CancellationToken ct = default) => RunLogged(root, ["pull", "--ff-only"], ct);

    /// <summary>A git command the user started (stage, commit, push…), for the console: what ran and what it said.</summary>
    public sealed class Command(string root, string commandLine)
    {
        public string Root { get; } = root;
        public string CommandLine { get; } = commandLine;
        public DateTime Started { get; } = DateTime.Now;

        /// <summary>Null while it runs.</summary>
        public Result? Result { get; internal set; }
    }

    /// <summary>Raised when a command the user started begins and when it ends (on the thread that awaits it).</summary>
    public static event Action<Command>? CommandStarted, CommandFinished;

    private static async Task<Result> RunLogged(string root, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var command = new Command(root, "git " + string.Join(' ', args.Select(Quote)));
        CommandStarted?.Invoke(command);
        Result r;
        try { r = await Run(root, args, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            r = new Result(-1, [], ex.Message);
        }
        command.Result = r;
        CommandFinished?.Invoke(command);
        return r;
    }

    /// <summary>An argument as typed in a shell (quoted when it has spaces or quotes).</summary>
    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>File contents at a revision (e.g. "HEAD", ":" for the index, or a commit hash); null if absent.</summary>
    public static async Task<byte[]?> ShowAsync(string root, string revision, string relativePath, CancellationToken ct = default)
    {
        if (!await AvailableAsync().ConfigureAwait(false)) return null;
        string spec = revision == ":" ? ":" + relativePath : $"{revision}:{relativePath}";
        var r = await Run(root, ["show", spec], ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? r.Output : null;
    }

    public static async Task<IReadOnlyList<GitCommit>> LogAsync(string root, string? relativePath = null, int max = 500, CancellationToken ct = default)
    {
        if (!await AvailableAsync().ConfigureAwait(false)) return [];
        string[] args = relativePath is null
            ? ["log", "--topo-order", "-z", $"-n{max}", "--format=%H%x00%an%x00%aI%x00%s"]
            : ["log", "--follow", "--name-only", $"-n{max}", "--date=iso-strict",
                "--format=%x1e%H%x1f%an%x1f%ad%x1f%s", "--", relativePath];
        var r = await Run(root, args, ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return [];
        var commits = new List<GitCommit>();
        if (relativePath is null)
        {
            var fields = Encoding.UTF8.GetString(r.Output).Split('\0');
            for (int i = 0; i + 3 < fields.Length; i += 4)
            {
                DateTimeOffset.TryParse(fields[i + 2], out var date);
                commits.Add(new GitCommit(fields[i], fields[i + 1], date, fields[i + 3], ""));
            }
            return commits;
        }
        foreach (var record in Encoding.UTF8.GetString(r.Output).Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;
            var f = lines[0].Split('\x1f');
            if (f.Length < 4) continue;
            DateTimeOffset.TryParse(f[2], out var date);
            string path = lines.Length > 1 ? lines[^1] : relativePath;
            commits.Add(new GitCommit(f[0], f[1], date, f[3], path));
        }
        return commits;
    }

    /// <summary>Files changed against the first parent, or added in the initial commit.</summary>
    public static async Task<IReadOnlyList<GitCommitFile>?> CommitFilesAsync(string root, string hash, CancellationToken ct = default)
    {
        var r = await Run(root, ["diff-tree", "--root", "--no-commit-id", "--name-status", "-z", "-r", "-M", "--first-parent", "-m", hash, "--"], ct)
            .ConfigureAwait(false);
        if (r.ExitCode != 0) return null;
        var files = new List<GitCommitFile>();
        var fields = Encoding.UTF8.GetString(r.Output).Split('\0');
        for (int i = 0; i + 1 < fields.Length;)
        {
            string status = fields[i++];
            string path = fields[i++];
            if (status.Length == 0 || path.Length == 0) break;
            if (status[0] is 'R' or 'C' && i < fields.Length && fields[i].Length > 0)
                files.Add(new GitCommitFile(status, fields[i++], path));
            else files.Add(new GitCommitFile(status, path));
        }
        return files;
    }

    /// <summary>The first parent of a commit, or null for a root commit.</summary>
    public static async Task<string?> ParentAsync(string root, string hash, CancellationToken ct = default)
    {
        var r = await Run(root, ["rev-parse", "--verify", "--quiet", hash + "^"], ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? Encoding.UTF8.GetString(r.Output).Trim() : null;
    }

    public readonly record struct Result(int ExitCode, byte[] Output, string Error);

    /// <summary>Runs git on a thread-pool thread: starting a process can take long (antivirus), never on the UI thread.</summary>
    public static Task<Result> Run(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) =>
        Task.Run(() => RunCore(workingDirectory, args, ct), ct);

    private static async Task<Result> RunCore(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        psi.ArgumentList.Add("--no-pager");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // status must not take the index lock
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never wait for a password on a hidden console (push)
        psi.Environment["LC_ALL"] = "C";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var err = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(copy, err).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (Exception) { }
            throw;
        }
        return new Result(p.ExitCode, ms.ToArray(), await err.ConfigureAwait(false));
    }
}
