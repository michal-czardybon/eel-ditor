using System.Collections.ObjectModel;
using EelDitor.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EelDitor.App.Documents;

/// <summary>A directory in the open-files panel.</summary>
public sealed partial class FileGroup : ObservableObject
{
    public FileGroup(string directory, string display)
    {
        Directory = directory;
        Display = display;
    }

    public string Directory { get; }

    public string Display { get; }

    /// <summary>Files directly in this directory.</summary>
    public ObservableCollection<EditorDocument> Files { get; } = [];

    /// <summary>Groups of open directories inside this one.</summary>
    public List<FileGroup> Children { get; } = [];

    public FileGroup? Parent { get; set; }

    /// <summary>What the tree shows under this group: subdirectories first, then files.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Files of this group and of all nested groups.</summary>
    public IEnumerable<EditorDocument> AllFiles => Files.Concat(Children.SelectMany(c => c.AllFiles));

    /// <summary>This group and all nested groups, depth first.</summary>
    public IEnumerable<FileGroup> SelfAndDescendants => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants));

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Repository info shown next to the directory (branch, change count).</summary>
    [ObservableProperty]
    private string _badge = "";

    /// <summary>Explains the badge ("git repository, branch main, 3 changed files").</summary>
    [ObservableProperty]
    private string? _badgeTooltip;

    [ObservableProperty]
    private bool _isRepository;

    public string Tooltip => string.IsNullOrEmpty(Directory) ? Display : Directory;
}

/// <summary>How the open-files panel lists the files.</summary>
public enum FileListMode
{
    /// <summary>Grouped by directory (nested directories nested).</summary>
    Folders,
    /// <summary>One flat list, by file name.</summary>
    Name,
    /// <summary>One flat list, the most recently modified (on disk) first.</summary>
    Modified,
}

/// <summary>
/// The list of open documents grouped by directory, with filtering and most-recently-used order.
/// Replaces tabs: scales to many files because groups collapse and the filter narrows instantly.
/// </summary>
public sealed partial class OpenFilesModel : ObservableObject
{
    private readonly List<EditorDocument> _documents = [];
    private readonly Dictionary<string, bool> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private long _activationCounter;

    public IReadOnlyList<EditorDocument> Documents => _documents;

    /// <summary>The directory groups (empty in a flat list).</summary>
    public ObservableCollection<FileGroup> Groups { get; } = [];

    /// <summary>What the panel's tree shows at the top: the groups, or the documents of a flat list.</summary>
    public ObservableCollection<object> TopItems { get; } = [];

    private readonly List<EditorDocument> _flat = [];

