using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.Core.Git;
using EelDitor.Core.Text;
using TextMateSharp.Grammars;

namespace EelDitor.App.Views;

public partial class MainWindow
{
    private DiffView? _diffView;
    private HeadComparison? _headComparison;
    private int _headRefreshVersion;

    private sealed record HeadComparison(string Root, string Path, EditorDocument? Document, string? OriginalPath = null);

    internal DiffView? CurrentDiffView => _diffView;
    private DiffLinePanel? _diffLinePanel;

    /// <summary>Opens a comparison with a read-only left side (HEAD, disk version, commit).</summary>
    internal void OpenDiff(string leftTitle, string leftText, string rightTitle, TextDocument right, bool rightEditable, string? path) =>
        OpenDiff(leftTitle, DiffView.ReadOnlyDocument(leftText), false, rightTitle, right, rightEditable, path);

    internal void OpenDiff(string leftTitle, TextDocument left, bool leftEditable, string rightTitle, TextDocument right, bool rightEditable, string? path)
    {
        CloseDiff();
        var view = new DiffView(leftTitle, left, leftEditable, rightTitle, right, rightEditable, Palette, Editor.FontFamily, Editor.FontSize,
            path is null ? null : Path.GetExtension(path),
            Palette == EelDitor.App.Editor.EditorPalette.Light ? ThemeName.LightPlus : ThemeName.DarkPlus);
        view.CloseRequested += CloseDiff;
        view.SyncScroll = _settings.DiffSyncScroll;
        view.SyncScrollChanged += on => { _settings.DiffSyncScroll = on; RefreshCommandStates(); };
        view.WordMode = _settings.DiffWordMode;
        // The word view shows both versions in one text: the panel with the current line of each side is not needed.
        view.WordModeChanged += on => { _settings.DiffWordMode = on; DiffLineHost.IsVisible = !on; FollowDiffInMap(view); RefreshCommandStates(); };
        // The map follows the comparison and marks its changed lines.
        view.CurrentLineChanged += () => _documentMap?.RefreshChanges();
        _diffView = view;
        UpdateDocumentHeader();
        Editor.IsVisible = false;
        EditorHost.Children.Add(view);

        var linePanel = new DiffLinePanel();
        linePanel.SetFont(Editor.FontFamily, Editor.FontSize);
        linePanel.PanelHeight = _settings.DiffLinePanelHeight;
        linePanel.PanelHeightChanged += h => _settings.DiffLinePanelHeight = h;
        view.CurrentLineChanged += () => linePanel.Show(view);
        _diffLinePanel = linePanel;
        DiffLineHost.Child = linePanel;
        DiffLineHost.IsVisible = !view.WordMode;
        linePanel.Show(view);
        FollowDiffInMap(view);
        UpdateDiffTools();
        RefreshCommandStates();
    }

    /// <summary>The document map shows what the comparison shows (the working side or the word view), with its changes.</summary>
    private void FollowDiffInMap(DiffView view) => _documentMap?.Follow(view.VisibleEditor, view.ChangedLines);

    internal void CloseDiff()
    {
        if (_diffView is null) return;
        int caret = _diffView.RightEditor.CaretOffset;
        bool sameDoc = ReferenceEquals(_diffView.RightEditor.Document, Editor.Document);
        EditorHost.Children.Remove(_diffView);
        _diffView = null;
        _headComparison = null;
        _documentMap?.Follow(null);
        UpdateDocumentHeader();
        _diffLinePanel = null;
        DiffLineHost.Child = null;
        DiffLineHost.IsVisible = false;
        UpdateDiffTools();
        Editor.IsVisible = true;
        if (sameDoc) Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
        RefreshCommandStates();
    }

    private static string DecodeFor(EditorDocument doc, byte[] bytes) =>
        TextFile.Decode(bytes, doc.Encoding.IsUnicode ? null : doc.Encoding).Text;

    private async Task CompareWithHeadAsync(EditorDocument doc)
    {
        if (doc.FilePath is null) return;
        if (!await GitService.AvailableAsync()) { await Dialogs.Info(this, AppInfo.Name, Loc.T("Git.NotAvailable")); return; }
        string? root = GitService.FindRoot(doc.FilePath);
        if (root is null) { await Dialogs.Info(this, AppInfo.Name, Loc.T("Git.NotInRepo", doc.FileName)); return; }
        string rel = GitService.RelativePath(root, doc.FilePath);
        var bytes = await GitService.ShowAsync(root, "HEAD", rel);
        Activate(doc);
        OpenDiff($"HEAD: {rel}", bytes is null ? "" : DecodeFor(doc, bytes),
            Loc.T("Diff.Working", doc.FileName), doc.Document, rightEditable: true, doc.FilePath);
        _headComparison = new(root, rel, doc);
        if (bytes is null) SetStatusMessage(Loc.T("Git.NotInHead", rel));
    }

