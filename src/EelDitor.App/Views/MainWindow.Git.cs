using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Core.Git;
using EelDitor.Core.Text;

namespace EelDitor.App.Views;

public partial class MainWindow
{
    private GitPanel? _gitPanel;
    private DocumentMap? _documentMap;

    /// <summary>Creates the sidebar tabs' content: the document map under the file tree and the git panel.</summary>
    private void SetupSidebar()
    {
        _documentMap = new DocumentMap(Editor);
        _documentMap.ApplyPalette(Palette);
        MapHost.Child = _documentMap;

        var panel = _gitPanel = new GitPanel();
        panel.RescanRequested += () => { if (panel.Root is { } r) _ = RefreshGitAsync(r); else UpdateGitPanel(); };
        panel.StageRequested += rows => _ = GitOnRowsAsync(rows, GitService.StageAsync);
        panel.UnstageRequested += rows => _ = GitOnRowsAsync(rows, GitService.UnstageAsync);
        panel.DiscardRequested += rows => _ = DiscardRowsAsync(rows);
        panel.ShowChangesRequested += row => _ = ShowGitChangesAsync(row);
        panel.OpenRequested += row => _ = OpenFileAsync(row.Entry.FullPath);
        panel.CommitRequested += message => _ = CommitAsync(message);
        panel.PushRequested += () => _ = PushAsync();
        GitHost.Child = panel;
        // SelectionChanged bubbles: a click in a list inside a tab (e.g. the Git panel) must not count as a tab change.
        SideTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, SideTabs) && ReferenceEquals(SideTabs.SelectedItem, GitTab)) UpdateGitPanel(rescan: true); };
    }

    /// <summary>Shows a sidebar tab (and the sidebar itself if it is hidden).</summary>
    private void ShowSideTab(TabItem tab)
    {
        if (MainGrid.ColumnDefinitions[0].Width.Value == 0) ToggleSidebar();
        SideTabs.SelectedItem = tab;
    }

    /// <summary>The cached status of the repository containing <paramref name="path"/> (innermost repository wins).</summary>
    private GitStatus? StatusFor(string path) =>
        _gitStatus.Values.Where(s => path.StartsWith(s.Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Root.Length).FirstOrDefault();

    private async Task RefreshGitAsync(string root)
    {
        var status = await GitService.StatusAsync(root);
        if (status is null) _gitStatus.Remove(root);
        else _gitStatus[root] = status;
        ApplyGitStatus();
    }

    /// <summary>Shows the repository of the current file in the git tab.</summary>
    private void UpdateGitPanel(bool rescan = false)
    {
        if (_gitPanel is null || !ReferenceEquals(SideTabs.SelectedItem, GitTab)) return;
        string? root = _active?.FilePath is { } p && GitService.KnownAvailable ? GitService.FindRoot(p) : null;
        if (root is null)
        {
            _gitPanel.Show(null, GitService.KnownAvailable ? Loc.T("Git.NoRepositoryHint") : Loc.T("Git.NotAvailable"));
            return;
        }
        if (_gitStatus.TryGetValue(root, out var status)) _gitPanel.Show(status);
        if (rescan || status is null) _ = RefreshGitAsync(root);
    }

    // ---------------------------------------------------------------- operations

    private async Task GitOnRowsAsync(IReadOnlyList<GitRow> rows, Func<string, IEnumerable<string>, Task<GitService.Result>> op)
    {
        if (rows.Count == 0 || _gitPanel?.Root is not { } root) return;
        var r = await op(root, rows.Select(x => x.Entry.RelativePath));
        if (r.ExitCode != 0) await Dialogs.Info(this, "git", r.Error.Trim());
        await RefreshGitAsync(root);
    }

    private async Task DiscardRowsAsync(IReadOnlyList<GitRow> rows)
    {
        if (rows.Count == 0) return;
        string names = string.Join(Environment.NewLine, rows.Take(10).Select(r => r.Entry.RelativePath)) + (rows.Count > 10 ? Environment.NewLine + "…" : "");
        if (await Dialogs.Ask(this, "git", Loc.T("Git.ConfirmDiscard", names), Loc.T("Git.Discard"), Loc.T("Button.Cancel")) != 0) return;
        await GitOnRowsAsync(rows, GitService.DiscardAsync);
        PollFilesOnDisk(); // reload open files that git just changed
    }

    private async Task CommitAsync(string message)
    {
        if (_gitPanel?.Root is not { } root) return;
        if (_files.Documents.Any(d => d.IsDirty && d.FilePath?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true)
            && await Dialogs.Ask(this, "git", Loc.T("Git.UnsavedBeforeCommit"), Loc.T("Git.Commit"), Loc.T("Button.Cancel")) != 0)
            return;
        _gitPanel.SetInfo(Loc.T("Git.Committing"));
        var r = await GitService.CommitAsync(root, message);
        if (r.ExitCode != 0)
        {
            _gitPanel.SetInfo("");
            await Dialogs.Info(this, "git commit", (r.Error + Environment.NewLine + System.Text.Encoding.UTF8.GetString(r.Output)).Trim());
        }
        else
        {
            _gitPanel.CommitMessage = "";
            _gitPanel.SetInfo(Loc.T("Git.Committed"));
        }
        await RefreshGitAsync(root);
    }

    private async Task PushAsync()
    {
        if (_gitPanel?.Root is not { } root) return;
        _gitPanel.SetInfo(Loc.T("Git.Pushing"));
        var r = await GitService.PushAsync(root);
        string output = (r.Error + Environment.NewLine + System.Text.Encoding.UTF8.GetString(r.Output)).Trim();
        if (r.ExitCode != 0)
        {
            _gitPanel.SetInfo("");
            await Dialogs.Info(this, "git push", output);
        }
        else _gitPanel.SetInfo(Loc.T("Git.Pushed"));
        await RefreshGitAsync(root);
    }

    /// <summary>
    /// Shows a change from the git tab in the comparison view: unstaged = index ↔ work tree (the live document
    /// when the file is open), staged = HEAD ↔ index.
    /// </summary>
    private async Task ShowGitChangesAsync(GitRow row)
    {
        if (_gitPanel?.Root is not { } root) return;
        var e = row.Entry;
        if (e.IsConflict)
        {
            var doc = _files.FindByPath(e.FullPath) ?? await OpenFileAsync(e.FullPath);
            if (doc is not null) await CompareConflictSidesAsync(doc);
            return;
        }
        string Decode(byte[]? bytes) => bytes is null ? "" : TextFile.Decode(bytes).Text;
        string rel = e.RelativePath;
        if (row.Staged)
        {
            string head = Decode(await GitService.ShowAsync(root, "HEAD", e.OriginalPath ?? rel));
            string index = Decode(await GitService.ShowAsync(root, ":", rel));
            if (_active is null) return;
            OpenDiff($"HEAD: {rel}", DiffView.ReadOnlyDocument(head), false, $"{Loc.T("Git.Index")}: {rel}", DiffView.ReadOnlyDocument(index), false, e.FullPath);
            return;
        }
        byte[]? baseBytes = e.IsUntracked ? null : await GitService.ShowAsync(root, ":", rel) ?? await GitService.ShowAsync(root, "HEAD", rel);
        string baseText = Decode(baseBytes);
        if (_files.FindByPath(e.FullPath) is { } open)
        {
            Activate(open);
            OpenDiff($"{Loc.T("Git.Index")}: {rel}", baseText, Loc.T("Diff.Working", open.FileName), open.Document, true, e.FullPath);
            return;
        }
        string working = File.Exists(e.FullPath) ? TextFile.Decode(await File.ReadAllBytesAsync(e.FullPath)).Text : "";
        if (_active is null) NewDocument();
        OpenDiff($"{Loc.T("Git.Index")}: {rel}", DiffView.ReadOnlyDocument(baseText), false, Loc.T("Diff.Working", Path.GetFileName(rel)), DiffView.ReadOnlyDocument(working), false, e.FullPath);
    }

    // ---------------------------------------------------------------- file context menu: Git ›

    private MenuItem BuildGitMenu(EditorDocument doc)
    {
        var menu = new MenuItem { Header = "Git" };
        string? path = doc.FilePath;
        string? root = path is not null && GitService.KnownAvailable ? GitService.FindRoot(path) : null;
        if (path is null || root is null)
        {
            menu.IsEnabled = false;
            return menu;
        }
        string rel = GitService.RelativePath(root, path);
        string letter = doc.GitStatus;
        MenuItem M(string key, Func<Task> action, bool enabled = true)
        {
            var mi = new MenuItem { Header = Loc.T(key), IsEnabled = enabled };
            mi.Click += async (_, _) => { await action(); await RefreshGitAsync(root); };
            return mi;
        }
        async Task Run(Task<GitService.Result> op)
        {
            var r = await op;
            if (r.ExitCode != 0) await Dialogs.Info(this, "git", r.Error.Trim());
        }
        var entry = _gitStatus.GetValueOrDefault(root)?.Entries.FirstOrDefault(e => string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase));
        bool untracked = letter == "?";
        bool tracked = !untracked;

        menu.Items.Add(M("Menu.Compare.History", () => ShowHistoryAsync(doc)));
        menu.Items.Add(M("Menu.Compare.WithHead", () => CompareWithHeadAsync(doc), tracked));
        menu.Items.Add(new Separator());
        if (untracked) menu.Items.Add(M("Git.Add", () => Run(GitService.StageAsync(root, [rel]))));
        else menu.Items.Add(M("Git.Stage", () => Run(GitService.StageAsync(root, [rel])), entry?.HasUnstaged == true));
        menu.Items.Add(M("Git.Unstage", () => Run(GitService.UnstageAsync(root, [rel])), entry?.HasStaged == true));
        menu.Items.Add(M("Git.Revert", async () =>
        {
            if (doc.IsDirty && await Dialogs.Ask(this, "git", Loc.T("Git.RevertDiscardsEditor"), Loc.T("Git.Revert"), Loc.T("Button.Cancel")) != 0) return;
            if (await Dialogs.Ask(this, "git", Loc.T("Git.ConfirmDiscard", rel), Loc.T("Git.Revert"), Loc.T("Button.Cancel")) != 0) return;
            await Run(GitService.DiscardAsync(root, [rel]));
            await ReloadAsync(doc, doc.Encoding);
        }, tracked && entry?.WorkTree is not null and not ' '));
        menu.Items.Add(new Separator());
        menu.Items.Add(M("Git.Untrack", async () =>
        {
            if (await Dialogs.Ask(this, "git", Loc.T("Git.ConfirmUntrack", rel), Loc.T("Git.Untrack"), Loc.T("Button.Cancel")) != 0) return;
            await Run(GitService.UntrackAsync(root, [rel]));
        }, tracked && letter != "A"));
        menu.Items.Add(new Separator());
        var showTab = new MenuItem { Header = Loc.T("Git.ShowPanel") };
        showTab.Click += (_, _) => { Activate(doc); SideTabs.SelectedItem = GitTab; };
        menu.Items.Add(showTab);
        return menu;
    }

    // ---------------------------------------------------------------- console

    /// <summary>Opens a terminal in <paramref name="directory"/> (Windows Terminal, else PowerShell; Terminal on macOS; the default terminal on Linux).</summary>
    private void OpenConsole(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (FindOnPath("wt.exe") is { } wt)
                    Process.Start(new ProcessStartInfo(wt) { ArgumentList = { "-d", directory }, UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = directory, UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-a", "Terminal", directory } });
            else
                Process.Start(new ProcessStartInfo("x-terminal-emulator") { WorkingDirectory = directory, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatusMessage(ex.Message);
        }
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        string apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe);
        return File.Exists(apps) ? apps : null;
    }

    // ---------------------------------------------------------------- editor context menu

    /// <summary>Right-click menu of the editor: clipboard commands, and table operations inside a Markdown table.</summary>
    private void SetupEditorContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (ConflictAtCaret() is not null)
            {
                foreach (var id in new[] { "conflict.current", "conflict.incoming", "conflict.both" }) menu.Items.Add(Item(id, track: false));
                menu.Items.Add(new Separator());
            }
            foreach (var id in new[] { "edit.cut", "edit.copy", "edit.paste", "edit.selectAll" }) menu.Items.Add(Item(id, track: false));
            if (InTable)
            {
                menu.Items.Add(new Separator());
                foreach (var id in new[] { "md.table.rowAbove", "md.table.rowBelow", "md.table.colLeft", "md.table.colRight" }) menu.Items.Add(Item(id, track: false));
                menu.Items.Add(new Separator());
                foreach (var id in new[] { "md.table.delRow", "md.table.delCol" }) menu.Items.Add(Item(id, track: false));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("md.table.editCell", track: false));
                menu.Items.Add(new Separator());
                // Styles act on the selected cells (or the current one).
                var styles = Sub("Menu.Md.Style");
                foreach (var id in new[] { "md.bold", "md.italic", "md.strike", "md.code", "md.highlight", "md.sub", "md.sup" }) styles.Items.Add(Item(id, track: false));
                menu.Items.Add(styles);
                menu.Items.Add(Item("md.clearFormat", track: false));
                menu.Items.Add(new Separator());
                foreach (var id in new[] { "md.table.rowUp", "md.table.rowDown", "md.table.colMoveLeft", "md.table.colMoveRight" }) menu.Items.Add(Item(id, track: false));
                menu.Items.Add(new Separator());
                foreach (var id in new[] { "md.table.selRow", "md.table.selCol", "md.table.copyCol", "md.table.split", "md.table.format" }) menu.Items.Add(Item(id, track: false));
            }
            else if (InMarkdown)
            {
                menu.Items.Add(new Separator());
                foreach (var id in new[] { "md.bold", "md.italic", "md.strike", "md.code", "md.highlight", "md.sub", "md.sup", "md.clearFormat" }) menu.Items.Add(Item(id, track: false));
                var heading = Sub("Menu.Md.Heading");
                foreach (var id in new[] { "md.h0", "md.h1", "md.h2", "md.h3", "md.h4", "md.h5", "md.h6" }) heading.Items.Add(Item(id, track: false));
                menu.Items.Add(heading);
                menu.Items.Add(new Separator());
                if (MarkdownCommands.InList(Editor, _markdown!))
                    foreach (var id in new[] { "md.list.itemAbove", "md.list.itemBelow" }) menu.Items.Add(Item(id, track: false));
                foreach (var id in new[] { "md.task", "md.image", "md.table.insert" }) menu.Items.Add(Item(id, track: false));
            }
        };
        Editor.ContextMenu = menu;

        // A right click outside the selection moves the caret first, so table commands act on the clicked cell.
        Editor.TextArea.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(Editor.TextArea).Properties.IsRightButtonPressed) return;
            // A table drawn as a grid is one element per row: the clicked cell comes from the grid, not the text.
            if (_markdown?.GridSelection.OnContextClick(e) == true) return;
            var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (pos is not { } p) return;
            int offset = Editor.Document.GetOffset(p.Location);
            var sel = Editor.TextArea.Selection;
            if (sel.IsEmpty || !sel.Contains(offset)) Editor.TextArea.Caret.Position = p;
        }, RoutingStrategies.Tunnel);
    }
}
