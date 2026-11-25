using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Core.Files;
using EelDitor.Core.Text;
using EelDitor.App.Syntax;
using TextMateSharp.Grammars;

namespace EelDitor.App.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly OpenFilesModel _files = new();
    private readonly KeyMap _keyMap = new();
    private readonly Dictionary<string, AppCommand> _commands = new();
    private readonly FileMonitor _monitor = new();
    private readonly EelRegistryOptions _textMateRegistry;
    private readonly TextMate.Installation _textMate;
    private readonly TextDocument _emptyDocument = new() { UndoStack = { SizeLimit = 0 } };
    private MarkdownView? _markdown;

    internal MarkdownView? MarkdownViewForTests => _markdown;
    private EditorDocument? _active;
    private bool _closingConfirmed;

    public MainWindow() : this(new AppSettings(), []) { }

    public MainWindow(AppSettings settings, string[] args)
    {
        _settings = settings;
        InitializeComponent();
        StartupTrace.Mark("  xaml loaded");
        DataContext = _files;
        _files.SortByName = settings.SortGroupsByName;

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowX is { } wx && settings.WindowY is { } wy)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)wx, (int)wy);
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        MainGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(settings.SidebarWidth, 120, 800));

        _textMateRegistry = Warmup.TextMateRegistry();
        _textMate = Editor.InstallTextMate(_textMateRegistry);
        StartupTrace.Mark("  textmate");

        ConfigureEditor();
        BuildCommands();
        BuildMenu();
        BuildToolbar();
        StartupTrace.Mark("  menus, toolbar");
        SetupSidebar();
        SetupPreview();
        SetupEditorContextMenu();
        SetupConflicts();
        SetupCellEditorDismiss();
        ApplyThemeToEditor();

        FilesTree.SelectionChanged += OnTreeSelectionChanged;
        // ✕ on a hovered row closes that file (asking about unsaved changes as usual), without activating it first.
        FilesTree.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c, DataContext: EditorDocument doc } && c.Contains("rowClose"))
            {
                e.Handled = true;
                CloseByUser(doc);
            }
        });
        FilesTree.ContainerPrepared += OnTreeContainerPrepared;
        _files.GroupsRebuilt += (_, _) => SyncTreeSelection();
        _files.GroupsRebuilt += (_, _) => Dispatcher.UIThread.Post(ForgetRemovedFocus, DispatcherPriority.Background);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        // handledEventsToo: the editor's own text drag & drop sees the files first and may mark the event handled.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);
        _monitor.Changed += path => Dispatcher.UIThread.Post(() => OnFileChangedOnDisk(path));
        Activated += (_, _) =>
        {
            PollFilesOnDisk();
            if (_headComparison is { } comparison) _ = RefreshHeadDiffAsync(comparison.Root);
        };
        ActualThemeVariantChanged += (_, _) => ApplyThemeToEditor();
        StatusEol.Click += (_, _) => ShowEolMenu(StatusEol);
        StatusEncoding.Click += (_, _) => ShowEncodingMenu(StatusEncoding);
        StatusIndent.Click += (_, _) => ShowIndentMenu(StatusIndent);
        StatusMdView.Click += (_, _) => { ToggleMarkdownRaw(); Editor.Focus(); };
        StatusType.Click += (_, _) =>
        {
            if (_active is null) return;
            var menu = new ContextMenu();
            var source = BuildDisplayAsMenu(_active);
            var items = source.Items.Cast<object>().ToList();
            source.Items.Clear(); // an item can have only one parent menu
            foreach (var item in items) menu.Items.Add(item);
            menu.Open(StatusType);
        };
        ToolTip.SetTip(StatusType, Loc.T("Panel.DisplayAs"));
        ToolTip.SetTip(StatusMdView, Loc.T("Menu.Md.Raw") + " (Ctrl+Shift+M)");

        Opened += async (_, _) =>
        {
            StartupTrace.Mark("window opened");
            NativeSplash.Close();
            EnsureOnScreen();
            BringToFront();
            _gitOnlyStartup = RequestedGitRoot(args) is not null;
            if (!_gitOnlyStartup) await RestoreSessionAsync();
            await OpenLaunchTargetsAsync(args);
            if (!_gitOnlyStartup && _files.Documents.Count == 0) NewDocument();
            if (_files.Documents.Count == 0) Editor.IsReadOnly = true;
            if (!_gitOnlyStartup) Editor.Focus();
            _sessionRestored = true;
            StartupTrace.Mark("session restored");
            ScheduleSessionSave();
        };
        _files.GroupsRebuilt += (_, _) => { ScheduleSessionSave(); UpdateGitPanel(); };
    }

    /// <summary>
    /// Started by another process (dotnet run, an IDE, a script), Windows may open the window behind the others
    /// and only flash the taskbar button; see <see cref="NativeWindow.ForceForeground"/>.
    /// </summary>
    /// <summary>Files passed by a second start of the program (see <see cref="SingleInstance"/>).</summary>
    internal async Task OpenFromOtherInstanceAsync(IReadOnlyList<string> targets)
    {
        BringToFront();
        await OpenLaunchTargetsAsync(targets);
        // The sending process may still hold the foreground for a moment; ask again once it has exited.
        await Task.Delay(250);
        BringToFront();
    }

    private async Task OpenLaunchTargetsAsync(IReadOnlyList<string> targets)
    {
        foreach (var file in targets.Where(arg => !arg.StartsWith('-')))
            await OpenFileAsync(file);
        if (RequestedGitRoot(targets) is { } root)
        {
            _launchedGitRoot = _selectedGitRoot = root;
            ShowSideTab(GitTab);
            UpdateGitPanel();
        }
    }

    private static int LongestLine(TextDocument doc)
    {
        int max = 0;
        foreach (var line in doc.Lines) if (line.Length > max) max = line.Length;
        return max;
    }

    /// <summary>
    /// Converts the whole file to a line-break convention ("Current file: … ▾"), one undo step. Breaking at a width or
    /// at sentences splits every long prose line (longer than the width, or than 120 characters for sentences); one
    /// line per paragraph joins the lines of each paragraph. Only line breaks change, never a word, which the word
    /// diff offered afterwards shows. When there is nothing to convert, nothing changes and no notice is shown.
    /// </summary>
    private void WrapLongLines(EditorDocument d, EelDitor.Core.Text.WrapMode mode)
    {
        var doc = d.Document;
        string text = doc.Text;
        string wrapped;
        int joined = 0;
        if (mode == EelDitor.Core.Text.WrapMode.OneLine)
            (wrapped, joined) = EelDitor.Core.Text.ParagraphWrap.JoinParagraphs(text, d.IsMarkdown);
        else
            wrapped = EelDitor.Core.Text.ParagraphWrap.SplitLongLines(text, mode, d.IsMarkdown, d.Eol.ToNewLine(), mode.Width() ?? 120);
        if (wrapped == text)
        {
            SetStatusMessage(Loc.T("Info.NothingToConvert"));
            return;
        }
        int changed = EelDitor.Core.Diff.LineDiff.Compute(text, wrapped).Blocks.Sum(b => b.OldCount);
        doc.Replace(0, doc.TextLength, wrapped);
        d.ReflowNotice = mode == EelDitor.Core.Text.WrapMode.OneLine
            ? Loc.T("Info.Joined", changed, joined)
            : Loc.T("Info.Reflowed", changed, LongestLine(doc));
        d.ReflowVersion = doc.Version;
        UpdateInfoBar();
        UpdateStatus();
    }

    /// <summary>A prose document: Markdown or plain text (never source code).</summary>
    private static bool IsProseDocument(EditorDocument d) =>
        d.IsMarkdown || d.SyntaxOverride == EditorDocument.PlainTextSyntax
        || d.SyntaxOverride is null && (d.FilePath is null || string.Equals(Path.GetExtension(d.FilePath), ".txt", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Line breaking while typing (Edit → Paragraph breaking). Only the line being typed is touched, and in the
    /// width modes only when this input makes it cross the limit, so paragraphs written in another style stay as
    /// they are. Ctrl+Z undoes the break alone.
    /// </summary>
    private void BreakLineAfterTyping(string? typed)
    {
        var mode = _settings.ParagraphWrap;
        if (mode == EelDitor.Core.Text.WrapMode.OneLine || string.IsNullOrEmpty(typed) || typed.Contains('\n')
            || _active is not { } d || Editor.IsReadOnly || !IsProseDocument(d) || Editor.TextArea.Selection is AvaloniaEdit.Editing.RectangleSelection)
            return;
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        if (line.Length > 4000) return;
        if (d.IsMarkdown && _markdown?.RenderFor(line).Kind is { } kind && kind != EelDitor.Markdown.MdLineKind.Normal) return;
        string text = doc.GetText(line);
        if (!EelDitor.Core.Text.ParagraphWrap.IsProse(text, d.IsMarkdown)) return;
        if (d.IsMarkdown && EelDitor.Core.Text.ParagraphWrap.InFrontMatter(
                i => i < doc.LineCount ? doc.GetText(doc.GetLineByNumber(i + 1)) : null, line.LineNumber - 1))
            return;
        var cut = EelDitor.Core.Text.ParagraphWrap.BreakAfterTyping(text, Editor.CaretOffset - line.Offset, typed.Length, mode, d.IsMarkdown);
        if (cut is not { } c) return;
        doc.Replace(line.Offset + c.Start, c.Length, d.Eol.ToNewLine() + c.Prefix);
    }

    /// <summary>Edit → Paragraph breaking → Reflow: the paragraph(s) at the caret or in the selection, in the current mode.</summary>
    private void ReflowParagraphs()
    {
        if (_active is not { } d || Editor.IsReadOnly) return;
        var doc = Editor.Document;
        var lines = Enumerable.Range(1, doc.LineCount).Select(n => doc.GetText(doc.GetLineByNumber(n))).ToList();
        int first = doc.GetLineByOffset(Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset).LineNumber - 1;
        int last = doc.GetLineByOffset(Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : Editor.CaretOffset).LineNumber - 1;
        var paragraphs = new List<(int Start, int End)>();
        for (int i = first; i <= last; i++)
            if (EelDitor.Core.Text.ParagraphWrap.FindParagraph(lines, i, d.IsMarkdown) is { } p && (paragraphs.Count == 0 || paragraphs[^1] != p))
            {
                paragraphs.Add(p);
                i = p.End;
            }
        if (paragraphs.Count == 0) { SetStatusMessage(Loc.T("Wrap.NoParagraph")); return; }
        string nl = d.Eol.ToNewLine();
        using (doc.RunUpdate())
        {
            // From the bottom up, so earlier line numbers stay valid.
            foreach (var (start, end) in Enumerable.Reverse(paragraphs))
            {
                var startLine = doc.GetLineByNumber(start + 1);
                var endLine = doc.GetLineByNumber(end + 1);
                string replacement = string.Join(nl, EelDitor.Core.Text.ParagraphWrap.Reflow(lines, start, end, _settings.ParagraphWrap, d.IsMarkdown));
                if (doc.GetText(startLine.Offset, endLine.EndOffset - startLine.Offset) != replacement)
                    doc.Replace(startLine.Offset, endLine.EndOffset - startLine.Offset, replacement);
            }
        }
    }

    private void SetParagraphWrap(EelDitor.Core.Text.WrapMode mode)
    {
        _settings.ParagraphWrap = mode;
        UpdateStatus();
        RefreshCommandStates();
    }

    /// <summary>A saved position on a monitor that is no longer connected: center on the primary screen.</summary>
    private void EnsureOnScreen()
    {
        if (WindowState != WindowState.Normal || Screens.All.Count == 0) return;
        var center = new PixelPoint(Position.X + (int)(Width / 2), Position.Y + 20);
        if (Screens.All.Any(s => s.WorkingArea.Contains(center))) return;
        var area = (Screens.Primary ?? Screens.All[0]).WorkingArea;
        Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)Width) / 2), area.Y + Math.Max(0, (area.Height - (int)Height) / 2));
    }

    /// <summary>The bar above the text: file name, its folder, and ✕ to close it.</summary>
    private void UpdateDocumentHeader()
    {
        DocHeader.IsVisible = _active is not null;
        // While comparing, the comparison has its own ✕ (Close diff); a second ✕ here would close the file instead.
        DocClose.IsVisible = _diffView is null;
        if (_active is null) return;
        DocHeaderName.Text = (_active.IsDirty ? "● " : "") + _active.FileName;
        DocHeaderPath.FullPath = _active.DirectoryPath is { Length: > 0 } dir ? OpenFilesModel.HomeRelative(dir) : "";
        ToolTip.SetTip(DocHeaderPath, _active.DirectoryPath);
        ToolTip.SetTip(DocHeaderName, Loc.T(_active.IsDirty ? "Doc.DirtyTip" : "Doc.CleanTip"));
    }

    /// <summary>Line numbers a bit smaller than the text, as in most editors.</summary>
    internal static void ApplyLineNumberFont(AvaloniaEdit.TextEditor editor)
    {
        foreach (var m in editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>())
        {
            m.SetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty, Math.Max(7, Math.Round(editor.FontSize * 0.8)));
            m.InvalidateMeasure();
        }
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        base.Activate();
        if (TryGetPlatformHandle()?.Handle is { } hwnd) NativeWindow.ForceForeground(hwnd);
    }

    private void ConfigureEditor()
    {
        Editor.Document = _emptyDocument;
        EditorScroll.KeepOnText(Editor);
        SymbolWeight.Install(Editor.TextArea.TextView);
        Editor.FontFamily = new FontFamily(_settings.FontFamily);
        SymbolWidths.Use(Editor.FontFamily);
        SymbolText.WarmUp(Editor.FontFamily);
        Editor.FontSize = _settings.FontSize;
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        Editor.WordWrap = _settings.WordWrap;
        var o = Editor.Options;
        o.EnableRectangularSelection = true;
        o.EnableTextDragDrop = true;
        o.AllowToggleOverstrikeMode = true;
        o.HighlightCurrentLine = _settings.HighlightCurrentLine;
        o.EnableHyperlinks = false;
        o.EnableEmailHyperlinks = false;
        o.AllowScrollBelowDocument = true;
        // Marks for spaces, tabs and line ends are drawn outside the text layout (AvaloniaEdit's own changed line
        // heights and the width of headings).
        _whitespaceMarks = new EelDitor.App.Editor.WhitespaceMarks(Editor) { Spaces = _settings.ShowSpacesAndTabs, LineEnds = _settings.ShowEndOfLine };
        o.IndentationSize = _settings.TabSize;
        o.ConvertTabsToSpaces = _settings.UseSpaces;
        o.CutCopyWholeLine = true;

        var area = Editor.TextArea;
        // Status and toolbar updates must never throw into AvaloniaEdit: its caret moves in the middle of operations
        // (switching documents), and an exception there leaves the editor half updated.
        void Safely(Action update)
        {
            try { update(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Write(ex); }
        }
        area.Caret.PositionChanged += (_, _) => Safely(() => { UpdateCaretStatus(); UpdateTableTools(); });
        area.SelectionChanged += (_, _) => Safely(UpdateCaretStatus);
        area.TextEntered += (_, e) => { BreakLineAfterTyping(e.Text); UpdateLengthStatus(); };

        area.PropertyChanged += (_, e) =>
        {
            if (e.Property == AvaloniaEdit.Editing.TextArea.OverstrikeModeProperty) UpdateCaretStatus();
        };
        area.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        // Web addresses outside the Markdown view (plain text, code, raw mode): underlined, Ctrl+click opens them.
        var plainLinks = new EelDitor.App.Editor.PlainLinks(Editor, () => _markdown is null, () => Palette.Accent);
        plainLinks.LinkActivated += url => { if (_active is { } doc) _ = OpenLinkAsync(url, doc); };
        ApplyLineNumberFont(Editor);
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == AvaloniaEdit.TextEditor.FontSizeProperty || e.Property == AvaloniaEdit.TextEditor.ShowLineNumbersProperty) ApplyLineNumberFont(Editor);
        };
        DocClose.Click += (_, _) => { if (_active is not null) CloseByUser(_active); };
        // Ctrl+wheel zooms the editor and both sides of a comparison.
        EditorHost.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            Zoom(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Editor.TextChanged += (_, _) =>
        {
            UpdateLengthStatus();
            DropStaleNotices();
            // The file's convention, longest line and line endings: whole-text scans, so not on every key press.
            ScheduleTextSettled();
        };
    }

    private EditorPalette Palette => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? EditorPalette.Light : EditorPalette.Dark;

    private void ApplyThemeToEditor()
    {
        var p = Palette;
        Editor.Foreground = p.Foreground;
        Editor.LineNumbersForeground = p.LineNumbers;
        Editor.TextArea.TextView.CurrentLineBackground = p.CurrentLine;
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(p.CurrentLine);
        Editor.TextArea.SelectionBrush = p.Selection;
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.TextView.NonPrintableCharacterBrush = p.Whitespace;
        if (_whitespaceMarks is not null) _whitespaceMarks.Brush = p.Whitespace;
        var tmTheme = p == EditorPalette.Light ? ThemeName.LightPlus : ThemeName.DarkPlus;
        _textMate.SetTheme(_textMateRegistry.LoadTheme(tmTheme));
        // TextMate applies the theme's own background; keep ours so the editor matches the window chrome.
        Editor.Background = p.Background;
        if (_markdown is not null) _markdown.Palette = p;
        _diffView?.ApplyPalette(p, tmTheme);
        if (_diffView is not null) _diffLinePanel?.Show(_diffView);
        _documentMap?.ApplyPalette(p);
        SchedulePreview();
        _searchHighlighter?.SetBrush(p.SearchHighlight);
    }

    // ---------------------------------------------------------------- activation

    internal void Activate(EditorDocument? doc)
    {
        if (ReferenceEquals(doc, _active)) return;
        // The comparison belongs to the document it was opened for; switching files closes it.
        if (_diffView is not null) CloseDiff();
        if (_active is not null)
        {
            _active.CaretOffset = Editor.CaretOffset;
            _active.VerticalOffset = Editor.VerticalOffset;
            _active.HorizontalOffset = Editor.HorizontalOffset;
        }

        _markdown?.Dispose();
        _markdown = null;
        _active = doc;
        _files.Active = doc;

        if (doc is null)
        {
            Editor.Document = _emptyDocument;
            Editor.IsReadOnly = true;
            UpdateTitle();
            UpdateStatus();
            UpdateInfoBar();
            return;
        }

        Editor.IsReadOnly = false;
        Editor.Document = doc.Document;
        ApplySyntax(doc);
        ApplyCyrillicView(doc);
        Editor.WordWrap = _settings.WordWrap && !doc.IsLarge;
        Editor.CaretOffset = Math.Clamp(doc.CaretOffset, 0, doc.Document.TextLength);
        Dispatcher.UIThread.Post(() =>
        {
            Editor.SetVerticalOffset(doc.VerticalOffset);
            Editor.SetHorizontalOffset(doc.HorizontalOffset);
        }, DispatcherPriority.Background);

        SyncTreeSelection();
        UpdateTitle();
        UpdateStatus();
        UpdateInfoBar();
        RefreshCommandStates();
        OnActiveDocumentChangedForSearch();
        ScheduleSessionSave();
        UpdateGitPanel();
        ScanConflicts();
        UpdateTableTools();
    }

    private CyrillicViewGenerator? _cyrillicView;

    /// <summary>Cyrillic display of the active document: read-only while on; Markdown formatting stays.</summary>
    private void ApplyCyrillicView(EditorDocument doc)
    {
        _cyrillicView?.Dispose();
        _cyrillicView = null;
        if (doc.CyrillicView is { } id)
        {
            _cyrillicView = new CyrillicViewGenerator(id, Editor.TextArea, _markdown);
            Editor.IsReadOnly = true;
        }
        Editor.TextArea.TextView.Redraw();
    }

    private void SetCyrillicView(string? id)
    {
        if (_active is not { } doc) return;
        doc.CyrillicView = id;
        Editor.IsReadOnly = false;
        _cyrillicView?.Dispose();
        _cyrillicView = null;
        _markdown?.Dispose();
        _markdown = null;
        ApplySyntax(doc);
        ApplyCyrillicView(doc);
        SetStatusMessage(id is null ? "" : Loc.T("Cyrillic.ViewOn"));
        RefreshCommandStates();
    }

    /// <summary>Converts the selection (or the whole document) between Latin and Cyrillic; one undo step.</summary>
    private void ConvertAlphabet(Func<string, string> convert)
    {
        if (_active is null || Editor.IsReadOnly) return;
        var doc = Editor.Document;
        bool selection = Editor.SelectionLength > 0 && Editor.TextArea.Selection is AvaloniaEdit.Editing.SimpleSelection;
        int start = selection ? Editor.SelectionStart : 0, length = selection ? Editor.SelectionLength : doc.TextLength;
        string text = doc.GetText(start, length), converted = convert(text);
        if (converted == text) return;
        doc.Replace(start, length, converted);
        if (selection) Editor.Select(start, converted.Length);
    }

    private MarkdownView CreateMarkdownView(EditorDocument doc)
    {
        var view = new MarkdownView(Editor, doc.Document, Palette, doc.DirectoryPath);
        // Select column / row in a table shown as text: style commands act on those cells.
        view.TextModeCellRanges = () => _tableSelection?.Ranges ?? [];
        view.LinkActivated += url => _ = OpenLinkAsync(url, doc);
        view.CellEditRequested += EditTableCell;
        return view;
    }

    /// <summary>
    /// Ctrl+click on a Markdown link: web and mail addresses open in the default browser/mail program,
    /// relative or absolute paths open in the editor. Nothing else is executed.
    /// </summary>
    private async Task OpenLinkAsync(string url, EditorDocument from)
    {
        url = url.Trim().Trim('<', '>');
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatusMessage(ex.Message); }
            return;
        }
        string path = url;
        int hash = path.IndexOf('#');
        if (hash >= 0) path = path[..hash];
        if (path.Length == 0) return; // an anchor in this file
        if (EelDitor.App.Editor.ImageCache.Resolve(from.DirectoryPath, path) is { } full && File.Exists(full))
            await OpenFileAsync(full);
        else SetStatusMessage(Loc.T("Link.NotFound", url));
    }

    private void ApplySyntax(EditorDocument doc)
    {
        // Large files open without highlighting; choosing "Display as" explicitly turns it on anyway.
        if (doc.IsMarkdown && (!doc.IsLarge || doc.SyntaxOverride == EditorDocument.MarkdownSyntax))
        {
            _textMate.SetGrammar(null);
            if (!_settings.MarkdownRaw) _markdown = CreateMarkdownView(doc);
            if (_cyrillicView is not null) ApplyCyrillicView(doc); // it keeps the Markdown view it formats with
            doc.SyntaxName = "Markdown";
            return;
        }
        if (doc.IsLarge && doc.SyntaxOverride is null || doc.SyntaxOverride == EditorDocument.PlainTextSyntax
            || doc.SyntaxOverride is null && string.IsNullOrEmpty(doc.SyntaxName) && doc.SyntaxPath is null)
        {
            _textMate.SetGrammar(null);
            if (string.IsNullOrEmpty(doc.SyntaxName) || doc.SyntaxOverride == EditorDocument.PlainTextSyntax) doc.SyntaxName = Loc.T("Syntax.PlainText");
            return;
        }
        string? scope = null;
        var lang = doc.SyntaxOverride is { } overrideId
            ? _textMateRegistry.GetAvailableLanguages().FirstOrDefault(l => l.Id == overrideId)
            : doc.SyntaxPath is not { } path ? null : _textMateRegistry.GetLanguageByExtension(Path.GetExtension(path));
        if (lang is not null) scope = _textMateRegistry.GetScopeByLanguageId(lang.Id);
        _textMate.SetGrammar(scope);
        doc.SyntaxName = lang is null ? Loc.T("Syntax.PlainText") : (lang.Aliases?.FirstOrDefault() ?? lang.Id);
    }

    /// <summary>"Display as" for a document: a TextMate language id, plain text, Markdown, or null (by extension).</summary>
    private void SetSyntax(EditorDocument doc, string? syntax)
    {
        Activate(doc);
        doc.SyntaxOverride = syntax;
        doc.SyntaxName = "";
        _markdown?.Dispose();
        _markdown = null;
        ApplySyntax(doc);
        SchedulePreview();
        UpdateStatus();
        RefreshCommandStates();
        ScheduleSessionSave();
    }

    // ---------------------------------------------------------------- open files panel

    private bool _syncingTree;

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree) return;
        if (FilesTree.SelectedItem is EditorDocument doc) Activate(doc);
    }

    private void OnTreeContainerPrepared(object? sender, Avalonia.Controls.ContainerPreparedEventArgs e)
    {
        // Nested items get their containers from the parent TreeViewItem, which raises its own event.
        if (e.Container is TreeViewItem nested)
        {
            nested.ContainerPrepared -= OnTreeContainerPrepared;
            nested.ContainerPrepared += OnTreeContainerPrepared;
        }
        if (e.Container is TreeViewItem item && item.DataContext is FileGroup group)
        {
            item.IsExpanded = group.IsExpanded;
            item.PropertyChanged += (_, pe) =>
            {
                if (pe.Property == TreeViewItem.IsExpandedProperty) _files.SetGroupExpanded(group, item.IsExpanded);
            };
        }
        if (e.Container is TreeViewItem fileItem && fileItem.DataContext is EditorDocument)
        {
            fileItem.ContextMenu = BuildFileContextMenu(fileItem);
            fileItem.AddHandler(PointerReleasedEvent, (_, pe) =>
            {
                if (pe.InitialPressMouseButton == MouseButton.Middle && fileItem.DataContext is EditorDocument d)
                    CloseByUser(d);
            }, RoutingStrategies.Tunnel);
        }
        else if (e.Container is TreeViewItem groupItem && groupItem.DataContext is FileGroup)
        {
            groupItem.ContextMenu = BuildGroupContextMenu(groupItem);
        }
    }

    /// <summary>
    /// Avalonia's attached properties in which a control remembers an element that had the focus: a focus scope's last
    /// focused element and a Tab-once container's (the sidebar's tabs) last active one.
    /// </summary>
    private static readonly AvaloniaProperty[] RememberedFocusProperties =
        AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(typeof(TabControl))
            .Where(p => p.Name is "FocusedElement" or "TabOnceActiveElement" && typeof(IInputElement).IsAssignableFrom(p.PropertyType))
            .ToArray();

    /// <summary>
    /// The sidebar remembers the element that last had the focus in it, also when it was the row of a file that is now
    /// closed and gone from the tree: through the row's data the closed document — the whole text, 120 MB for a 30 MB
    /// file — stayed in memory until something else in the sidebar got the focus (found with a heap dump). A remembered
    /// element that is no longer in the window is forgotten.
    /// </summary>
    private void ForgetRemovedFocus()
    {
        foreach (var holder in new Control[] { SideTabs, FilesTree, FilesTab })
            foreach (var property in RememberedFocusProperties)
                if (holder.GetValue(property) is Visual v && Avalonia.VisualTree.VisualExtensions.GetVisualRoot(v) is null)
                    holder.ClearValue(property);
    }

    private void SyncTreeSelection()
    {
        _syncingTree = true;
        try { FilesTree.SelectedItem = _active; }
        finally { _syncingTree = false; }
    }

    // ---------------------------------------------------------------- keyboard

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ShowSwitcher(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
            return;
        }
        if (_switcher?.IsOpen == true && _switcher.HandleKey(e)) { e.Handled = true; return; }
        // Enter, Esc and Alt+letter belong to the find bar while it has the focus.
        if (_findBar is { IsVisible: true } && _findBar.IsFocusWithin && _findBar.HandleKey(e)) { e.Handled = true; return; }

        // Text boxes (filter, dialogs) keep their own editing keys.
        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (e.Source is TextBox && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && !functionKey) return;
        if (e.Source is TextBox && e.Key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Left or Key.Right or Key.Home or Key.End or Key.Back or Key.Delete) return;

        // While comparing, editing commands would act on the hidden main editor; let the focused diff editor
        // handle those keys natively (copy, paste, undo…) and keep only file, view and diff commands.
        if (_keyMap.Handle(e, _diffView is null ? null : AllowedWhileComparing))
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) _altShortcutUsed = true;
        }
    }

    /// <summary>
    /// An Alt+key shortcut was handled while Alt is down. Releasing Alt then must not open the menu bar: Avalonia
    /// does that when Alt is pressed alone, and the handled key never reached it.
    /// </summary>
    private bool _altShortcutUsed;

    private static bool AllowedWhileComparing(AppCommand c) =>
        // Undo/redo go to the diff's editable side (also from the word view, which shows a copy).
        c.Id is "edit.undo" or "edit.redo" ||
        !(c.Id.StartsWith("edit.", StringComparison.Ordinal)
          || c.Id.StartsWith("md.", StringComparison.Ordinal)
          || (c.Id.StartsWith("search.", StringComparison.Ordinal) && c.Id is not ("search.findInFiles" or "search.nextResult" or "search.prevResult")));

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && _switcher?.IsOpen == true) _switcher.Commit();
        if (e.Key is Key.LeftAlt or Key.RightAlt && _altShortcutUsed)
        {
            _altShortcutUsed = false;
            e.Handled = true;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_markdown is null || e.Handled) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool plain = (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
        // On a table drawn as a grid: the arrows go from cell to cell, Enter edits the current cell.
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter && HandleGridKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Tab when plain:
                if (MarkdownCommands.HandleTab(Editor, _markdown, shift)) e.Handled = true;
                break;
            case Key.Left when plain:
            case Key.Right when plain:
                if (MarkdownCommands.HandleHorizontalMove(Editor, _markdown, e.Key == Key.Right, shift)) e.Handled = true;
                break;
            case Key.Back when e.KeyModifiers == KeyModifiers.None:
            case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                if (MarkdownCommands.HandleDelete(Editor, _markdown, e.Key == Key.Back)) e.Handled = true;
                break;
            case Key.Enter when plain:
                // In a list: the next item (renumbered), or Shift+Enter for a line inside the item.
                if (MarkdownCommands.HandleListEnter(Editor, _markdown, shift)) { e.Handled = true; Editor.TextArea.Caret.BringCaretToView(); }
                break;
        }
    }

    // ---------------------------------------------------------------- status bar

    private void UpdateTitle()
    {
        string name = _active is null ? "" : (_active.FilePath ?? _active.FileName);
        Title = _active is null ? AppInfo.Name : $"{AppInfo.Name} — {(_active.IsDirty ? "● " : "")}{name}";
        UpdateDocumentHeader();
    }

    private void UpdateStatus()
    {
        UpdateCaretStatus();
        UpdateLengthStatus();
        if (_active is null)
        {
            StatusType.Content = "";
            StatusEol.Content = StatusEncoding.Content = StatusIndent.Content = "";
            StatusType.Content = "";
            return;
        }
        StatusType.Content = _active.SyntaxName;
        StatusMdView.IsVisible = StatusMdSep.IsVisible = _active is { IsMarkdown: true, IsLarge: false };
        StatusMdView.Content = Loc.T(_settings.MarkdownRaw ? "Status.MdPlain" : "Status.MdFormatted");
        StatusEol.Content = _active.EolMixed ? $"{Loc.T("Status.Mixed")} ({_active.Eol.ShortName()})" : _active.Eol.DisplayName();
        StatusEncoding.Content = _active.Encoding.DisplayName + (_active.IsLossyDecode ? " ⚠" : "");
        UpdateParagraphTools();
        StatusIndent.Content = Editor.Options.ConvertTabsToSpaces
            ? Loc.T("Status.Spaces", Editor.Options.IndentationSize)
            : Loc.T("Status.Tabs", Editor.Options.IndentationSize);
    }

    private void UpdateLengthStatus()
    {
        var d = Editor.Document;
        StatusLength.Text = Loc.T("Status.Length", d.TextLength.ToString("N0"), d.LineCount.ToString("N0"));
        if (_active is not null) UpdateTitle();
    }

    private void UpdateCaretStatus()
    {
        var area = Editor.TextArea;
        var caret = area.Caret;
        int selChars = 0, selLines = 0;
        // While another document is being shown, AvaloniaEdit moves the caret before it clears the old selection: its
        // offsets still belong to the old document. Reading them threw inside AvaloniaEdit's document switch, which was
        // left half done (the old selection drawn over the new document failed every frame: the window stood still).
        if (!area.Selection.IsEmpty && area.Selection.SurroundingSegment.EndOffset <= Editor.Document.TextLength)
        {
            selChars = area.Selection.Segments.Sum(s => s.Length);
            var seg = area.Selection.SurroundingSegment;
            selLines = Editor.Document.GetLineByOffset(seg.EndOffset).LineNumber - Editor.Document.GetLineByOffset(seg.Offset).LineNumber + 1;
        }
        StatusPosition.Text = Loc.T("Status.Position", caret.Line, caret.Column, selChars, selLines);
        StatusInsert.Text = area.OverstrikeMode ? "OVR" : "INS";
    }

    internal void ReportError(Exception ex) =>
        SetStatusMessage(Loc.T("Error.Unexpected", ex.GetType().Name, ex.Message, ErrorLog.FilePath));

    private void SetStatusMessage(string text)
    {
        StatusMessage.Text = text;
        _statusTimer?.Dispose();
        _statusTimer = DispatcherTimer.RunOnce(() => StatusMessage.Text = "", TimeSpan.FromSeconds(6));
    }

    private IDisposable? _statusTimer;

    // ---------------------------------------------------------------- info bar

    private void UpdateInfoBar()
    {
        InfoBarButtons.Children.Clear();
        var d = _active;
        if (d is null) { InfoBar.IsVisible = false; return; }

        void Button(string label, Action action)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 2), FontSize = 12 };
            b.Click += (_, _) => action();
            InfoBarButtons.Children.Add(b);
        }

        if (d.DiskState == DiskState.ChangedOnDisk)
        {
            InfoBarText.Text = Loc.T("Info.ChangedOnDisk");
            Button(Loc.T("Info.Reload"), () => _ = ReloadAsync(d, null));
            Button(Loc.T("Info.Compare"), () => _ = CompareWithDiskAsync(d));
            Button(Loc.T("Info.KeepMine"), () => { d.DiskState = DiskState.Normal; d.MetadataDirty = true; UpdateInfoBar(); });
        }
        else if (d.DiskState == DiskState.Deleted)
        {
            InfoBarText.Text = Loc.T("Info.Deleted");
            Button(Loc.T("Info.KeepMine"), () => { d.DiskState = DiskState.Normal; d.MetadataDirty = true; UpdateInfoBar(); });
            Button(Loc.T("Menu.File.Close"), () => _ = CloseDocumentAsync(d, force: true));
        }
        else if (d.LooksBinary)
        {
            InfoBarText.Text = Loc.T("Info.Binary");
            Button(Loc.T("Info.Dismiss"), () => { d.LooksBinary = false; UpdateInfoBar(); });
        }
        else if (d.IsLossyDecode)
        {
            InfoBarText.Text = Loc.T("Info.Lossy", d.Encoding.DisplayName);
            Button(Loc.T("Info.ReopenAs"), () => ShowEncodingMenu(InfoBarButtons.Children[0] as Control ?? InfoBar, reopen: true));
            Button(Loc.T("Info.Dismiss"), () => { d.IsLossyDecode = false; UpdateInfoBar(); });
        }
        else if (d.EolMixed && !d.EolMixedAccepted)
        {
            InfoBarText.Text = Loc.T("Info.MixedEol", d.Eol.ShortName());
            Button(Loc.T("Info.Unify", d.Eol.ShortName()), () => ConvertEol(d.Eol));
            Button(Loc.T("Info.Leave"), () => { d.EolMixedAccepted = true; UpdateInfoBar(); UpdateStatus(); });
        }
        else if (d.ReflowNotice is { } notice)
        {
            InfoBarText.Text = notice;
            if (d.FilePath is not null)
                Button(Loc.T("Info.ShowWordDiff"), () => { d.ReflowNotice = null; _settings.DiffWordMode = true; UpdateInfoBar(); _ = CompareWithDiskAsync(d); });
            Button(Loc.T("Info.Ok"), () => { d.ReflowNotice = null; UpdateInfoBar(); });
        }
        else
        {
            InfoBar.IsVisible = false;
            return;
        }
        InfoBar.IsVisible = true;
    }

    // ---------------------------------------------------------------- drag & drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 }) e.DragEffects = DragDropEffects.Copy;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } items) return;
        e.Handled = true;
        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToList();
        // Images dropped onto a Markdown text are inserted as ![name](path) where they were dropped; other files open.
        if (_diffView is null && _active is { IsMarkdown: true } md && !Editor.IsReadOnly && Editor.IsPointerOver
            && paths.Where(EelDitor.App.Editor.MarkdownCommands.IsImageFile).ToList() is { Count: > 0 } images)
        {
            int offset = Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } pos ? Editor.Document.GetOffset(pos.Location) : Editor.CaretOffset;
            EelDitor.App.Editor.MarkdownCommands.InsertImages(Editor, md.DirectoryPath, images, offset);
            Editor.Focus();
            paths = paths.Except(images).ToList();
        }
        foreach (var path in paths) await OpenFileAsync(path);
    }

    /// <summary>Markdown → Insert image: picks image files and inserts them at the caret.</summary>
    private async Task InsertImageAsync()
    {
        if (_active is not { IsMarkdown: true } md || Editor.IsReadOnly) return;
        var start = md.DirectoryPath is { Length: > 0 } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Menu.Md.Image"),
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("Image.Files")) { Patterns = EelDitor.App.Editor.MarkdownCommands.ImageExtensions.Select(x => "*" + x).ToArray() },
                FilePickerFileTypes.All,
            ],
        });
        var images = result.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        EelDitor.App.Editor.MarkdownCommands.InsertImages(Editor, md.DirectoryPath, images, Editor.CaretOffset);
        Editor.Focus();
    }

    // ---------------------------------------------------------------- closing

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closingConfirmed) { base.OnClosing(e); return; }
        e.Cancel = true;
        var dirty = _files.Documents.Where(d => d.IsDirty).ToList();
        foreach (var d in dirty)
        {
            Activate(d);
            if (!await ConfirmCloseAsync(d)) return;
        }
        SaveSession();
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            // Also the position: a window snapped to one half of the screen comes back there.
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        _settings.SidebarWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        SettingsStore.Save(_settings);
        _monitor.Dispose();
        _closingConfirmed = true;
        Close();
    }
}