    private async Task RefreshHeadDiffAsync(string root)
    {
        if (_diffView is not { } view || _headComparison is not { } comparison
            || !RepositoryComparer.Equals(comparison.Root, root)) return;
        int version = ++_headRefreshVersion;
        var head = await GitService.ShowAsync(root, "HEAD", comparison.Path);
        if (head is null && comparison.OriginalPath is { } original)
            head = await GitService.ShowAsync(root, "HEAD", original);

        // An editable working side stays attached to its live document. A staged comparison has two snapshots.
        var index = comparison.Document is null ? await GitService.ShowAsync(root, ":", comparison.Path) : null;
        if (!ReferenceEquals(view, _diffView) || version != _headRefreshVersion) return;
        string Decode(byte[]? bytes) => bytes is null ? ""
            : comparison.Document is { } doc ? DecodeFor(doc, bytes) : TextFile.Decode(bytes).Text;
        string left = Decode(head);
        string? right = comparison.Document is null ? Decode(index) : null;
        await view.RefreshSnapshotsAsync(left, right);
    }

    private async Task CompareWithDiskAsync(EditorDocument doc)
    {
        if (doc.FilePath is null || !File.Exists(doc.FilePath)) return;
        var bytes = await File.ReadAllBytesAsync(doc.FilePath);
        Activate(doc);
        OpenDiff(Loc.T("Diff.OnDisk", doc.FileName), DecodeFor(doc, bytes), Loc.T("Diff.Editor", doc.FileName), doc.Document, true, doc.FilePath);
    }

    private async Task CompareWithFileAsync()
    {
        if (_active is null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("Menu.Compare.WithFile"), AllowMultiple = false });
        if (result.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        // A file that is already open is compared live, so changes can be copied into it too.
        if (_files.FindByPath(path) is { } open && !ReferenceEquals(open, _active))
        {
            OpenDiff(path, open.Document, true, _active.FilePath ?? _active.FileName, _active.Document, true, _active.FilePath ?? path);
            return;
        }
        string text = TextFile.Decode(await File.ReadAllBytesAsync(path)).Text;
        OpenDiff(path, text, _active.FilePath ?? _active.FileName, _active.Document, true, _active.FilePath ?? path);
    }

    private Task CompareDocumentsAsync(EditorDocument left, EditorDocument right)
    {
        Activate(right);
        OpenDiff(left.FilePath ?? left.FileName, left.Document, true, right.FilePath ?? right.FileName, right.Document, true, right.FilePath);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- history

    private async Task ShowHistoryAsync(EditorDocument doc)
    {
        if (doc.FilePath is null) return;
        if (!await GitService.AvailableAsync()) { await Dialogs.Info(this, AppInfo.Name, Loc.T("Git.NotAvailable")); return; }
        string? root = GitService.FindRoot(doc.FilePath);
        if (root is null) { await Dialogs.Info(this, AppInfo.Name, Loc.T("Git.NotInRepo", doc.FileName)); return; }
        string rel = GitService.RelativePath(root, doc.FilePath);
        var commits = await GitService.LogAsync(root, rel);
        var panel = new HistoryPanel(doc, root, rel, commits);
        panel.CloseRequested += () => SetResultsVisible(false);
        panel.CompareCommit += c => _ = CompareCommitAsync(doc, root, c);
        panel.CompareWithCurrent += c => _ = CompareCommitWithCurrentAsync(doc, root, c);
        panel.OpenRevision += c => _ = OpenRevisionAsync(doc, root, c);
        SetResultsVisible(true);
        ResultsHost.Child = panel;
    }

    private async Task CompareCommitAsync(EditorDocument doc, string root, GitCommit commit)
    {
        string? parent = await GitService.ParentAsync(root, commit.Hash);
        var after = await GitService.ShowAsync(root, commit.Hash, commit.PathAtCommit) ?? [];
        byte[] before = [];
        if (parent is not null)
        {
            // The file may have had another name in the parent (renames are followed by git log --follow).
            before = await GitService.ShowAsync(root, parent, commit.PathAtCommit) ?? [];
        }
        OpenDiff($"{(parent is null ? "∅" : parent[..8])}: {commit.PathAtCommit}", DecodeFor(doc, before),
            $"{commit.ShortHash}: {commit.Subject}", new TextDocument(DecodeFor(doc, after)), rightEditable: false, doc.FilePath);
    }

    private async Task CompareCommitWithCurrentAsync(EditorDocument doc, string root, GitCommit commit)
    {
        var bytes = await GitService.ShowAsync(root, commit.Hash, commit.PathAtCommit) ?? [];
        Activate(doc);
        OpenDiff($"{commit.ShortHash}: {commit.Subject}", DecodeFor(doc, bytes), Loc.T("Diff.Working", doc.FileName), doc.Document, true, doc.FilePath);
    }

    private async Task OpenRevisionAsync(EditorDocument doc, string root, GitCommit commit)
    {
        var bytes = await GitService.ShowAsync(root, commit.Hash, commit.PathAtCommit) ?? [];
        var loaded = TextFile.Decode(bytes, doc.Encoding.IsUnicode ? null : doc.Encoding);
        var rev = new EditorDocument(new TextDocument(loaded.Text), null)
        {
            RevisionPath = commit.PathAtCommit,
            Encoding = loaded.Encoding,
            Eol = loaded.Eol.Dominant(doc.Eol),
        };
        rev.PropertyChanged += OnDocumentPropertyChanged;
        _files.Add(rev);
        Activate(rev);
        SetStatusMessage(Loc.T("Git.RevisionOpened", commit.ShortHash, Path.GetFileName(commit.PathAtCommit)));
    }

    // ---------------------------------------------------------------- status in the files panel

    private readonly Dictionary<string, GitStatus> _gitStatus = new(RepositoryComparer);
    private readonly Dictionary<string, DispatcherTimer> _gitTimers = new(RepositoryComparer);

    private void RequestGitRefresh(EditorDocument doc)
    {
        if (doc.FilePath is null || !GitService.KnownAvailable) return;
        string? root = GitService.FindRoot(doc.FilePath);
        if (root is null) return;
        if (!_gitTimers.TryGetValue(root, out var timer))
        {
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await RefreshGitAsync(root);
            });
            _gitTimers[root] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    private void RequestGitRefreshAll()
    {
        foreach (var d in _files.Documents) RequestGitRefresh(d);
    }

    private void ApplyGitStatus()
    {
        foreach (var d in _files.Documents)
        {
            if (d.FilePath is null) { d.GitStatus = ""; d.GitRoot = null; continue; }
            var st = StatusFor(d.FilePath);
            d.GitRoot = st?.Root;
            d.GitStatus = st is not null && st.Files.TryGetValue(d.FilePath, out var letter) ? letter : "";
        }
        foreach (var g in _files.AllGroups())
        {
            var st = g.Directory.Length == 0 ? null : StatusFor(Path.Combine(g.Directory, "x"));
            // Show the branch once per repository: on the top-most group inside it.
            if (st is not null && g.Parent is { } parent && StatusFor(Path.Combine(parent.Directory, "x"))?.Root == st.Root)
            {
                g.IsRepository = false;
                g.Badge = "";
                g.BadgeTooltip = null;
                continue;
            }
            g.IsRepository = st is not null;
            g.Badge = st is null ? "" : st.ChangeCount > 0 ? $"{st.Branch} · {st.ChangeCount}" : st.Branch;
            g.BadgeTooltip = st is null ? null : Loc.T("Git.BadgeTooltip", st.Root, st.Branch, st.ChangeCount);
        }
        UpdateGitPanel();
        UpdateConflictBar();
    }
}

/// <summary>Git history of one file in the bottom panel.</summary>
public sealed class HistoryPanel : DockPanel
{
    private readonly ListBox _list = new() { Background = Brushes.Transparent, FontSize = 12 };

