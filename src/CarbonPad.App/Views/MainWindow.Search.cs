using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CarbonPad.App.Documents;
using CarbonPad.App.Services;
using CarbonPad.Core.Search;
using CarbonPad.Core.Text;

namespace CarbonPad.App.Views;

public partial class MainWindow
{
    private FindWindow? _findWindow;
    private FindBar? _findBar;
    private ResultsPanel? _results;
    private SearchHighlighter? _searchHighlighter;
    private SearchOptions? _lastSearch;
    private CancellationTokenSource? _fileSearchCts;
    private TextAnchor? _scopeStart, _scopeEnd;
    private int _incrementalFrom;

    private FindWindow FindDialog
    {
        get
        {
            if (_findWindow is not null) return _findWindow;
            _findWindow = new FindWindow(_settings);
            _findWindow.ActionRequested += a => _ = OnFindAction(a);
            return _findWindow;
        }
    }

    private FindBar Bar
    {
        get
        {
            if (_findBar is not null) return _findBar;
            var bar = _findBar = new FindBar { IsVisible = false };
            bar.OptionsChanged += () => UpdateFindBar(incremental: true);
            bar.NextRequested += forward => FindFromBar(forward);
            bar.ReplaceRequested += ReplaceFromBar;
            bar.ReplaceAllRequested += ReplaceAllFromBar;
            bar.FindAllRequested += FindAllFromBar;
            bar.InFilesRequested += () => ShowFilesDialog(FilesScope.Folder, bar.FindText);
            bar.CloseRequested += CloseFindBar;
            EditorHost.Children.Add(bar);
            return bar;
        }
    }

    internal FindBar? FindBarControl => _findBar;

    private ResultsPanel Results
    {
        get
        {
            if (_results is not null) return _results;
            _results = new ResultsPanel();
            _results.HitActivated += hit => _ = GoToHitAsync(hit);
            _results.CloseRequested += () => SetResultsVisible(false);
            ResultsHost.Child = _results;
            return _results;
        }
    }

    private void SetResultsVisible(bool visible)
    {
        _ = Results;
        ResultsHost.IsVisible = visible;
        ResultsSplitter.IsVisible = visible;
        EditorGrid.RowDefinitions[3].Height = visible ? new Avalonia.Controls.GridLength(220) : new Avalonia.Controls.GridLength(0);
        RefreshCommandStates();
    }

    private void ToggleResults() => SetResultsVisible(!ResultsHost.IsVisible);

    /// <summary>Shows the search results table (the bottom panel may currently hold the git history).</summary>
    private void ShowSearchResults()
    {
        ResultsHost.Child = Results;
        SetResultsVisible(true);
    }

    // ---------------------------------------------------------------- quick find bar (Ctrl+F / Ctrl+H)

    private void ShowFindBar(bool replace)
    {
        var bar = Bar;
        string selected = Editor.SelectedText;
        bool multiLine = selected.Contains('\n') || selected.Contains('\r');
        if (selected.Length > 0 && multiLine)
        {
            // A multi-line selection becomes the search scope, like in Visual Studio.
            _scopeStart = Editor.Document.CreateAnchor(Editor.SelectionStart);
            _scopeEnd = Editor.Document.CreateAnchor(Editor.SelectionStart + Editor.SelectionLength);
            _scopeEnd.MovementType = AnchorMovementType.AfterInsertion;
            bar.Scope = SearchScope.Selection;
        }
        else if (bar.Scope == SearchScope.Selection) bar.Scope = SearchScope.Document;
        _incrementalFrom = Editor.SelectionStart;
        bar.ReplaceVisible = replace;
        bar.IsVisible = true;
        if (selected.Length > 0 && !multiLine) bar.FindText = selected;
        bar.FocusFind(replace && bar.FindText.Length > 0);
        UpdateFindBar(incremental: false);
    }

    private void CloseFindBar()
    {
        if (_findBar is null) return;
        _findBar.IsVisible = false;
        _scopeStart = _scopeEnd = null;
        if (_searchHighlighter is not null) _searchHighlighter.Regex = null;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        Editor.Focus();
    }

    /// <summary>The text range searched by the bar: the remembered selection, or the whole document.</summary>
    private (int Start, int End) BarRange()
    {
        if (_findBar?.Scope == SearchScope.Selection && _scopeStart is { IsDeleted: false } s && _scopeEnd is { IsDeleted: false } e && e.Offset > s.Offset)
            return (s.Offset, e.Offset);
        return (0, Editor.Document.TextLength);
    }

