using EelDitor.App.Editor;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.Core.Files;
using EelDitor.Core.Text;

namespace EelDitor.App.Views;

public partial class MainWindow
{
    internal void NewDocument()
    {
        var doc = new EditorDocument(new TextDocument(), null)
        {
            Eol = EolConverter.PlatformDefault,
            Encoding = TextEncoding.Utf8,
        };
        doc.PropertyChanged += OnDocumentPropertyChanged;
        WatchDirtyState(doc);
        _files.Add(doc);
        Activate(doc);
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _active)) return;
        if (e.PropertyName is nameof(EditorDocument.IsDirty) or nameof(EditorDocument.Title)) UpdateTitle();
        if (e.PropertyName is nameof(EditorDocument.DiskState) or nameof(EditorDocument.EolMixed)) UpdateInfoBar();
    }

    private async Task OpenFilesDialogAsync()
    {
        var start = _active?.DirectoryPath is { Length: > 0 } dir
            ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Menu.File.Open"),
            AllowMultiple = true,
            SuggestedStartLocation = start,
        });
        foreach (var f in result)
            if (f.TryGetLocalPath() is { } path) await OpenFileAsync(path);
    }

    public async Task<EditorDocument?> OpenFileAsync(string path, TextEncoding? encoding = null, bool activate = true)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return null; }

        if (_files.FindByPath(full) is { } existing)
        {
            if (activate) Activate(existing);
            return existing;
        }
        if (!File.Exists(full))
        {
            await Dialogs.Info(this, AppInfo.Name, Loc.T("Error.FileNotFound", full));
            return null;
        }

        long size = new FileInfo(full).Length;
        bool large = size > (long)_settings.LargeFileThresholdMb * 1024 * 1024;
        if (large)
        {
            LoadingText.Text = Loc.T("Status.Loading", Path.GetFileName(full), (size / (1024.0 * 1024)).ToString("N1"));
            LoadingText.IsVisible = true;
        }

        LoadedText loaded;
        string hash;
        try
        {
            // Read and decode off the UI thread so large files never freeze the window.
            (loaded, hash) = await Task.Run(async () =>
            {
                var bytes = await File.ReadAllBytesAsync(full);
                var decoded = TextFile.Decode(bytes, encoding);
                return (decoded, Convert.ToHexString(SHA1.HashData(bytes)));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadingText.IsVisible = false;
            await Dialogs.Info(this, AppInfo.Name, Loc.T("Error.Open", full, ex.Message));
            return null;
        }

        // Building the rope also happens off-thread; the document is handed to the UI thread afterwards.
        var textDocument = await Task.Run(() =>
        {
            var d = new TextDocument(loaded.Text);
            d.SetOwnerThread(null);
            return d;
        });
        textDocument.SetOwnerThread(Thread.CurrentThread);
        textDocument.UndoStack.ClearAll();
        textDocument.FileName = full;
        LoadingText.IsVisible = false;

        var info = new FileInfo(full);
        var doc = new EditorDocument(textDocument, full)
        {
            Encoding = loaded.Encoding,
            Eol = loaded.Eol.Dominant(EolConverter.PlatformDefault),
            EolMixed = loaded.Eol.IsMixed,
            IsLossyDecode = loaded.IsLossy,
            LooksBinary = loaded.LooksBinary,
            IsLarge = large,
            FileSize = loaded.FileSize,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            LastKnownHash = hash,
        };
        doc.PropertyChanged += OnDocumentPropertyChanged;
        WatchDirtyState(doc);
        _files.Add(doc);
        _monitor.Watch(full);
        AddRecent(full);
        if (activate) Activate(doc);
        RequestGitRefresh(doc);
        return doc;
    }

    private void AddRecent(string path)
    {
        _settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.Insert(0, path);
        if (_settings.RecentFiles.Count > 20) _settings.RecentFiles.RemoveRange(20, _settings.RecentFiles.Count - 20);
        RebuildRecentMenu();
    }

    /// <summary>Reloads from disk, optionally reinterpreting the bytes with another encoding.</summary>
    private async Task ReloadAsync(EditorDocument doc, TextEncoding? encoding)
    {
        if (doc.FilePath is null) return;
        if (!File.Exists(doc.FilePath)) { doc.DiskState = DiskState.Deleted; return; }
        LoadedText loaded;
        string hash;
        try
        {
            (loaded, hash) = await Task.Run(async () =>
            {
                var bytes = await File.ReadAllBytesAsync(doc.FilePath);
                return (TextFile.Decode(bytes, encoding), Convert.ToHexString(SHA1.HashData(bytes)));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatusMessage(Loc.T("Error.Open", doc.FilePath, ex.Message));
            return;
        }

        int caret = ReferenceEquals(doc, _active) ? Editor.CaretOffset : doc.CaretOffset;
        double scroll = ReferenceEquals(doc, _active) ? Editor.VerticalOffset : doc.VerticalOffset;
        // Replace the text as one undoable edit, so a reload can be undone.
        doc.Document.Replace(0, doc.Document.TextLength, loaded.Text);
        doc.Encoding = loaded.Encoding;
        doc.Eol = loaded.Eol.Dominant(EolConverter.PlatformDefault);
        doc.EolMixed = loaded.Eol.IsMixed;
        doc.IsLossyDecode = loaded.IsLossy;
        doc.LastKnownHash = hash;
        doc.LastWriteTimeUtc = File.GetLastWriteTimeUtc(doc.FilePath);
        doc.FileSize = loaded.FileSize;
        doc.DiskState = DiskState.Normal;
        doc.MarkSaved();
        if (ReferenceEquals(doc, _active))
        {
            Editor.CaretOffset = Math.Min(caret, doc.Document.TextLength);
            Dispatcher.UIThread.Post(() => Editor.SetVerticalOffset(scroll), DispatcherPriority.Background);
            UpdateStatus();
            UpdateInfoBar();
        }
        RequestGitRefresh(doc);
    }

    private async Task<bool> SaveAsync(EditorDocument doc, bool saveAs = false)
    {
        string? path = doc.FilePath;
        if (path is null || saveAs)
        {
            var start = doc.DirectoryPath is { Length: > 0 } dir ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Menu.File.SaveAs"),
                SuggestedFileName = doc.FileName,
                SuggestedStartLocation = start,
                ShowOverwritePrompt = true,
            });
            path = file?.TryGetLocalPath();
            if (path is null) return false;
        }

        var encoding = doc.Encoding;
        string text = doc.Document.Text;
        try
        {
            TextFile.Encode(text, encoding);
        }
        catch (UnencodableCharactersException ex)
        {
            int choice = await Dialogs.Ask(this, AppInfo.Name,
                Loc.T("Save.Unencodable", encoding.DisplayName, string.Join(", ", ex.Lines)),
                Loc.T("Save.UseUtf8"), Loc.T("Button.Cancel"));
            if (choice != 0) return false;
            encoding = TextEncoding.Utf8;
        }

        try
        {
            if (doc.FilePath is not null && !saveAs) _monitor.Unwatch(doc.FilePath);
            var bytes = TextFile.Encode(text, encoding);
            doc.LastKnownHash = Convert.ToHexString(SHA1.HashData(bytes));
            doc.LastWriteTimeUtc = await TextFile.SaveAsync(path, text, encoding);
            doc.FileSize = bytes.LongLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Info(this, AppInfo.Name, Loc.T("Error.Save", path, ex.Message));
            if (doc.FilePath is not null) _monitor.Watch(doc.FilePath);
            return false;
        }

        bool renamed = !string.Equals(doc.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        if (renamed && doc.FilePath is not null) _monitor.Unwatch(doc.FilePath);
        doc.FilePath = Path.GetFullPath(path);
        doc.Document.FileName = doc.FilePath;
        _monitor.Watch(doc.FilePath);
        doc.Encoding = encoding;
        doc.DiskState = DiskState.Normal;
        doc.MarkSaved();
        if (renamed)
        {
            AddRecent(doc.FilePath);
            if (ReferenceEquals(doc, _active))
            {
                _markdown?.Dispose();
                _markdown = null;
                ApplySyntax(doc);
            }
        }
        if (ReferenceEquals(doc, _active)) { UpdateStatus(); UpdateInfoBar(); UpdateTitle(); }
        SetStatusMessage(Loc.T("Status.Saved", doc.FileName));
        RequestGitRefresh(doc);
        return true;
    }

    private async Task SaveAllAsync()
    {
        foreach (var d in _files.Documents.Where(d => d.IsDirty).ToList())
            if (!await SaveAsync(d)) return;
    }

    /// <summary>Asks to save a modified document. Returns false if the user cancelled.</summary>
    private async Task<bool> ConfirmCloseAsync(EditorDocument doc)
    {
        if (!doc.IsDirty) return true;
        int choice = await Dialogs.Ask(this, AppInfo.Name, Loc.T("Close.SaveChanges", doc.FileName),
            Loc.T("Button.Save"), Loc.T("Button.DontSave"), Loc.T("Button.Cancel"));
        return choice switch
        {
            0 => await SaveAsync(doc),
            1 => true,
            _ => false,
        };
    }

    /// <summary>
    /// Closing a file by hand (Ctrl+W, ✕, the file's menu, a middle click): when the file is being compared, the first
    /// close ends the comparison and only the next one closes the file, so closing the diff never closes the file.
    /// </summary>
    private void CloseByUser(EditorDocument doc)
    {
        if (_diffView is not null && ReferenceEquals(doc, _active))
        {
            CloseDiff();
            return;
        }
        _ = CloseDocumentAsync(doc);
    }

    private async Task CloseDocumentAsync(EditorDocument doc, bool force = false)
    {
        if (!force && !await ConfirmCloseAsync(doc)) return;
        // Choose the next document: the most recently used one.
        var next = _files.MostRecentlyUsed().FirstOrDefault(d => !ReferenceEquals(d, doc));
        if (doc.FilePath is not null) _monitor.Unwatch(doc.FilePath);
        doc.PropertyChanged -= OnDocumentPropertyChanged;
        if (ReferenceEquals(doc, _active)) Activate(next);
        _files.Remove(doc);
        if (_files.Documents.Count == 0) NewDocument();
    }

    private async Task CloseManyAsync(IEnumerable<EditorDocument> docs)
    {
        foreach (var d in docs.ToList())
        {
            if (d.IsDirty) Activate(d);
            if (!await ConfirmCloseAsync(d)) return;
            await CloseDocumentAsync(d, force: true);
        }
    }

    // ---------------------------------------------------------------- external changes

    private void OnFileChangedOnDisk(string path)
    {
        var doc = _files.FindByPath(path);
        if (doc is null) return;
        _ = CheckDiskStateAsync(doc);
    }

    private void PollFilesOnDisk()
    {
        foreach (var d in _files.Documents.Where(d => d.FilePath is not null).ToList())
            _ = CheckDiskStateAsync(d);
        RequestGitRefreshAll();
    }

    private readonly HashSet<EditorDocument> _checking = [];

    private async Task CheckDiskStateAsync(EditorDocument doc)
    {
        if (doc.FilePath is null || !_checking.Add(doc)) return;
        try
        {
            var stamp = FileMonitor.GetStamp(doc.FilePath);
            if (!stamp.Exists)
            {
                if (doc.DiskState != DiskState.Deleted) doc.DiskState = DiskState.Deleted;
                return;
            }
            if (doc.DiskState == DiskState.Deleted) doc.DiskState = DiskState.ChangedOnDisk;
            if (stamp.LastWriteUtc == doc.LastWriteTimeUtc && stamp.Length == doc.FileSize) return;

            // Ignore our own writes and touch-only changes: compare content hashes.
            string? hash = await Task.Run(() =>
            {
                try { return Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(doc.FilePath))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            });
            if (hash is null) return;
            doc.LastWriteTimeUtc = stamp.LastWriteUtc;
            doc.FileSize = stamp.Length;
            if (hash == doc.LastKnownHash) return;

            if (!doc.IsDirty && _settings.AutoReloadUnmodified)
            {
                await ReloadAsync(doc, doc.Encoding.IsUnicode ? null : doc.Encoding);
                if (ReferenceEquals(doc, _active)) SetStatusMessage(Loc.T("Status.Reloaded", doc.FileName));
            }
            else
            {
                doc.LastKnownHash = hash;
                doc.DiskState = DiskState.ChangedOnDisk;
            }
        }
        finally
        {
            _checking.Remove(doc);
            if (ReferenceEquals(doc, _active)) UpdateInfoBar();
        }
    }

    // ---------------------------------------------------------------- session

    /// <summary>Re-checks "changed?" against the file a moment after the last edit (see <see cref="EditorDocument.ClearDirtyIfSameAsDisk"/>).</summary>
    private static void WatchDirtyState(EditorDocument doc)
    {
        CancellationTokenSource? pending = null;
        doc.Document.Changed += (_, _) =>
        {
            pending?.Cancel();
            var cts = pending = new CancellationTokenSource();
            _ = Task.Delay(400, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) doc.ClearDirtyIfSameAsDisk(); });
            }, TaskScheduler.Default);
        };
    }

    private CancellationTokenSource? _sessionSaveDelay;
    private bool _sessionRestored;

    /// <summary>True once the previous session and the command-line files have been opened.</summary>
    internal bool StartupCompleted => _sessionRestored;

    /// <summary>
    /// Saves the open files shortly after they change, not only on exit, so the list survives a crash or a killed process.
    /// Nothing is written before the previous session has been restored (it would overwrite it).
    /// </summary>
    private void ScheduleSessionSave()
    {
        if (!_sessionRestored || _closingConfirmed) return;
        _sessionSaveDelay?.Cancel();
        var cts = _sessionSaveDelay = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || _closingConfirmed) return;
                SaveSession();
                SettingsStore.Save(_settings); // recent files
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void SaveSession()
    {
        if (_active is not null)
        {
            _active.CaretOffset = Editor.CaretOffset;
            _active.VerticalOffset = Editor.VerticalOffset;
        }
        var session = new Session
        {
            ActivePath = _active?.FilePath,
            Files = _files.Documents.Where(d => d.FilePath is not null).Select(d => new SessionFile
            {
                Path = d.FilePath!,
                CaretOffset = d.CaretOffset,
                VerticalOffset = d.VerticalOffset,
                EncodingCodePage = d.Encoding.IsUnicode ? null : d.Encoding.CodePage,
                Pinned = d.Pinned,
                Syntax = d.SyntaxOverride,
            }).ToList(),
        };
        SettingsStore.SaveSession(session);
    }

    private async Task RestoreSessionAsync()
    {
        var session = SettingsStore.LoadSession();
        foreach (var f in session.Files)
        {
            if (!File.Exists(f.Path)) continue;
            var enc = f.EncodingCodePage is int cp ? new TextEncoding(cp, false) : null;
            var doc = await OpenFileAsync(f.Path, enc, activate: false);
            if (doc is null) continue;
            doc.CaretOffset = f.CaretOffset;
            doc.VerticalOffset = f.VerticalOffset;
            doc.Pinned = f.Pinned;
            if (f.Syntax is not null) doc.SyntaxOverride = f.Syntax;
        }
        var active = session.ActivePath is not null ? _files.FindByPath(session.ActivePath) : null;
        Activate(active ?? _files.Documents.FirstOrDefault());
    }

    // ---------------------------------------------------------------- encoding / EOL

    private void ConvertEol(EolKind target)
    {
        if (_active is null) return;
        var doc = _active.Document;
        string text = doc.Text;
        string converted = EolConverter.Convert(text, target);
        int caretLine = Editor.TextArea.Caret.Line, caretCol = Editor.TextArea.Caret.Column;
        if (converted != text) doc.Replace(0, doc.TextLength, converted);
        _active.Eol = target;
        _active.EolMixed = false;
        Editor.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(Math.Min(caretLine, doc.LineCount), caretCol);
        UpdateStatus();
        UpdateInfoBar();
    }

    private void ConvertEncoding(TextEncoding target)
    {
        if (_active is null || _active.Encoding == target) return;
        _active.Encoding = target;
        _active.MetadataDirty = true;
        _active.IsLossyDecode = false;
        UpdateStatus();
        UpdateInfoBar();
    }

    private async Task ReopenWithEncodingAsync(TextEncoding encoding)
    {
        if (_active is null || _active.FilePath is null) return;
        if (_active.IsDirty)
        {
            int choice = await Dialogs.Ask(this, AppInfo.Name, Loc.T("Encoding.ReopenDiscard"), Loc.T("Button.Continue"), Loc.T("Button.Cancel"));
            if (choice != 0) return;
        }
        await ReloadAsync(_active, encoding);
    }

    private void ShowEolMenu(Control anchor)
    {
        var menu = new ContextMenu();
        foreach (var kind in new[] { EolKind.CrLf, EolKind.Lf, EolKind.Cr })
        {
            var k = kind;
            var item = new MenuItem { Header = Loc.T("Eol.ConvertTo", kind.DisplayName()) };
            item.Click += (_, _) => ConvertEol(k);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    private void ShowEncodingMenu(Control anchor, bool reopen = false)
    {
        var menu = new ContextMenu();
        foreach (var item in BuildEncodingItems(reopen)) menu.Items.Add(item);
        menu.Open(anchor);
    }

    private IEnumerable<object> BuildEncodingItems(bool reopen)
    {
        var header = new MenuItem { Header = Loc.T(reopen ? "Encoding.ReopenAs" : "Encoding.ConvertTo"), IsEnabled = false };
        yield return header;
        string? lastGroup = null;
        MenuItem? groupItem = null;
        foreach (var (group, enc) in TextEncoding.MenuEncodings)
        {
            var e = enc;
            var item = new MenuItem
            {
                Header = enc.DisplayName,
                Icon = _active?.Encoding == enc ? new TextBlock { Text = "✓" } : null,
            };
            item.Click += (_, _) => { if (reopen) _ = ReopenWithEncodingAsync(e); else ConvertEncoding(e); };
            if (group == "Unicode") { yield return item; continue; }
            if (group != lastGroup)
            {
                if (groupItem is not null) yield return groupItem;
                groupItem = new MenuItem { Header = Loc.T("Encoding.Group." + group) };
                lastGroup = group;
            }
            groupItem!.Items.Add(item);
        }
        if (groupItem is not null) yield return groupItem;
        if (!reopen)
        {
            yield return new Separator();
            var other = new MenuItem { Header = Loc.T("Encoding.ReopenAs") };
            foreach (var i in BuildEncodingItems(reopen: true).Skip(1)) other.Items.Add(i);
            yield return other;
        }
    }

    private void ShowIndentMenu(Control anchor)
    {
        var menu = new ContextMenu();
        foreach (var item in BuildIndentItems()) menu.Items.Add(item);
        menu.Open(anchor);
    }

    private IEnumerable<object> BuildIndentItems()
    {
        var spaces = new MenuItem { Header = Loc.T("Indent.UseSpaces"), Icon = Editor.Options.ConvertTabsToSpaces ? new TextBlock { Text = "✓" } : null };
        spaces.Click += (_, _) => SetIndentation(true, Editor.Options.IndentationSize);
        var tabs = new MenuItem { Header = Loc.T("Indent.UseTabs"), Icon = !Editor.Options.ConvertTabsToSpaces ? new TextBlock { Text = "✓" } : null };
        tabs.Click += (_, _) => SetIndentation(false, Editor.Options.IndentationSize);
        yield return spaces;
        yield return tabs;
        yield return new Separator();
        foreach (int size in new[] { 2, 3, 4, 8 })
        {
            int s = size;
            var item = new MenuItem { Header = Loc.T("Indent.Width", size), Icon = Editor.Options.IndentationSize == size ? new TextBlock { Text = "✓" } : null };
            item.Click += (_, _) => SetIndentation(Editor.Options.ConvertTabsToSpaces, s);
            yield return item;
        }
        yield return new Separator();
        var tabsToSpaces = new MenuItem { Header = Loc.T("Indent.ConvertToSpaces") };
        tabsToSpaces.Click += (_, _) => ConvertIndentation(toSpaces: true);
        var spacesToTabs = new MenuItem { Header = Loc.T("Indent.ConvertToTabs") };
        spacesToTabs.Click += (_, _) => ConvertIndentation(toSpaces: false);
        yield return tabsToSpaces;
        yield return spacesToTabs;
    }

    private void SetIndentation(bool spaces, int size)
    {
        Editor.Options.ConvertTabsToSpaces = spaces;
        Editor.Options.IndentationSize = size;
        _settings.UseSpaces = spaces;
        _settings.TabSize = size;
        UpdateStatus();
    }

    /// <summary>Converts leading indentation of every line (selected lines if there is a selection).</summary>
    private void ConvertIndentation(bool toSpaces)
    {
        var doc = Editor.Document;
        int size = Editor.Options.IndentationSize;
        var (first, last) = SelectedLineRange();
        using (doc.RunUpdate())
        {
            for (int n = first; n <= last; n++)
            {
                var line = doc.GetLineByNumber(n);
                string text = doc.GetText(line);
                int i = 0, col = 0;
                while (i < text.Length && text[i] is ' ' or '\t')
                {
                    col = text[i] == '\t' ? (col / size + 1) * size : col + 1;
                    i++;
                }
                if (i == 0) continue;
                string indent = toSpaces ? new string(' ', col) : new string('\t', col / size) + new string(' ', col % size);
                if (indent != text[..i]) doc.Replace(line.Offset, i, indent);
            }
        }
    }
}
