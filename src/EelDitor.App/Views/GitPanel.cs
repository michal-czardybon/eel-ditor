using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EelDitor.App.Services;
using EelDitor.Core.Git;

namespace EelDitor.App.Views;

/// <summary>A changed file in one of the two lists of the git panel.</summary>
public sealed record GitRow(GitEntry Entry, bool Staged)
{
    public char State => Staged ? Entry.Index : Entry.IsUntracked ? '?' : Entry.IsConflict ? 'U' : Entry.WorkTree;

    /// <summary>VS Code style letter: U = untracked (new), C = conflict.</summary>
    public string Letter => State switch { '?' => "U", 'U' => "C", var c => c.ToString() };

    public string Name => Path.GetFileName(Entry.RelativePath);

    public string Folder => Path.GetDirectoryName(Entry.RelativePath)?.Replace('\\', '/') ?? "";

    public string BrushKey => State switch
    {
        'A' or '?' => "GitAddedBrush",
        'D' => "GitDeletedBrush",
        'U' => "GitConflictBrush",
        _ => "GitModifiedBrush",
    };

    public string StatusKey => GitService.StatusKey(State switch { 'A' => "A", '?' => "?", 'D' => "D", 'R' or 'C' => "R", 'U' => "U", _ => "M" });
}

/// <summary>
/// Sidebar tab with the git changes of the repository of the current file: unstaged and staged lists
/// (click shows the change in the comparison view), commit message, and Rescan / Commit / Push.
/// </summary>
public sealed class GitPanel : DockPanel
{
    // Shortened by whole leading folders ("…\src\Project"), like the folders of the open files.
    private readonly PathTextBlock _repo = new() { FontWeight = FontWeight.SemiBold, FontSize = 13 };
    private readonly TextBlock _branch = new() { FontSize = 12 };
    private readonly TextBox _message = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, FontSize = 12,
        Watermark = Loc.T("Git.MessageWatermark"),
    };
    private readonly ListBox _unstaged = List();
    private readonly ListBox _staged = List();
    private readonly TextBlock _unstagedHeader = Header();
    private readonly TextBlock _stagedHeader = Header();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(8, 4) };
    private readonly Button _commit = new() { Content = Loc.T("Git.Commit"), Classes = { "accent" } };
    private readonly Button _push = new() { Content = Loc.T("Git.Push") };
    private readonly Grid _body;

    public GitPanel()
    {
        _info.Res(TextBlock.ForegroundProperty, "DimTextBrush");
        _branch.Res(TextBlock.ForegroundProperty, "DimTextBrush");

        var rescan = new Button { Content = Loc.T("Git.Rescan") };
        ToolTip.SetTip(rescan, "F5");
        rescan.Click += (_, _) => RescanRequested?.Invoke();
        ToolTip.SetTip(_commit, "Ctrl+Enter");
        _commit.Click += (_, _) => RequestCommit();
        _push.Click += (_, _) => PushRequested?.Invoke();

        // The repository, then the actions as a mini toolbar across the panel, then the commit message.
        var top = new StackPanel { Margin = new Thickness(8, 8, 8, 6), Spacing = 4 };
        top.Children.Add(_repo);
        top.Children.Add(_branch);
        SetDock(top, Dock.Top);
        Children.Add(top);
        var actions = ButtonBar.Strip([rescan, _commit, _push]);
        SetDock(actions, Dock.Top);
        Children.Add(actions);
        _message.Margin = new Thickness(8, 6, 8, 4);
        SetDock(_message, Dock.Top);
        Children.Add(_message);
        SetDock(_info, Dock.Bottom);
        Children.Add(_info);

        _body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,*") };
        AddSection(0, _unstagedHeader, "Git.StageAll", () => StageRequested?.Invoke(Rows(_unstaged, all: true)), _unstaged);
        AddSection(2, _stagedHeader, "Git.UnstageAll", () => UnstageRequested?.Invoke(Rows(_staged, all: true)), _staged);
        Children.Add(_body);

        _message.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { RequestCommit(); e.Handled = true; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.F5) { RescanRequested?.Invoke(); e.Handled = true; } };
        Show(null);
    }

    public event Action? RescanRequested;
    public event Action<string>? CommitRequested;
    public event Action? PushRequested;
    public event Action<GitRow>? ShowChangesRequested;
    public event Action<GitRow>? OpenRequested;
    public event Action<IReadOnlyList<GitRow>>? StageRequested;
    public event Action<IReadOnlyList<GitRow>>? UnstageRequested;
    public event Action<IReadOnlyList<GitRow>>? DiscardRequested;

    public string CommitMessage { get => _message.Text ?? ""; set => _message.Text = value; }

    public string? Root { get; private set; }

    /// <summary>Shows the repository state (null = the current file is not in a repository).</summary>
    public void Show(GitStatus? status, string? info = null)
    {
        Root = status?.Root;
        _body.IsVisible = status is not null;
        _commit.IsEnabled = status?.Entries.Any(e => e.HasStaged) == true;
        _push.IsEnabled = status is not null;
        if (status is null)
        {
            _repo.FullPath = Loc.T("Git.NoRepository");
            ToolTip.SetTip(_repo, null);
            _branch.Text = "";
            _unstaged.ItemsSource = _staged.ItemsSource = null;
            _info.Text = info ?? "";
            return;
        }
        _repo.FullPath = status.Root;
        ToolTip.SetTip(_repo, status.Root);
        string tracking = !status.HasUpstream ? Loc.T("Git.NoUpstream")
            : status.Ahead == 0 && status.Behind == 0 ? Loc.T("Git.UpToDate")
            : $"↑{status.Ahead} ↓{status.Behind}";
        _branch.Text = $"⎇ {status.Branch}   {tracking}";
        var unstaged = status.Entries.Where(e => e.HasUnstaged).Select(e => new GitRow(e, false)).ToList();
        var staged = status.Entries.Where(e => e.HasStaged).Select(e => new GitRow(e, true)).ToList();
        _unstaged.ItemsSource = unstaged;
        _staged.ItemsSource = staged;
        _unstagedHeader.Text = Loc.T("Git.Unstaged", unstaged.Count);
        _stagedHeader.Text = Loc.T("Git.Staged", staged.Count);
        if (info is not null) _info.Text = info;
    }

    public void SetInfo(string text) => _info.Text = text;

    public void FocusMessage() => _message.Focus();

    private void RequestCommit()
    {
        if (string.IsNullOrWhiteSpace(CommitMessage)) { _message.Focus(); SetInfo(Loc.T("Git.MessageRequired")); return; }
        if (!_commit.IsEnabled) { SetInfo(Loc.T("Git.NothingStaged")); return; }
        CommitRequested?.Invoke(CommitMessage.Trim());
    }

    private static IReadOnlyList<GitRow> Rows(ListBox list, bool all = false) =>
        all ? (list.ItemsSource as IEnumerable<GitRow>)?.ToList() ?? [] : list.SelectedItems?.OfType<GitRow>().ToList() ?? [];

    private void AddSection(int row, TextBlock header, string allKey, Action all, ListBox list)
    {
        var allButton = new Button { Content = Loc.T(allKey), FontSize = 11, Padding = new Thickness(6, 1), Background = Brushes.Transparent };
        allButton.Click += (_, _) => all();
        var head = new DockPanel { Margin = new Thickness(8, 6, 4, 2) };
        SetDock(allButton, Dock.Right);
        head.Children.Add(allButton);
        head.Children.Add(header);
        Grid.SetRow(head, row);
        Grid.SetRow(list, row + 1);
        _body.Children.Add(head);
        _body.Children.Add(list);

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItems?.Count == 1 && list.SelectedItem is GitRow r) ShowChangesRequested?.Invoke(r);
        };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is GitRow r) OpenRequested?.Invoke(r); };
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var rows = Rows(list);
            if (rows.Count == 0) return;
            MenuItem M(string key, Action a) { var m = new MenuItem { Header = Loc.T(key) }; m.Click += (_, _) => a(); return m; }
            if (rows[0].Staged) menu.Items.Add(M("Git.Unstage", () => UnstageRequested?.Invoke(rows)));
            else menu.Items.Add(M(rows.All(r => r.Entry.IsUntracked) ? "Git.Add" : "Git.Stage", () => StageRequested?.Invoke(rows)));
            if (!rows[0].Staged && rows.Any(r => !r.Entry.IsUntracked))
                menu.Items.Add(M("Git.Discard", () => DiscardRequested?.Invoke(rows.Where(r => !r.Entry.IsUntracked).ToList())));
            menu.Items.Add(new Separator());
            menu.Items.Add(M("Git.ShowChanges", () => ShowChangesRequested?.Invoke(rows[0])));
            menu.Items.Add(M("Git.OpenFile", () => OpenRequested?.Invoke(rows[0])));
        };
        list.ContextMenu = menu;
    }

    private static TextBlock Header() => new() { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private static ListBox List() => new()
    {
        Background = Brushes.Transparent,
        SelectionMode = SelectionMode.Multiple,
        ItemTemplate = new FuncDataTemplate<GitRow?>((r, _) =>
        {
            if (r is null) return new TextBlock();
            var letter = new TextBlock { Text = r.Letter, FontWeight = FontWeight.SemiBold, Width = 16, FontSize = 12 }.Res(TextBlock.ForegroundProperty, r.BrushKey);
            var name = new TextBlock { Text = r.Name, FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
            var folder = new PathTextBlock { FullPath = r.Folder, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                .Res(TextBlock.ForegroundProperty, "DimTextBrush");
            var row = new DockPanel { Background = Brushes.Transparent };
            SetDock(letter, Dock.Left);
            SetDock(name, Dock.Left);
            row.Children.Add(letter);
            row.Children.Add(name);
            row.Children.Add(folder);
            ToolTip.SetTip(row, $"{r.Entry.RelativePath} — {Loc.T(r.StatusKey)}");
            return row;
        }),
    };
}
