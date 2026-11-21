using EelDitor.Core.Git;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Core.Text;
using EelDitor.Markdown;

namespace EelDitor.App.Views;

public partial class MainWindow
{
    private readonly List<(MenuItem Item, AppCommand Command)> _checkableItems = [];
    private readonly List<(MenuItem Item, AppCommand Command)> _enablableItems = [];
    private MenuItem? _conflictsMenu;
    private List<Control> _diffTools = [], _diffRevertTools = [], _diffCopyTools = [], _tableTools = [];
    private bool _tableToolsShown = true;

    /// <summary>Table buttons on the toolbar only while the caret is in a Markdown table.</summary>
    private void UpdateTableTools()
    {
        bool show = _diffView is null && InTable;
        if (show == _tableToolsShown) return;
        _tableToolsShown = show;
        foreach (var c in _tableTools) c.IsVisible = show;
        // Shown buttons must also be enabled now (their state may be stale, e.g. after closing a comparison).
        if (show)
            foreach (var (button, cmd) in _toolButtons)
                if (_tableTools.Contains(button)) button.IsEnabled = cmd.CanExecute?.Invoke() ?? true;
    }

    /// <summary>Shows the comparison buttons on the toolbar only while a comparison is open.</summary>
    private void UpdateDiffTools()
    {
        bool open = _diffView is not null;
        foreach (var c in _diffTools) c.IsVisible = open;
        UpdateTableTools();
        if (!open) return;
        foreach (var c in _diffRevertTools) c.IsVisible = _diffView!.LeftIsBase;
        foreach (var c in _diffCopyTools) c.IsVisible = !_diffView!.LeftIsBase;
    }
    private readonly Dictionary<string, string?> _repoRootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The active file belongs to a git repository (cached per folder).</summary>
    private bool InGitRepo
    {
        get
        {
            if (_active?.GitRoot is not null) return true;
            if (_active?.DirectoryPath is not { Length: > 0 } dir || !GitService.KnownAvailable) return false;
            if (!_repoRootCache.TryGetValue(dir, out var root)) _repoRootCache[dir] = root = GitService.FindRoot(dir);
            return root is not null;
        }
    }
    private readonly List<(Button Button, AppCommand Command)> _toolButtons = [];
    private readonly List<(ToggleButton Button, AppCommand Command)> _toggleButtons = [];
    private MenuItem? _recentMenu;

    private bool InMarkdown => _markdown is not null;

    private bool InTable => _markdown is not null && MarkdownCommands.TableAtCaret(Editor, _markdown) is not null;

    private void Add(string id, string labelKey, Action execute, string[]? gestures = null, Func<bool>? canExecute = null, Func<bool>? isChecked = null, string? tooltipKey = null)
    {
        var cmd = new AppCommand
        {
            Id = id,
            LabelKey = labelKey,
            Execute = execute,
            Gestures = gestures ?? [],
            CanExecute = canExecute,
            IsChecked = isChecked,
            TooltipKey = tooltipKey,
        };
        _commands[id] = cmd;
        _keyMap.Register(cmd);
    }

    private void ToggleWordDiff()
    {
        if (_diffView is not null) { _diffView.WordMode = !_diffView.WordMode; return; }
        if (_active is not { FilePath: not null } d) return;
        _settings.DiffWordMode = true;
        _ = InGitRepo ? CompareWithHeadAsync(d) : CompareWithDiskAsync(d);
    }