    /// <summary>Grouped by folder, or a flat list by name or by modification time.</summary>
    public FileListMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Rebuild();
            OnPropertyChanged();
        }
    }

    private FileListMode _mode;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private EditorDocument? _active;

    public bool SortByName { get; set; } = true;

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnActiveChanged(EditorDocument? oldValue, EditorDocument? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.LastActivated = ++_activationCounter;
        }
    }

    public void Add(EditorDocument doc)
    {
        _documents.Add(doc);
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorDocument.FilePath)) Rebuild();
        };
        Rebuild();
    }

    public void Remove(EditorDocument doc)
    {
        _documents.Remove(doc);
        Rebuild();
    }

    public EditorDocument? FindByPath(string path)
    {
        string full = Path.GetFullPath(path);
        return _documents.FirstOrDefault(d => d.FilePath is not null &&
            string.Equals(d.FilePath, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    /// <summary>Documents ordered by most recent activation (for Ctrl+Tab).</summary>
    public IReadOnlyList<EditorDocument> MostRecentlyUsed() =>
        _documents.OrderByDescending(d => d.LastActivated).ToList();

    /// <summary>Documents in the order they appear in the panel.</summary>
    public IEnumerable<EditorDocument> VisualOrder() => Mode == FileListMode.Folders ? AllGroups().SelectMany(g => g.Files) : _flat;

    /// <summary>Every group, nested ones included, in tree order.</summary>
    public IEnumerable<FileGroup> AllGroups() => Groups.SelectMany(g => g.SelfAndDescendants);

    public void SetGroupExpanded(FileGroup group, bool expanded)
    {
        group.IsExpanded = expanded;
        _collapsed[group.Directory] = !expanded;
    }

    public void Rebuild()
    {
        foreach (var g in AllGroups()) _collapsed[g.Directory] = !g.IsExpanded;

        var filtered = string.IsNullOrWhiteSpace(Filter)
            ? _documents
            : _documents.Where(d => FuzzyMatch(Filter, d)).ToList();

        _flat.Clear();
        if (Mode != FileListMode.Folders)
        {
            // Pinned files first, as in the groups; files without a name (new) after the others.
            var ordered = filtered.OrderByDescending(d => d.Pinned);
            ordered = Mode == FileListMode.Name
                ? ordered.ThenBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.DirectoryPath, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(d => d.FilePath is null ? DateTime.MinValue : d.LastWriteTimeUtc)
                    .ThenBy(d => d.FileName, StringComparer.OrdinalIgnoreCase);
            _flat.AddRange(ordered);
            Groups.Clear();
            TopItems.Clear();
            foreach (var d in _flat) TopItems.Add(d);
            GroupsRebuilt?.Invoke(this, EventArgs.Empty);
            return;
        }

        var grouped = filtered
            .GroupBy(d => d.DirectoryPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(g => g.Key.Length == 0 ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        // Directories inside other open directories become nested groups (no groups for directories without open files).
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var all = new List<FileGroup>();
        foreach (var g in grouped)
        {
            var parent = g.Key.Length == 0 ? null : all.Where(p => p.Directory.Length > 0 && IsInside(g.Key, p.Directory, comparison))
                .MaxBy(p => p.Directory.Length);
            string display = g.Key.Length == 0 ? Loc.T("Panel.NewFiles")
                : parent is not null ? Path.GetRelativePath(parent.Directory, g.Key)
                : HomeRelative(g.Key);
            var group = new FileGroup(g.Key, display) { Parent = parent };
            group.IsExpanded = !string.IsNullOrWhiteSpace(Filter) || !_collapsed.GetValueOrDefault(g.Key);
            var files = SortByName
                ? g.OrderByDescending(d => d.Pinned).ThenBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
                : g.OrderByDescending(d => d.Pinned).ThenBy(d => _documents.IndexOf(d));
            foreach (var d in files) group.Files.Add(d);
            parent?.Children.Add(group);
            all.Add(group);
        }
        foreach (var group in all)
        {
            foreach (var c in group.Children) group.Items.Add(c);
            foreach (var f in group.Files) group.Items.Add(f);
        }
        Groups.Clear();
        TopItems.Clear();
        foreach (var group in all.Where(g => g.Parent is null)) { Groups.Add(group); TopItems.Add(group); }
        GroupsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? GroupsRebuilt;

    private static bool IsInside(string path, string directory, StringComparison comparison)
    {
        string d = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Length > d.Length + 1 && path.StartsWith(d, comparison)
            && (path[d.Length] == Path.DirectorySeparatorChar || path[d.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>Subsequence match on file name, or plain substring match on the full path.</summary>
    public static bool FuzzyMatch(string pattern, EditorDocument doc)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return true;
        string name = doc.FileName;
        if (doc.FilePath?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true) return true;
        int pi = 0;
        foreach (char c in name)
            if (pi < pattern.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(pattern[pi])) pi++;
        return pi == pattern.Length;
    }

    /// <summary>Replaces the home directory with ~ and elides the middle of very long paths.</summary>
    /// <summary>The path with the home directory written as "~"; the panel trims it from the left when narrow.</summary>
    public static string HomeRelative(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    public static string ShortenPath(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            path = "~" + path[home.Length..];
        const int max = 48;
        if (path.Length <= max) return path;
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length <= 3) return path;
        string head = parts[0], tail = parts[^1];
        int i = parts.Length - 2;
        while (i > 1 && (head.Length + tail.Length + parts[i].Length + 5) <= max)
        {
            tail = parts[i] + Path.DirectorySeparatorChar + tail;
            i--;
        }
        return head + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + tail;
    }
}