    public HistoryPanel(EditorDocument doc, string root, string relativePath, IReadOnlyList<GitCommit> commits)
    {
        var header = new DockPanel { Height = 26 }.Res(BackgroundProperty, "ChromeBrush");
        var close = new Button { Content = "✕", Padding = new Thickness(8, 0), Background = Brushes.Transparent };
        close.Click += (_, _) => CloseRequested?.Invoke();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void B(string key, Action<GitCommit> action)
        {
            var b = new Button { Content = Loc.T(key), FontSize = 12, Padding = new Thickness(8, 1), Background = Brushes.Transparent };
            b.Click += (_, _) => { if (_list.SelectedItem is GitCommit c) action(c); };
            buttons.Children.Add(b);
        }
        B("History.ShowChanges", c => CompareCommit?.Invoke(c));
        B("History.CompareCurrent", c => CompareWithCurrent?.Invoke(c));
        B("History.Open", c => OpenRevision?.Invoke(c));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock
        {
            Text = Loc.T("History.Title", relativePath, commits.Count),
            FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        _list.ItemTemplate = new FuncDataTemplate<GitCommit>((c, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,140,160,*") };
            void Cell(string text, int col, bool dim = false, FontFamily? font = null)
            {
                var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
                if (dim) t.Res(TextBlock.ForegroundProperty, "DimTextBrush");
                if (font is not null) t.FontFamily = font;
                Grid.SetColumn(t, col);
                g.Children.Add(t);
            }
            Cell(c.ShortHash, 0, true, new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"));
            Cell(c.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), 1, true);
            Cell(c.Author, 2, true);
            Cell(c.Subject, 3);
            return g;
        });
        _list.ItemsSource = commits;
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is GitCommit c) CompareCommit?.Invoke(c); };
        if (commits.Count == 0)
            Children.Add(new TextBlock { Text = Loc.T("History.Empty"), Margin = new Thickness(10) }.Res(TextBlock.ForegroundProperty, "DimTextBrush"));
        else
            Children.Add(_list);
    }

    public event Action? CloseRequested;
    public event Action<GitCommit>? CompareCommit;
    public event Action<GitCommit>? CompareWithCurrent;
    public event Action<GitCommit>? OpenRevision;
}