    /// <summary>Copies the selection; in Markdown with its styles closed (no lone markers in the clipboard).</summary>
    private async Task CopyAsync()
    {
        if (_markdown is not null && MarkdownCommands.BalancedSelectionText(Editor, _markdown) is { } text && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
        else Editor.Copy();
    }

    private async Task CutAsync()
    {
        if (Editor.SelectionLength == 0 || Editor.IsReadOnly) return;
        if (_markdown is null) { Editor.Cut(); return; }
        await CopyAsync();
        if (!MarkdownCommands.DeleteSelectionKeepingMarkers(Editor, _markdown))
            Editor.Document.Remove(Editor.SelectionStart, Editor.SelectionLength);
    }

    /// <summary>The shortcuts of every command (tests).</summary>
    internal Dictionary<string, string[]> CommandGestures() => _commands.ToDictionary(c => c.Key, c => c.Value.Gestures);

    /// <summary>Runs a command by id (tests).</summary>
    internal void RunCommand(string id) => _commands[id].Execute();

    private void Table(TableCommand c)
    {
        if (_markdown is not null) MarkdownCommands.RunTableCommand(Editor, _markdown, c);
    }

    /// <summary>
    /// Shortcuts follow Notepad++ and Visual Studio. Where they disagree, the VS binding is primary and
    /// the Notepad++ one is kept as an alias when it does not collide.
    /// Context-specific bindings (Markdown, tables) are registered first and fall through when not applicable.
    /// </summary>
    private void BuildCommands()
    {
        // Markdown first: they win when applicable.
        Add("md.bold", "Menu.Md.Bold", () => MdStyleToggle("**", MdStyle.Bold), ["Ctrl+B"], () => InMarkdown);
        Add("md.italic", "Menu.Md.Italic", () => MdStyleToggle("*", MdStyle.Italic), ["Ctrl+I"], () => InMarkdown);
        Add("md.strike", "Menu.Md.Strike", () => MdStyleToggle("~~", MdStyle.Strike), ["Ctrl+Shift+X"], () => InMarkdown);
        Add("md.code", "Menu.Md.Code", () => MdStyleToggle("`", MdStyle.Code), ["Ctrl+OemTilde"], () => InMarkdown);
        Add("md.highlight", "Menu.Md.Highlight", () => MdStyleToggle("==", MdStyle.Highlight), null, () => InMarkdown);
        Add("md.task", "Menu.Md.Task", () => { if (_markdown is not null) MarkdownCommands.ToggleTask(Editor, _markdown); }, null, () => InMarkdown, tooltipKey: "Menu.Md.TaskTip");
        for (int level = 1; level <= 6; level++)
        {
            int l = level;
            Add($"md.h{l}", $"Menu.Md.H{l}", () => MarkdownCommands.SetHeading(Editor, l), [$"Ctrl+D{l}"], () => InMarkdown);
        }
        Add("md.h0", "Menu.Md.H0", () => MarkdownCommands.SetHeading(Editor, 0), null, () => InMarkdown);
        Add("md.raw", "Menu.Md.Raw", ToggleMarkdownRaw, ["Ctrl+Shift+M"], isChecked: () => _settings.MarkdownRaw, tooltipKey: "Menu.Md.RawTip");
        Add("md.image", "Menu.Md.Image", () => _ = InsertImageAsync(), ["Ctrl+Shift+I"], () => _active?.IsMarkdown == true && !Editor.IsReadOnly, tooltipKey: "Menu.Md.ImageTip");
        Add("md.table.editCell", "Menu.Md.Table.EditCell", EditCellAtCaret, ["F2"], () => InTable && !Editor.IsReadOnly, tooltipKey: "Table.CellEditTip");
        Add("md.table.insert", "Menu.Md.Table.Insert", () => _ = InsertTableAsync(), null, () => _active?.IsMarkdown == true);
        Add("md.table.rowAbove", "Menu.Md.Table.RowAbove", () => Table(TableCommand.InsertRowAbove), ["Ctrl+Shift+Enter"], () => InTable);
        Add("md.table.rowBelow", "Menu.Md.Table.RowBelow", () => Table(TableCommand.InsertRowBelow), ["Ctrl+Enter"], () => InTable);
        Add("md.table.colLeft", "Menu.Md.Table.ColLeft", () => Table(TableCommand.InsertColumnLeft), ["Ctrl+Alt+Left"], () => InTable);
        Add("md.table.colRight", "Menu.Md.Table.ColRight", () => Table(TableCommand.InsertColumnRight), ["Ctrl+Alt+Right"], () => InTable);
        Add("md.table.delRow", "Menu.Md.Table.DeleteRow", () => Table(TableCommand.DeleteRow), ["Ctrl+Alt+Back"], () => InTable);
        Add("md.table.delCol", "Menu.Md.Table.DeleteColumn", () => Table(TableCommand.DeleteColumn), ["Ctrl+Alt+Delete"], () => InTable);
        Add("md.table.rowUp", "Menu.Md.Table.RowUp", () => Table(TableCommand.MoveRowUp), ["Alt+Up"], () => InTable);
        Add("md.table.rowDown", "Menu.Md.Table.RowDown", () => Table(TableCommand.MoveRowDown), ["Alt+Down"], () => InTable);
        Add("md.table.colMoveLeft", "Menu.Md.Table.ColMoveLeft", () => Table(TableCommand.MoveColumnLeft), ["Alt+Left"], () => InTable);
        Add("md.table.colMoveRight", "Menu.Md.Table.ColMoveRight", () => Table(TableCommand.MoveColumnRight), ["Alt+Right"], () => InTable);
        Add("md.table.selCol", "Menu.Md.Table.SelectColumn", () => SelectTablePart(column: true), ["Ctrl+Alt+Up"], () => InTable);
        Add("md.table.selRow", "Menu.Md.Table.SelectRow", () => SelectTablePart(column: false), ["Ctrl+Alt+Down"], () => InTable);
        Add("md.table.copyCol", "Menu.Md.Table.CopyColumn", () => _ = CopyTableColumnAsync(), null, () => InTable);
        Add("md.table.format", "Menu.Md.Table.Format", () => Table(TableCommand.FormatInSource), null, () => InTable);

        // File
        Add("file.new", "Menu.File.New", NewDocument, ["Ctrl+N"]);
        Add("file.open", "Menu.File.Open", () => _ = OpenFilesDialogAsync(), ["Ctrl+O"]);
        Add("file.reload", "Menu.File.Reload", () => { if (_active is not null) _ = ReloadAsync(_active, null); }, null, () => _active?.FilePath is not null);
        Add("file.save", "Menu.File.Save", () => { if (_active is not null) _ = SaveAsync(_active); }, ["Ctrl+S"]);
        Add("file.saveAs", "Menu.File.SaveAs", () => { if (_active is not null) _ = SaveAsync(_active, saveAs: true); }, ["F12", "Ctrl+Alt+S"]);
        Add("file.saveAll", "Menu.File.SaveAll", () => _ = SaveAllAsync(), ["Ctrl+Shift+S"]);
        Add("file.close", "Menu.File.Close", () => { if (_active is not null) _ = CloseDocumentAsync(_active); }, ["Ctrl+W", "Ctrl+F4"]);
        Add("file.closeAll", "Menu.File.CloseAll", () => _ = CloseManyAsync(_files.Documents), ["Ctrl+Shift+W"]);
        Add("file.closeOthers", "Menu.File.CloseOthers", () => _ = CloseManyAsync(_files.Documents.Where(d => !ReferenceEquals(d, _active))));
        Add("file.copyPath", "Menu.File.CopyPath", () => _ = CopyToClipboardAsync(_active?.FilePath), null, () => _active?.FilePath is not null);
        Add("file.reveal", "Menu.File.Reveal", () => RevealInFileManager(_active?.FilePath), null, () => _active?.FilePath is not null);
        Add("file.exit", "Menu.File.Exit", Close, ["Alt+F4"]);

        // Edit
        Add("edit.undo", "Menu.Edit.Undo", () => Editor.Undo(), ["Ctrl+Z"]);
        Add("edit.redo", "Menu.Edit.Redo", () => Editor.Redo(), ["Ctrl+Y", "Ctrl+Shift+Z"]);
        // Windows also uses Ctrl+Insert, Shift+Delete and Shift+Insert for copy, cut and paste.
        Add("edit.cut", "Menu.Edit.Cut", () => _ = CutAsync(), ["Ctrl+X", "Shift+Delete"]);
        Add("edit.copy", "Menu.Edit.Copy", () => _ = CopyAsync(), ["Ctrl+C", "Ctrl+Insert"]);
        Add("edit.paste", "Menu.Edit.Paste", () => Editor.Paste(), ["Ctrl+V", "Shift+Insert"]);
        Add("edit.selectAll", "Menu.Edit.SelectAll", () => Editor.SelectAll(), ["Ctrl+A"]);
        Add("edit.duplicate", "Menu.Edit.Duplicate", DuplicateLines, ["Ctrl+D"]);
        Add("edit.deleteLine", "Menu.Edit.DeleteLine", DeleteLines, ["Ctrl+Shift+L", "Ctrl+L"]);
        Add("edit.moveUp", "Menu.Edit.MoveLineUp", () => MoveLines(-1), ["Alt+Up", "Ctrl+Shift+Up"]);
        Add("edit.moveDown", "Menu.Edit.MoveLineDown", () => MoveLines(1), ["Alt+Down", "Ctrl+Shift+Down"]);
        Add("edit.comment", "Menu.Edit.ToggleComment", () => ToggleComment(null), ["Ctrl+Q", "Ctrl+OemQuestion"]);
        Add("edit.commentAdd", "Menu.Edit.Comment", () => ToggleComment(true), ["Ctrl+K, Ctrl+C"]);
        Add("edit.commentRemove", "Menu.Edit.Uncomment", () => ToggleComment(false), ["Ctrl+K, Ctrl+U"]);
        Add("edit.upper", "Menu.Edit.Upper", () => ChangeCase(upper: true), ["Ctrl+Shift+U"]);
        Add("edit.lower", "Menu.Edit.Lower", () => ChangeCase(upper: false), ["Ctrl+U"]);
        Add("edit.trimTrailing", "Menu.Edit.TrimTrailing", TrimTrailingWhitespace);
        Add("edit.indentToSpaces", "Indent.ConvertToSpaces", () => ConvertIndentation(true));
        Add("edit.indentToTabs", "Indent.ConvertToTabs", () => ConvertIndentation(false));
        Add("edit.toCyrillicA", "Cyrillic.ToA", () => ConvertAlphabet(t => Core.Text.PolishCyrillic.ToCyrillic(t, Core.Text.CyrillicVariant.A)), null, () => _active is not null && !Editor.IsReadOnly, tooltipKey: "Cyrillic.Tip");
        Add("edit.toCyrillicB", "Cyrillic.ToB", () => ConvertAlphabet(t => Core.Text.PolishCyrillic.ToCyrillic(t, Core.Text.CyrillicVariant.B)), null, () => _active is not null && !Editor.IsReadOnly, tooltipKey: "Cyrillic.Tip");
        Add("edit.toLatin", "Cyrillic.Polish", () => ConvertAlphabet(Core.Text.PolishCyrillic.ToLatin), null, () => _active is not null && !Editor.IsReadOnly, tooltipKey: "Cyrillic.Tip");
        foreach (var t in Core.Text.Transliteration.All)
        {
            var tr = t;
            Add($"edit.toCyr.{tr.Id}", $"Cyrillic.Lang.{tr.Id}", () => ConvertAlphabet(tr.ToCyrillic), null, () => _active is not null && !Editor.IsReadOnly, tooltipKey: $"Cyrillic.Tip.{tr.Id}");
            Add($"edit.toLat.{tr.Id}", $"Cyrillic.Lang.{tr.Id}", () => ConvertAlphabet(tr.ToLatin), null, () => _active is not null && !Editor.IsReadOnly, tooltipKey: $"Cyrillic.Tip.{tr.Id}");
        }
        Add("view.cyrNone", "Cyrillic.ViewNone", () => SetCyrillicView(null), null, () => _active is not null, isChecked: () => _active?.CyrillicView is null);
        Add("view.cyrA", "Cyrillic.ViewA", () => SetCyrillicView(Core.Text.CyrillicVariant.A), null, () => _active is not null, isChecked: () => _active?.CyrillicView == Core.Text.CyrillicVariant.A);
        Add("view.cyrB", "Cyrillic.ViewB", () => SetCyrillicView(Core.Text.CyrillicVariant.B), null, () => _active is not null, isChecked: () => _active?.CyrillicView == Core.Text.CyrillicVariant.B);

        // Search
        Add("search.find", "Menu.Search.Find", () => ShowFindBar(replace: false), ["Ctrl+F"]);
        Add("search.replace", "Menu.Search.Replace", () => ShowFindBar(replace: true), ["Ctrl+H"]);
        Add("search.findInFiles", "Menu.Search.FindInFiles", () => ShowFilesDialog(), ["Ctrl+Shift+F"]);
        Add("search.replaceInFiles", "Menu.Search.ReplaceInFiles", () => ShowFilesDialog(), ["Ctrl+Shift+H"]);
        Add("edit.columnHelp", "Menu.Edit.ColumnSelection", () => _ = Dialogs.Info(this, Loc.T("Menu.Edit.ColumnSelection").TrimEnd('…'), Loc.T("Help.ColumnSelection")));
        Add("edit.eolCrLf", "Menu.Edit.EolCrLf", () => ConvertEol(EolKind.CrLf), null, () => _active is not null, isChecked: () => _active is { EolMixed: false, Eol: EolKind.CrLf });
        Add("edit.eolLf", "Menu.Edit.EolLf", () => ConvertEol(EolKind.Lf), null, () => _active is not null, isChecked: () => _active is { EolMixed: false, Eol: EolKind.Lf });
        Add("edit.eolCr", "Menu.Edit.EolCr", () => ConvertEol(EolKind.Cr), null, () => _active is not null, isChecked: () => _active is { EolMixed: false, Eol: EolKind.Cr });
        Add("search.next", "Menu.Search.FindNext", () => FindNext(forward: true), ["F3"]);
        Add("search.prev", "Menu.Search.FindPrevious", () => FindNext(forward: false), ["Shift+F3"]);
        Add("search.nextSelected", "Menu.Search.FindNextSelected", () => FindSelected(forward: true), ["Ctrl+F3"]);
        Add("search.prevSelected", "Menu.Search.FindPrevSelected", () => FindSelected(forward: false), ["Ctrl+Shift+F3"]);
        Add("search.nextResult", "Menu.Search.NextResult", () => GoToResult(1), ["F4"]);
        Add("search.prevResult", "Menu.Search.PrevResult", () => GoToResult(-1), ["Shift+F4"]);
        Add("search.gotoLine", "Menu.Search.GotoLine", () => _ = GoToLineAsync(), ["Ctrl+G"]);
        Add("search.brace", "Menu.Search.MatchingBrace", GoToMatchingBrace, ["Ctrl+OemCloseBrackets", "Ctrl+B"]);
        Add("search.bookmark", "Menu.Search.ToggleBookmark", ToggleBookmark, ["Ctrl+F2"]);
        Add("search.nextBookmark", "Menu.Search.NextBookmark", () => GoToBookmark(1), ["F2"]);
        Add("search.prevBookmark", "Menu.Search.PrevBookmark", () => GoToBookmark(-1), ["Shift+F2"]);
        Add("search.clearBookmarks", "Menu.Search.ClearBookmarks", ClearBookmarks);

        // View
        Add("view.wrap", "Menu.View.WordWrap", ToggleWordWrap, ["Alt+Z"], isChecked: () => Editor.WordWrap);
        Add("view.whitespace", "Menu.View.ShowWhitespace", () => SetWhitespace(!Editor.Options.ShowSpaces, Editor.Options.ShowEndOfLine), null, () => true, () => Editor.Options.ShowSpaces);
        Add("view.eol", "Menu.View.ShowEol", () => SetWhitespace(Editor.Options.ShowSpaces, !Editor.Options.ShowEndOfLine), null, () => true, () => Editor.Options.ShowEndOfLine);
        Add("view.allChars", "Menu.View.ShowAll", ToggleAllCharacters, ["Ctrl+Shift+8"], isChecked: () => Editor.Options.ShowSpaces && Editor.Options.ShowEndOfLine);
        Add("view.lineNumbers", "Menu.View.LineNumbers", ToggleLineNumbers, null, isChecked: () => Editor.ShowLineNumbers);
        Add("view.currentLine", "Menu.View.CurrentLine", ToggleCurrentLine, null, isChecked: () => Editor.Options.HighlightCurrentLine);
        Add("view.sidebar", "Menu.View.Sidebar", ToggleSidebar, ["Ctrl+Shift+B", "Ctrl+Alt+L"], isChecked: () => MainGrid.ColumnDefinitions[0].Width.Value > 0);
        Add("view.results", "Menu.View.Results", ToggleResults, ["Ctrl+Shift+R", "Ctrl+Alt+R"], isChecked: () => ResultsHost.IsVisible);
        Add("view.zoomIn", "Menu.View.ZoomIn", () => Zoom(1), ["Ctrl+OemPlus", "Ctrl+Add"]);
        Add("view.zoomOut", "Menu.View.ZoomOut", () => Zoom(-1), ["Ctrl+OemMinus", "Ctrl+Subtract"]);
        Add("view.zoomReset", "Menu.View.ZoomReset", () => Zoom(0), ["Ctrl+D0", "Ctrl+NumPad0"]);
        Add("view.filter", "Menu.View.FilterFiles", () => { FilterBox.Focus(); FilterBox.SelectAll(); }, ["Ctrl+Shift+P", "Ctrl+Alt+F"]);
        Add("view.goToFile", "Menu.View.GoToFile", () => ShowQuickOpen(), ["Ctrl+P", "Ctrl+Shift+O"]);
        Add("view.nextDoc", "Menu.View.NextDocument", () => CycleVisual(1), ["Ctrl+PageDown"]);
        Add("view.prevDoc", "Menu.View.PrevDocument", () => CycleVisual(-1), ["Ctrl+PageUp"]);
        Add("view.themeDark", "Menu.View.ThemeDark", () => SetTheme(AppTheme.Dark), null, () => true, () => _settings.Theme == AppTheme.Dark);
        Add("view.themeSystem", "Menu.View.ThemeSystem", () => SetTheme(AppTheme.System), null, () => true, () => _settings.Theme == AppTheme.System);
        Add("view.themeLight", "Menu.View.ThemeLight", () => SetTheme(AppTheme.Light), null, () => true, () => _settings.Theme == AppTheme.Light);

        // Compare / git
        Add("diff.head", "Menu.Compare.WithHead", () => { if (_active is not null) _ = CompareWithHeadAsync(_active); }, ["Ctrl+Shift+D", "Ctrl+Alt+D"], () => InGitRepo);
        Add("diff.disk", "Menu.Compare.WithDisk", () => { if (_active is not null) _ = CompareWithDiskAsync(_active); }, null, () => _active?.FilePath is not null);
        Add("diff.file", "Menu.Compare.WithFile", () => _ = CompareWithFileAsync(), null, () => _active is not null);
        Add("diff.close", "Menu.Compare.Close", CloseDiff, ["Shift+Escape"], () => _diffView is not null);
        Add("diff.sync", "Diff.SyncScroll", () => { if (_diffView is not null) _diffView.SyncScroll = !_diffView.SyncScroll; }, null,
            () => _diffView is not null, isChecked: () => _diffView?.SyncScroll ?? _settings.DiffSyncScroll, tooltipKey: "Diff.SyncScrollTip");
        // Word diff: switches an open comparison, or opens one (with HEAD in a repository, else with the file on disk).
        Add("diff.words", "Diff.WordMode", ToggleWordDiff, ["Ctrl+F7", "Ctrl+Alt+W"],
            () => _diffView is not null || _active?.FilePath is not null, isChecked: () => _diffView?.WordMode == true, tooltipKey: "Diff.WordModeTip");
        Add("wrap.oneLine", "Wrap.OneLineItem", () => SetParagraphWrap(Core.Text.WrapMode.OneLine), null, null, () => _settings.ParagraphWrap == Core.Text.WrapMode.OneLine);
        Add("wrap.80", "Wrap.Width80Item", () => SetParagraphWrap(Core.Text.WrapMode.Width80), null, null, () => _settings.ParagraphWrap == Core.Text.WrapMode.Width80);
        Add("wrap.120", "Wrap.Width120Item", () => SetParagraphWrap(Core.Text.WrapMode.Width120), null, null, () => _settings.ParagraphWrap == Core.Text.WrapMode.Width120);
        Add("wrap.sentences", "Wrap.SentencesItem", () => SetParagraphWrap(Core.Text.WrapMode.Sentences), null, null, () => _settings.ParagraphWrap == Core.Text.WrapMode.Sentences, tooltipKey: "Wrap.SentencesTip");
        Add("wrap.reflow", "Wrap.Reflow", ReflowParagraphs, ["Alt+Q"], () => _active is not null && !Editor.IsReadOnly, tooltipKey: "Wrap.ReflowTip");
        Add("diff.revert", "Diff.RevertChange", () => _diffView?.CopyBlock(toRight: true), null, () => _diffView is { RightEditable: true, WordMode: false }, tooltipKey: "Diff.RevertChangeTip");
        // Whole-file operations work in the word view too; single blocks only in the side-by-side one.
        Add("diff.revertAll", "Diff.RevertAll", () => _diffView?.CopyAll(toRight: true), null, () => _diffView is { RightEditable: true }, tooltipKey: "Diff.RevertAllTip");
        Add("diff.next", "Menu.Compare.NextChange", () => _diffView?.GoToChange(1), ["F7", "Alt+Down"], () => _diffView is not null);
        Add("diff.prev", "Menu.Compare.PrevChange", () => _diffView?.GoToChange(-1), ["Shift+F7", "Alt+Up"], () => _diffView is not null);
        Add("diff.copyRight", "Menu.Compare.CopyRight", () => _diffView?.CopyBlock(toRight: true), ["Alt+Right"], () => _diffView is { RightEditable: true, WordMode: false });
        Add("diff.copyLeft", "Menu.Compare.CopyLeft", () => _diffView?.CopyBlock(toRight: false), ["Alt+Left"], () => _diffView is { LeftEditable: true, WordMode: false });
        Add("diff.copyAllRight", "Menu.Compare.CopyAllRight", () => _diffView?.CopyAll(toRight: true), null, () => _diffView is { RightEditable: true });
        Add("diff.copyAllLeft", "Menu.Compare.CopyAllLeft", () => _diffView?.CopyAll(toRight: false), null, () => _diffView is { LeftEditable: true });
        Add("conflict.next", "Conflict.Next", () => GoToConflict(1), ["F8"], () => _conflicts.Count > 0 && InGitRepo);
        Add("conflict.prev", "Conflict.Prev", () => GoToConflict(-1), ["Shift+F8"], () => _conflicts.Count > 0 && InGitRepo);
        Add("conflict.current", "Conflict.AcceptCurrent", () => ResolveConflict(ConflictChoice.Current), null, () => _conflicts.Count > 0 && InGitRepo, tooltipKey: "Conflict.AcceptCurrentTip");
        Add("conflict.incoming", "Conflict.AcceptIncoming", () => ResolveConflict(ConflictChoice.Incoming), null, () => _conflicts.Count > 0 && InGitRepo, tooltipKey: "Conflict.AcceptIncomingTip");
        Add("conflict.both", "Conflict.AcceptBoth", () => ResolveConflict(ConflictChoice.Both), null, () => _conflicts.Count > 0 && InGitRepo);
        Add("git.log", "Menu.Compare.History", () => { if (_active is not null) _ = ShowHistoryAsync(_active); }, ["Ctrl+Shift+Y", "Ctrl+Alt+H"], () => InGitRepo);

        // Settings / help
        Add("settings.open", "Menu.Settings.Open", () => _ = ShowSettingsAsync());
        Add("settings.autoReload", "Menu.Settings.AutoReload", () => _settings.AutoReloadUnmodified = !_settings.AutoReloadUnmodified, null, () => true, () => _settings.AutoReloadUnmodified);
        Add("help.shortcuts", "Menu.Help.Shortcuts", ShowShortcuts, ["F1"]);
        Add("file.console", "Menu.File.Console", () => OpenConsole(_active?.DirectoryPath), null, () => _active?.FilePath is not null);
        Add("view.filesPanel", "Menu.View.FilesPanel", () => ShowSideTab(FilesTab), ["Ctrl+Shift+E", "Ctrl+Alt+E"]);
        Add("view.gitPanel", "Menu.View.GitPanel", () => ShowSideTab(GitTab), ["Ctrl+Shift+G", "Ctrl+Alt+G"]);
        Add("view.map", "Menu.View.DocumentMap", () => ShowSideTab(MapTab), ["Ctrl+Shift+J", "Ctrl+Alt+M"]);
        Add("help.about", "Menu.Help.About", () => _ = Dialogs.Info(this, AppInfo.Name, Loc.T("About.Text", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "")));

        _keyMap.ChordStateChanged += chord => StatusMessage.Text = chord is null ? "" : Loc.T("Status.Chord", chord);
    }

    /// <summary>A menu item for a command. Tracked items follow the command's enabled/checked state.</summary>
    private MenuItem Item(string id, bool track = true)
    {
        var cmd = _commands[id];
        var item = new MenuItem { Header = cmd.Label };
        if (cmd.TooltipKey is { } tip) ToolTip.SetTip(item, Loc.T(tip));
        if (cmd.CanExecute is not null)
        {
            item.IsEnabled = cmd.CanExecute();
            if (track) _enablableItems.Add((item, cmd));
        }
        if (cmd.GestureText is { } g)
        {
            try { item.InputGesture = KeyGesture.Parse(g.Split(',')[0].Trim()); }
            catch (ArgumentException) { }
            if (g.Contains(',')) item.InputGesture = null;
            if (g.Contains(',')) item.Header = $"{cmd.Label}\t{g}";
        }
        item.Click += (_, _) =>
        {
            if (cmd.CanExecute?.Invoke() ?? true) cmd.Execute();
            RefreshCommandStates();
        };
        if (cmd.IsChecked is not null)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = cmd.IsChecked();
            if (track) _checkableItems.Add((item, cmd));
        }
        return item;
    }