    /// <summary>Re-highlights, recounts and (while typing) jumps to the first match after where the search started.</summary>
    private void UpdateFindBar(bool incremental)
    {
        if (_findBar is not { IsVisible: true } bar) return;
        var options = bar.Options;
        if (options.Pattern.Length == 0)
        {
            bar.SetStatus("");
            Highlight(options);
            return;
        }
        Regex regex;
        try { regex = options.ToRegex(); }
        catch (ArgumentException) { bar.SetStatus(Loc.T("Find.InvalidRegexShort"), error: true); return; }
        _lastSearch = options;
        Highlight(options);
        if (incremental && bar.IsFocusWithin)
        {
            var (s, e) = BarRange();
            int from = Math.Clamp(_incrementalFrom, s, e);
            var m = SafeMatch(regex, Editor.Document.Text, from, e) ?? SafeMatch(regex, Editor.Document.Text, s, e);
            if (m is not null) SelectMatch(m.Index, m.Length);
        }
        UpdateCount(regex);
    }

    private static Match? SafeMatch(Regex regex, string text, int from, int end)
    {
        try
        {
            var m = regex.Match(text, from, end - from);
            while (m.Success && m.Length == 0) m = m.NextMatch();
            return m.Success ? m : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private void UpdateCount(Regex regex)
    {
        if (_findBar is null) return;
        var (s, e) = BarRange();
        string text = Editor.Document.Text;
        int total = 0, current = 0;
        try
        {
            for (var m = regex.Match(text, s, e - s); m.Success; m = m.NextMatch())
            {
                if (m.Length == 0) continue;
                total++;
                if (m.Index == Editor.SelectionStart && m.Length == Editor.SelectionLength) current = total;
                if (total >= TextSearch.MaxHitsPerFile) break;
            }
        }
        catch (RegexMatchTimeoutException) { _findBar.SetStatus(Loc.T("Find.Timeout"), error: true); return; }
        string totalText = total >= TextSearch.MaxHitsPerFile ? total + "+" : total.ToString();
        _findBar.SetStatus(total == 0 ? Loc.T("Find.NoResults") : current > 0 ? Loc.T("Find.Position", current, totalText) : totalText, error: total == 0);
    }

    private void SelectMatch(int index, int length)
    {
        Editor.Select(index, length);
        var loc = Editor.Document.GetLocation(index);
        Editor.ScrollTo(loc.Line, loc.Column);
    }

    private void FindFromBar(bool forward)
    {
        if (_findBar is null || _findBar.FindText.Length == 0) return;
        _lastSearch = _findBar.Options;
        Find(_lastSearch, forward);
        _incrementalFrom = Editor.SelectionStart;
        UpdateFindBar(incremental: false);
    }

    private void ReplaceFromBar()
    {
        if (_findBar is null || _findBar.FindText.Length == 0) return;
        if (!_findBar.ReplaceVisible) { _findBar.ReplaceVisible = true; _findBar.FocusFind(replace: true); return; }
        ReplaceOne(_findBar.Options, _findBar.ReplaceText, forward: true);
        UpdateFindBar(incremental: false);
    }

    private void ReplaceAllFromBar()
    {
        if (_findBar is null || _findBar.FindText.Length == 0) return;
        var options = _findBar.Options;
        try { options.ToRegex(); } catch (ArgumentException) { return; }
        int count = _findBar.Scope switch
        {
            SearchScope.OpenDocuments => _files.Documents.Sum(d => ReplaceAllIn(d, options, _findBar.ReplaceText, inSelection: false)),
            SearchScope.Selection => ReplaceInRange(options, _findBar.ReplaceText),
            _ => ReplaceAllIn(_active, options, _findBar.ReplaceText, inSelection: false),
        };
        SetStatusMessage(Loc.T("Find.Replaced", count));
        UpdateFindBar(incremental: false);
    }

    private int ReplaceInRange(SearchOptions options, string replacement)
    {
        var (s, e) = BarRange();
        var doc = Editor.Document;
        var (text, count) = TextSearch.ReplaceAll(doc.GetText(s, e - s), options, replacement);
        if (count > 0) doc.Replace(s, e - s, text);
        return count;
    }

    private void FindAllFromBar()
    {
        if (_findBar is null || _findBar.FindText.Length == 0) return;
        var options = _findBar.Options;
        try { options.ToRegex(); } catch (ArgumentException) { return; }
        if (_findBar.Scope == SearchScope.OpenDocuments) ShowHitsForDocuments(_files.Documents.ToList(), options);
        else if (_active is not null) ShowHitsForDocuments([_active], options);
    }

    // ---------------------------------------------------------------- find and replace in files (Ctrl+Shift+F / H)

    private void ShowFilesDialog(FilesScope? scope = null, string? text = null, string? directory = null)
    {
        var dlg = FindDialog;
        string selected = Editor.SelectedText;
        if (text is { Length: > 0 }) dlg.FindText = text;
        else if (selected.Length > 0 && !selected.Contains('\n') && !selected.Contains('\r')) dlg.FindText = selected;
        if (scope is { } sc) dlg.Scope = sc;
        if (directory is not null) dlg.Directory = directory;
        else if (string.IsNullOrWhiteSpace(dlg.Directory) && _active?.DirectoryPath is { Length: > 0 } dir) dlg.Directory = dir;
        if (!dlg.IsVisible) dlg.Show(this);
        dlg.Activate();
        Dispatcher.UIThread.Post(dlg.FocusFind, DispatcherPriority.Input);
    }

    private async Task OnFindAction(FindAction action)
    {
        var dlg = FindDialog;
        var options = dlg.Options;
        if (action == FindAction.Cancel) { _fileSearchCts?.Cancel(); return; }
        try { options.ToRegex(); }
        catch (ArgumentException ex)
        {
            dlg.ShowMessage(Loc.T("Find.InvalidRegex", ex.Message));
            return;
        }
        _lastSearch = options;
        dlg.ShowMessage("");
        switch (action, dlg.Scope)
        {
            case (FindAction.FindAll, FilesScope.CurrentDocument):
                if (_active is not null) ShowHitsForDocuments([_active], options);
                break;
            case (FindAction.FindAll, FilesScope.OpenDocuments):
                ShowHitsForDocuments(_files.Documents.ToList(), options);
                break;
            case (FindAction.FindAll, FilesScope.Folder):
                await FindInFilesAsync(options, dlg.FileOptions);
                break;
            case (FindAction.ReplaceAll, FilesScope.CurrentDocument):
                dlg.ShowMessage(Loc.T("Find.Replaced", ReplaceAllIn(_active, options, dlg.ReplaceText, inSelection: false)));
                break;
            case (FindAction.ReplaceAll, FilesScope.OpenDocuments):
                dlg.ShowMessage(Loc.T("Find.Replaced", _files.Documents.Sum(d => ReplaceAllIn(d, options, dlg.ReplaceText, inSelection: false))));
                break;
            case (FindAction.ReplaceAll, FilesScope.Folder):
                await ReplaceInFilesAsync(options, dlg.FileOptions, dlg.ReplaceText);
                break;
        }
    }

    // ---------------------------------------------------------------- single document

    private bool Find(SearchOptions options, bool forward)
    {
        var doc = Editor.Document;
        string text = doc.Text;
        var (rs, re) = _findBar is { IsVisible: true } ? BarRange() : (0, text.Length);
        int start = forward ? Editor.SelectionStart + Editor.SelectionLength : Editor.SelectionStart;
        Match? m;
        try
        {
            if (rs == 0 && re == text.Length) m = TextSearch.FindNext(text, options, start, forward);
            else
            {
                // Search inside the remembered selection only.
                m = TextSearch.FindNext(text[rs..re], options, Math.Clamp(start - rs, 0, re - rs), forward);
                Highlight(options);
                if (m is null) return false;
                SelectMatch(rs + m.Index, m.Length);
                return true;
            }
        }
        catch (RegexMatchTimeoutException) { SetStatusMessage(Loc.T("Find.Timeout")); return false; }
        Highlight(options);
        if (m is null) return false;
        SelectMatch(m.Index, m.Length);
        return true;
    }

    private void FindNext(bool forward)
    {
        if (_lastSearch is null) { ShowFindBar(replace: false); return; }
        if (!Find(_lastSearch, forward)) SetStatusMessage(Loc.T("Find.NotFound", _lastSearch.Pattern));
        if (_findBar is { IsVisible: true }) UpdateFindBar(incremental: false);
    }

    /// <summary>Ctrl+F3: search for the selection (or the word at the caret).</summary>
    private void FindSelected(bool forward)
    {
        string text = Editor.SelectedText;
        if (text.Length == 0)
        {
            var doc = Editor.Document;
            int c = Editor.CaretOffset, s = c, e = c;
            while (s > 0 && IsWordChar(doc.GetCharAt(s - 1))) s--;
            while (e < doc.TextLength && IsWordChar(doc.GetCharAt(e))) e++;
            if (e <= s) return;
            text = doc.GetText(s, e - s);
            Editor.Select(s, e - s);
        }
        _lastSearch = new SearchOptions(text, MatchCase: true);
        FindNext(forward);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void ReplaceOne(SearchOptions options, string replacement, bool forward)
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            // Replace the current selection if it is a match.
            var regex = options.ToRegex();
            var m = regex.Match(doc.Text, Editor.SelectionStart, Editor.SelectionLength);
            if (m.Success && m.Index == Editor.SelectionStart && m.Length == Editor.SelectionLength)
            {
                string r = m.Result(options.PrepareReplacement(replacement));
                doc.Replace(m.Index, m.Length, r);
                Editor.Select(m.Index + r.Length, 0);
            }
        }
        if (!Find(options, forward)) SetStatusMessage(Loc.T("Find.NotFound", options.Pattern));
    }

    private int ReplaceAllIn(EditorDocument? target, SearchOptions options, string replacement, bool inSelection)
    {
        if (target is null) return 0;
        var doc = target.Document;
        if (inSelection && ReferenceEquals(target, _active) && Editor.SelectionLength > 0)
        {
            var seg = Editor.TextArea.Selection.SurroundingSegment;
            var (newText, count) = TextSearch.ReplaceAll(doc.GetText(seg), options, replacement);
            if (count > 0) doc.Replace(seg.Offset, seg.Length, newText);
            return count;
        }
        var (text, n) = TextSearch.ReplaceAll(doc.Text, options, replacement);
        if (n > 0)
        {
            int caret = ReferenceEquals(target, _active) ? Editor.CaretOffset : target.CaretOffset;
            // One undo step; a full replace keeps memory low and is fast even for large files.
            doc.Replace(0, doc.TextLength, text);
            if (ReferenceEquals(target, _active)) Editor.CaretOffset = Math.Min(caret, doc.TextLength);
        }
        return n;
    }

    // ---------------------------------------------------------------- find all / in files

    private void ShowHitsForDocuments(IReadOnlyList<EditorDocument> docs, SearchOptions options)
    {
        FindWindow.Push(_settings.FindHistory, options.Pattern);
        ShowSearchResults();
        var set = Results.Begin(Loc.T("Results.Header", options.Pattern, docs.Count == 1 ? docs[0].FileName : Loc.T("Results.OpenDocs")));
        foreach (var d in docs)
        {
            var hits = TextSearch.FindAll(d.Document.Text, options, d.FilePath, d);
            if (hits.Count > 0)
                Results.AddHits(set, d.FilePath is null ? hits.Select(h => h with { FilePath = null, DocumentKey = d }).ToList() : hits);
        }
        Results.Finish(set, Loc.T("Results.Summary", set.HitCount, set.Rows.Count(r => r.FileHeader is not null)));
        Highlight(options);
    }

    private async Task FindInFilesAsync(SearchOptions options, FileSearchOptions fileOptions)
    {
        if (!Directory.Exists(fileOptions.Directory))
        {
            FindDialog.ShowMessage(Loc.T("Find.NoDirectory", fileOptions.Directory));
            return;
        }
        _fileSearchCts?.Cancel();
        var cts = _fileSearchCts = new CancellationTokenSource();
        ShowSearchResults();
        var set = Results.Begin(Loc.T("Results.Header", options.Pattern, fileOptions.Directory));
        FindDialog.ShowMessage(Loc.T("Find.Searching"));

        // Buffer hits from worker threads and push them to the UI in batches.
        var pending = new List<SearchHit>();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
        void Flush()
        {
            List<SearchHit> batch;
            lock (pending) { batch = [.. pending]; pending.Clear(); }
            if (batch.Count > 0) Results.AddHits(set, batch);
        }
        timer.Start();

        // Open documents are searched in their current (possibly unsaved) state.
        var openTexts = _files.Documents.Where(d => d.FilePath is not null)
            .ToDictionary(d => d.FilePath!, d => d.Document.Text, StringComparer.OrdinalIgnoreCase);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (files, hits) = await Task.Run(() => FileSearch.SearchAsync(fileOptions, options,
                p => openTexts.GetValueOrDefault(p),
                found => { lock (pending) pending.AddRange(found); },
                cts.Token));
            Flush();
            string summary = Loc.T("Find.InFilesDone", hits, set.Rows.Count(r => r.FileHeader is not null), files, sw.Elapsed.TotalSeconds.ToString("N1"));
            Results.Finish(set, summary);
            FindDialog.ShowMessage(summary);
        }
        catch (OperationCanceledException)
        {
            Flush();
            FindDialog.ShowMessage(Loc.T("Find.Cancelled"));
        }
        finally
        {
            timer.Stop();
        }
    }

