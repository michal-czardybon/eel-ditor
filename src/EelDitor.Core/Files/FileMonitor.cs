using System.Collections.Concurrent;

namespace EelDitor.Core.Files;

/// <summary>
/// Watches open files for external changes. Uses one <see cref="FileSystemWatcher"/> per directory
/// (debounced), plus <see cref="Poll"/> which callers run when the window is activated, because
/// watchers are unreliable on network drives and some editors replace files via rename.
/// </summary>
public sealed class FileMonitor : IDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _files = new(PathComparer);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new(PathComparer);
    private readonly TimeSpan _delay;

    public FileMonitor(TimeSpan? debounce = null) => _delay = debounce ?? TimeSpan.FromMilliseconds(300);

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Raised (on a thread pool thread) with the full path of a watched file that changed, appeared or disappeared.</summary>
    public event Action<string>? Changed;

    public void Watch(string path)
    {
        path = Path.GetFullPath(path);
        if (!_files.TryAdd(path, 0)) return;
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return;
        _watchers.GetOrAdd(dir, d =>
        {
            var w = new FileSystemWatcher(d)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Renamed += (s, e) => { OnPath(e.OldFullPath); OnPath(e.FullPath); };
            w.Error += (_, _) => { /* buffer overflow etc.: Poll() on activation still catches changes */ };
            try { w.EnableRaisingEvents = true; } catch (Exception ex) when (ex is IOException or ArgumentException) { }
            return w;
        });
    }

    public void Unwatch(string path)
    {
        path = Path.GetFullPath(path);
        if (!_files.TryRemove(path, out _)) return;
        string? dir = Path.GetDirectoryName(path);
        if (dir is null) return;
        bool stillUsed = _files.Keys.Any(f => PathComparer.Equals(Path.GetDirectoryName(f), dir));
        if (!stillUsed && _watchers.TryRemove(dir, out var w)) w.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => OnPath(e.FullPath);

    private void OnPath(string path)
    {
        if (!_files.ContainsKey(path)) return;
        var cts = new CancellationTokenSource();
        var old = _debounce.AddOrUpdate(path, cts, (_, prev) => { prev.Cancel(); return cts; });
        _ = Task.Delay(_delay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _debounce.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cts));
            Changed?.Invoke(path);
        }, TaskScheduler.Default);
    }

    /// <summary>Snapshot of a file's identity used to detect changes.</summary>
    public readonly record struct Stamp(bool Exists, DateTime LastWriteUtc, long Length);

    public static Stamp GetStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new Stamp(true, fi.LastWriteTimeUtc, fi.Length) : new Stamp(false, default, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Stamp(false, default, 0);
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        foreach (var c in _debounce.Values) c.Cancel();
    }
}