    private static MenuItem Sub(string labelKey, params object[] items)
    {
        var m = new MenuItem { Header = Loc.T(labelKey) };
        foreach (var i in items) m.Items.Add(i);
        return m;
    }

    private static Separator Sep() => new();

    private void BuildMenu()
    {
        _recentMenu = new MenuItem { Header = Loc.T("Menu.File.Recent") };
        RebuildRecentMenu();

        var encodingMenu = new MenuItem { Header = Loc.T("Menu.Encoding") };
        // SubmenuOpened bubbles up from nested submenus; rebuilding then would close the submenu being opened.
        encodingMenu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.Source, encodingMenu)) FillEncodingMenu(encodingMenu); };
        encodingMenu.Items.Add(new MenuItem { Header = "…" });

        var indentMenu = new MenuItem { Header = Loc.T("Menu.Settings.Indent") };
        indentMenu.SubmenuOpened += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, indentMenu)) return;
            indentMenu.Items.Clear();
            foreach (var i in BuildIndentItems()) indentMenu.Items.Add(i);
        };
        indentMenu.Items.Add(new MenuItem { Header = "…" });

        MainMenu.Items.Add(Sub("Menu.File",
            Item("file.new"), Item("file.open"), _recentMenu, Item("file.reload"), Sep(),
            Item("file.save"), Item("file.saveAs"), Item("file.saveAll"), Sep(),
            Item("file.close"), Item("file.closeOthers"), Item("file.closeAll"), Sep(),
            Item("file.copyPath"), Item("file.reveal"), Item("file.console"), Sep(),
            Item("file.exit")));

        MainMenu.Items.Add(Sub("Menu.Edit",
            Item("edit.undo"), Item("edit.redo"), Sep(),
            Item("edit.cut"), Item("edit.copy"), Item("edit.paste"), Item("edit.selectAll"), Sep(),
            Sub("Menu.Edit.Lines", Item("edit.duplicate"), Item("edit.deleteLine"), Item("edit.moveUp"), Item("edit.moveDown"), Item("edit.trimTrailing")),
            Sub("Menu.Edit.Comments", Item("edit.comment"), Item("edit.commentAdd"), Item("edit.commentRemove")),
            Sub("Menu.Edit.Case", Item("edit.upper"), Item("edit.lower")),
            Sub("Menu.Edit.Indentation", Item("edit.indentToSpaces"), Item("edit.indentToTabs")),
            Sub("Menu.Edit.Eol", Item("edit.eolCrLf"), Item("edit.eolLf"), Item("edit.eolCr")),
            Sub("Wrap.Menu", Item("wrap.oneLine"), Item("wrap.80"), Item("wrap.120"), Item("wrap.sentences"), Sep(), Item("wrap.reflow")),
            Sub("Cyrillic.Menu",
                Sub("Cyrillic.ToCyrillicMenu", Item("edit.toCyr.ru"), Item("edit.toCyr.uk"), Item("edit.toCyr.sr"), Item("edit.toCyr.isv"), Sep(),
                    Item("edit.toCyrillicA"), Item("edit.toCyrillicB")),
                Sub("Cyrillic.ToLatinMenu", Item("edit.toLat.ru"), Item("edit.toLat.uk"), Item("edit.toLat.sr"), Item("edit.toLat.isv"), Sep(),
                    Item("edit.toLatin"))),
            Sep(),
            Item("edit.columnHelp")));

        MainMenu.Items.Add(Sub("Menu.Search",
            Item("search.find"), Item("search.replace"), Sep(),
            Item("search.findInFiles"), Item("search.replaceInFiles"), Sep(),
            Item("search.next"), Item("search.prev"), Item("search.nextSelected"), Item("search.prevSelected"), Sep(),
            Item("search.nextResult"), Item("search.prevResult"), Sep(),
            Item("search.gotoLine"), Item("search.brace"), Sep(),
            Sub("Menu.Search.Bookmarks", Item("search.bookmark"), Item("search.nextBookmark"), Item("search.prevBookmark"), Item("search.clearBookmarks"))));

        MainMenu.Items.Add(Sub("Menu.View",
            Item("view.wrap"), Sep(),
            Item("view.whitespace"), Item("view.eol"), Item("view.allChars"), Sep(),
            Item("view.lineNumbers"), Item("view.currentLine"), Sep(),
            Item("view.sidebar"), Item("view.filesPanel"), Item("view.gitPanel"), Item("view.map"), Item("view.results"), Item("view.filter"), Item("view.goToFile"), Item("view.nextDoc"), Item("view.prevDoc"), Sep(),
            Item("view.zoomIn"), Item("view.zoomOut"), Item("view.zoomReset"), Sep(),
            Sub("Menu.View.Theme", Item("view.themeDark"), Item("view.themeSystem"), Item("view.themeLight"))));

        MainMenu.Items.Add(encodingMenu);

        MainMenu.Items.Add(Sub("Menu.Markdown",
            Item("md.bold"), Item("md.italic"), Item("md.strike"), Item("md.code"), Item("md.highlight"), Sep(),
            Item("md.task"), Item("md.image"), Sep(),
            Sub("Menu.Md.Heading", Item("md.h1"), Item("md.h2"), Item("md.h3"), Item("md.h4"), Item("md.h5"), Item("md.h6"), Item("md.h0")),
            Sub("Menu.Md.Table",
                Item("md.table.insert"), Item("md.table.editCell"), Sep(),
                Item("md.table.rowAbove"), Item("md.table.rowBelow"), Item("md.table.colLeft"), Item("md.table.colRight"), Sep(),
                Item("md.table.delRow"), Item("md.table.delCol"), Sep(),
                Item("md.table.rowUp"), Item("md.table.rowDown"), Item("md.table.colMoveLeft"), Item("md.table.colMoveRight"), Sep(),
                Item("md.table.selCol"), Item("md.table.selRow"), Item("md.table.copyCol"), Sep(),
                Item("md.table.format")),
            Sep(),
            Item("md.raw")));

        MainMenu.Items.Add(Sub("Menu.Compare",
            Item("diff.head"), Item("diff.disk"), Item("diff.file"), Sep(),
            Item("diff.next"), Item("diff.prev"), Item("diff.words"), Sep(),
            Item("diff.copyRight"), Item("diff.copyLeft"), Item("diff.copyAllRight"), Item("diff.copyAllLeft"), Sep(),
            Item("diff.close"), Sep(),
            _conflictsMenu = Sub("Menu.Compare.Conflicts", Item("conflict.next"), Item("conflict.prev"), Sep(),
                Item("conflict.current"), Item("conflict.incoming"), Item("conflict.both")), Sep(),
            Item("git.log")));

        MainMenu.Items.Add(Sub("Menu.Settings", Item("settings.open"), indentMenu, Item("settings.autoReload")));
        MainMenu.Items.Add(Sub("Menu.Help", Item("help.shortcuts"), Item("help.about")));

        foreach (var top in MainMenu.Items.OfType<MenuItem>())
            top.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.Source, top)) RefreshCommandStates(); };
    }

    // Formats shown directly in "Display as"; the rest are under "All formats".
    private static readonly string[] CommonSyntaxes =
        ["csharp", "cpp", "c", "java", "javascript", "typescript", "python", "go", "rust", "html", "xml", "css", "json", "yaml", "sql", "powershell", "shellscript", "bat", "ini"];

    /// <summary>"Display as ›" submenu for a document: automatic, plain text, Markdown, common languages, all languages.</summary>
    private MenuItem BuildDisplayAsMenu(EditorDocument doc)
    {
        var menu = new MenuItem { Header = Loc.T("Panel.DisplayAs") };
        MenuItem Choice(string header, string? syntax)
        {
            bool current = doc.SyntaxOverride == syntax;
            var mi = new MenuItem { Header = header, Icon = current ? new TextBlock { Text = "✓" } : null };
            mi.Click += (_, _) => SetSyntax(doc, syntax);
            return mi;
        }
        menu.Items.Add(Choice(Loc.T("Menu.Language.Auto"), null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Choice(Loc.T("Syntax.PlainText"), EditorDocument.PlainTextSyntax));
        menu.Items.Add(Choice("Markdown", EditorDocument.MarkdownSyntax));
        menu.Items.Add(new Separator());
        var languages = _textMateRegistry.GetAvailableLanguages()
            .Where(l => l.Aliases is { Count: > 0 } && l.Id != "markdown")
            .OrderBy(l => l.Aliases![0], StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var id in CommonSyntaxes)
            if (languages.FirstOrDefault(l => l.Id == id) is { } l) menu.Items.Add(Choice(l.Aliases![0], l.Id));
        var all = new MenuItem { Header = Loc.T("Panel.AllFormats") };
        foreach (var group in languages.GroupBy(l => char.ToUpperInvariant(l.Aliases![0][0])))
        {
            var sub = new MenuItem { Header = group.Key.ToString() };
            foreach (var l in group) sub.Items.Add(Choice(l.Aliases![0], l.Id));
            all.Items.Add(sub);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(all);
        return menu;
    }

    private void FillEncodingMenu(MenuItem menu)
    {
        menu.Items.Clear();
        foreach (var i in BuildEncodingItems(reopen: false)) menu.Items.Add(i);
        menu.Items.Add(new Separator());
        var view = new MenuItem { Header = Loc.T("Cyrillic.ViewMenu") };
        foreach (var id in new[] { "view.cyrNone", "view.cyrA", "view.cyrB" }) view.Items.Add(Item(id, track: false));
        menu.Items.Add(view);
        menu.Items.Add(new Separator());
        var eol = new MenuItem { Header = Loc.T("Menu.Encoding.Eol") };
        foreach (var kind in new[] { Core.Text.EolKind.CrLf, Core.Text.EolKind.Lf, Core.Text.EolKind.Cr })
        {
            var k = kind;
            var item = new MenuItem { Header = kind.DisplayName(), Icon = _active?.Eol == kind && _active?.EolMixed == false ? new TextBlock { Text = "✓" } : null };
            item.Click += (_, _) => ConvertEol(k);
            eol.Items.Add(item);
        }
        menu.Items.Add(eol);
    }

    private void RebuildRecentMenu()
    {
        if (_recentMenu is null) return;
        _recentMenu.Items.Clear();
        var recent = _settings.RecentFiles.Take(20).ToList();
        var duplicateNames = recent.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in recent)
        {
            var p = path;
            string name = Path.GetFileName(path);
            // Same name in two folders: add the parent folder so the entries can be told apart.
            if (duplicateNames.Contains(name) && Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } parent)
                name += $"  ({parent})";
            var item = new MenuItem { Header = name };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => _ = OpenFileAsync(p);
            _recentMenu.Items.Add(item);
        }
        _recentMenu.IsEnabled = _recentMenu.Items.Count > 0;
    }

    private void RefreshCommandStates()
    {
        foreach (var (item, cmd) in _checkableItems) item.IsChecked = cmd.IsChecked!();
        foreach (var (button, cmd) in _toggleButtons) button.IsChecked = cmd.IsChecked!();
        foreach (var (item, cmd) in _enablableItems) item.IsEnabled = cmd.CanExecute!();
        if (_conflictsMenu is not null) _conflictsMenu.IsEnabled = InGitRepo;
        foreach (var (button, cmd) in _toolButtons) button.IsEnabled = cmd.CanExecute?.Invoke() ?? true;
    }

    private static string Tip(AppCommand cmd)
    {
        string tip = cmd.GestureText is null ? cmd.Label : $"{cmd.Label} ({ShortcutsPopup.Pretty(cmd.GestureText)})";
        return cmd.TooltipKey is { } key ? tip + Environment.NewLine + Loc.T(key) : tip;
    }

    private void BuildToolbar()
    {
        // Flat icons with a muted color per group (files blue, folders amber, editing gray, history violet…).
        PathIcon Icon(string icon, string brush) =>
            new PathIcon { Data = (Geometry)this.FindResource(icon)! }.Res(PathIcon.ForegroundProperty, brush);
        void Btn(string id, string icon, string brush = "IconGrayBrush")
        {
            var cmd = _commands[id];
            var b = new Button { Classes = { "tool" }, Content = Icon(icon, brush) };
            ToolTip.SetTip(b, Tip(cmd));
            b.Click += (_, _) => { if (cmd.CanExecute?.Invoke() ?? true) cmd.Execute(); Editor.Focus(); RefreshCommandStates(); };
            if (cmd.CanExecute is not null) _toolButtons.Add((b, cmd));
            Toolbar.Children.Add(b);
        }
        void Toggle(string id, string icon, string brush = "IconGrayBrush")
        {
            var cmd = _commands[id];
            var b = new ToggleButton { Classes = { "tool" }, Content = Icon(icon, brush) };
            ToolTip.SetTip(b, Tip(cmd));
            b.IsChecked = cmd.IsChecked!();
            b.Click += (_, _) => { cmd.Execute(); Editor.Focus(); RefreshCommandStates(); };
            _toggleButtons.Add((b, cmd));
            Toolbar.Children.Add(b);
        }
        void S() => Toolbar.Children.Add(new Border { Classes = { "separator" } });

        Btn("file.new", "IconNew", "IconBlueBrush");
        Btn("file.open", "IconOpen", "IconAmberBrush");
        Btn("file.save", "IconSave", "IconBlueBrush");
        Btn("file.saveAll", "IconSaveAll", "IconBlueBrush");
        Btn("file.close", "IconClose", "IconRedBrush");
        S();
        Btn("edit.cut", "IconCut");
        Btn("edit.copy", "IconCopy");
        Btn("edit.paste", "IconPaste", "IconAmberBrush");
        S();
        Btn("edit.undo", "IconUndo", "IconVioletBrush");
        Btn("edit.redo", "IconRedo", "IconVioletBrush");
        S();
        Btn("search.find", "IconFind", "IconTealBrush");
        Btn("search.replace", "IconReplace", "IconTealBrush");
        S();
        Toggle("view.wrap", "IconWrap");
        Toggle("view.allChars", "IconWhitespace");
        Toggle("md.raw", "IconMarkdown", "IconBlueBrush");
        S();
        Btn("diff.head", "IconDiff", "IconGreenBrush");
        Toggle("diff.words", "IconWordDiff", "IconTealBrush");
        Btn("git.log", "IconHistory", "IconVioletBrush");

        // Caret in a Markdown table: table operations.
        int tableStart = Toolbar.Children.Count;
        S();
        Btn("md.table.rowAbove", "IconTableRowAbove", "IconBlueBrush");
        Btn("md.table.rowBelow", "IconTableRowBelow", "IconBlueBrush");
        Btn("md.table.colLeft", "IconTableColLeft", "IconBlueBrush");
        Btn("md.table.colRight", "IconTableColRight", "IconBlueBrush");
        Btn("md.table.delRow", "IconTableDelRow", "IconRedBrush");
        Btn("md.table.delCol", "IconTableDelCol", "IconRedBrush");
        Btn("md.table.format", "IconTableFormat", "IconTealBrush");
        _tableTools = Toolbar.Children.Skip(tableStart).OfType<Control>().ToList();
        UpdateTableTools();

        // While comparing: navigation, revert/copy and sync. Reverting applies against a base version, copying between two files.
        int diffStart = Toolbar.Children.Count;
        S();
        Btn("diff.prev", "IconPrevChange", "IconTealBrush");
        Btn("diff.next", "IconNextChange", "IconTealBrush");
        int revertStart = Toolbar.Children.Count;
        Btn("diff.revert", "IconRevert", "IconVioletBrush");
        Btn("diff.revertAll", "IconRevertAll", "IconVioletBrush");
        int copyStart = Toolbar.Children.Count;
        Btn("diff.copyLeft", "IconCopyLeft", "IconBlueBrush");
        Btn("diff.copyRight", "IconCopyRight", "IconBlueBrush");
        Btn("diff.copyAllLeft", "IconCopyAllLeft", "IconBlueBrush");
        Btn("diff.copyAllRight", "IconCopyAllRight", "IconBlueBrush");
        int copyEnd = Toolbar.Children.Count;
        Toggle("diff.sync", "IconSync", "IconGreenBrush");
        Btn("diff.close", "IconClose", "IconRedBrush");
        _diffTools = Toolbar.Children.Skip(diffStart).OfType<Control>().ToList();
        _diffRevertTools = Toolbar.Children.Skip(revertStart).Take(copyStart - revertStart).OfType<Control>().ToList();
        _diffCopyTools = Toolbar.Children.Skip(copyStart).Take(copyEnd - copyStart).OfType<Control>().ToList();
        UpdateDiffTools();
    }

    private ContextMenu BuildFileContextMenu(TreeViewItem item)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (item.DataContext is not EditorDocument doc) return;
            MenuItem M(string key, Action a, bool enabled = true)
            {
                var mi = new MenuItem { Header = Loc.T(key), IsEnabled = enabled };
                mi.Click += (_, _) => a();
                return mi;
            }
            bool hasPath = doc.FilePath is not null;
            menu.Items.Add(M("Menu.File.Save", () => _ = SaveAsync(doc)));
            menu.Items.Add(M("Menu.File.Close", () => _ = CloseDocumentAsync(doc)));
            menu.Items.Add(M("Menu.File.CloseOthers", () => _ = CloseManyAsync(_files.Documents.Where(d => !ReferenceEquals(d, doc)))));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildDisplayAsMenu(doc));
            menu.Items.Add(new Separator());
            menu.Items.Add(M(doc.Pinned ? "Panel.Unpin" : "Panel.Pin", () => { doc.Pinned = !doc.Pinned; _files.Rebuild(); }));
            menu.Items.Add(M("Menu.File.CopyPath", () => _ = CopyToClipboardAsync(doc.FilePath), hasPath));
            menu.Items.Add(M("Menu.File.Reveal", () => RevealInFileManager(doc.FilePath), hasPath));
            menu.Items.Add(M("Menu.File.Console", () => OpenConsole(doc.DirectoryPath), hasPath));
            menu.Items.Add(new Separator());
            menu.Items.Add(M("Menu.Compare.WithHead", () => _ = CompareWithHeadAsync(doc), hasPath));
            menu.Items.Add(M("Menu.Compare.WithDisk", () => _ = CompareWithDiskAsync(doc), hasPath));
            if (_active is not null && !ReferenceEquals(_active, doc))
                menu.Items.Add(M("Panel.CompareWithActive", () => _ = CompareDocumentsAsync(doc, _active)));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildGitMenu(doc));
        };
        return menu;
    }

    private ContextMenu BuildGroupContextMenu(TreeViewItem item)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (item.DataContext is not FileGroup group) return;
            MenuItem M(string key, Action a, bool enabled = true)
            {
                var mi = new MenuItem { Header = Loc.T(key), IsEnabled = enabled };
                mi.Click += (_, _) => a();
                return mi;
            }
            var inGroup = group.AllFiles.ToHashSet();
            menu.Items.Add(M("Panel.CloseGroup", () => _ = CloseManyAsync(inGroup)));
            menu.Items.Add(M("Panel.CloseOtherGroups", () => _ = CloseManyAsync(_files.Documents.Where(d => !inGroup.Contains(d)))));
            menu.Items.Add(new Separator());
            menu.Items.Add(M("Panel.OpenFolder", () => RevealInFileManager(group.Directory, isDirectory: true), group.Directory.Length > 0));
            menu.Items.Add(M("Panel.CopyFolderPath", () => _ = CopyToClipboardAsync(group.Directory), group.Directory.Length > 0));
            menu.Items.Add(M("Menu.File.Console", () => OpenConsole(group.Directory), group.Directory.Length > 0));
            menu.Items.Add(M("Panel.FindInFolder", () => ShowFilesDialog(FilesScope.Folder, directory: group.Directory), group.Directory.Length > 0));
            menu.Items.Add(new Separator());
            menu.Items.Add(M(_files.SortByName ? "Panel.SortByOpenOrder" : "Panel.SortByName", () =>
            {
                _files.SortByName = !_files.SortByName;
                _settings.SortGroupsByName = _files.SortByName;
                _files.Rebuild();
            }));
        };
        return menu;
    }
}