    private async Task ReplaceInFilesAsync(SearchOptions options, FileSearchOptions fileOptions, string replacement)
    {
        if (!Directory.Exists(fileOptions.Directory)) return;
        FindDialog.ShowMessage(Loc.T("Find.Searching"));
        var openTexts = _files.Documents.Where(d => d.FilePath is not null)
            .ToDictionary(d => d.FilePath!, d => d.Document.Text, StringComparer.OrdinalIgnoreCase);
        var perFile = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() => FileSearch.SearchAsync(fileOptions, options, p => openTexts.GetValueOrDefault(p),
            found => perFile.AddOrUpdate(found[0].FilePath!, found.Count, (_, c) => c + found.Count), CancellationToken.None));
        if (perFile.IsEmpty) { FindDialog.ShowMessage(Loc.T("Find.NotFound", options.Pattern)); return; }

        int confirm = await Dialogs.Ask(FindDialog, AppInfo.Name,
            Loc.T("Find.ConfirmReplaceInFiles", perFile.Values.Sum(), perFile.Count),
            Loc.T("Find.ReplaceInFiles"), Loc.T("Button.Cancel"));
        if (confirm != 0) return;

        int replaced = 0, failed = 0;
        foreach (var path in perFile.Keys.Order())
        {
            if (_files.FindByPath(path) is { } open)
            {
                // Open files are changed in the buffer (undoable), not on disk.
                replaced += ReplaceAllIn(open, options, replacement, inSelection: false);
                continue;
            }
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var loaded = TextFile.Decode(bytes);
                if (loaded.IsLossy || loaded.LooksBinary) { failed++; continue; }
                var (text, count) = TextSearch.ReplaceAll(loaded.Text, options, replacement);
                if (count == 0) continue;
                await TextFile.SaveAsync(path, text, loaded.Encoding);
                replaced += count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnencodableCharactersException) { failed++; }
        }
        FindDialog.ShowMessage(Loc.T("Find.ReplacedInFiles", replaced, perFile.Count, failed));
    }

    private async Task GoToHitAsync(SearchHit hit)
    {
        EditorDocument? doc = hit.DocumentKey as EditorDocument;
        if (doc is null && hit.FilePath is not null) doc = await OpenFileAsync(hit.FilePath);
        if (doc is null) return;
        Activate(doc);
        var d = doc.Document;
        // Offsets may be stale if the document changed since the search; fall back to line/column.
        int offset;
        if (hit.Line <= d.LineCount)
        {
            var line = d.GetLineByNumber(hit.Line);
            offset = Math.Min(line.Offset + hit.Column - 1, line.EndOffset);
        }
        else offset = Math.Min(hit.Offset, d.TextLength);
        Editor.Select(offset, Math.Min(hit.Length, d.TextLength - offset));
        Editor.ScrollTo(hit.Line, hit.Column);
        Editor.Focus();
    }

    private void GoToResult(int direction)
    {
        if (_results?.Current is null) return;
        if (!ReferenceEquals(ResultsHost.Child, _results) || !ResultsHost.IsVisible) ShowSearchResults();
        _results.Step(direction);
    }

    // ---------------------------------------------------------------- highlighting

    private void Highlight(SearchOptions options)
    {
        if (_searchHighlighter is null)
        {
            _searchHighlighter = new SearchHighlighter(Palette.SearchHighlight);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_searchHighlighter);
        }
        try { _searchHighlighter.Regex = options.Pattern.Length == 0 ? null : options.ToRegex(); }
        catch (ArgumentException) { _searchHighlighter.Regex = null; }
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void OnActiveDocumentChangedForSearch() => Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

    /// <summary>Marks all matches in the visible lines only, so it costs nothing for big files.</summary>
    private sealed class SearchHighlighter(IBrush brush) : IBackgroundRenderer
    {
        private IBrush _brush = brush;

        public Regex? Regex { get; set; }

        public KnownLayer Layer => KnownLayer.Selection;

        public void SetBrush(IBrush brush) => _brush = brush;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Regex is null || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            var doc = textView.Document;
            int start = textView.VisualLines[0].FirstDocumentLine.Offset;
            int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            string text = doc.GetText(start, end - start);
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            int n = 0;
            try
            {
                foreach (Match m in Regex.Matches(text))
                {
                    if (m.Length == 0) continue;
                    builder.AddSegment(textView, new TextSegment { StartOffset = start + m.Index, Length = m.Length });
                    builder.CloseFigure();
                    if (++n > 2000) break;
                }
            }
            catch (RegexMatchTimeoutException) { return; }
            if (builder.CreateGeometry() is { } g) drawingContext.DrawGeometry(_brush, null, g);
        }
    }
}
