using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.Core.Diff;
using EelDitor.Core.Text;
using EelDitor.App.Syntax;
using TextMateSharp.Grammars;

namespace EelDitor.App.Views;

/// <summary>
/// Side-by-side comparison inside the main window. Each side is either a live document (editable, the diff is
/// recomputed as you type) or read-only text (HEAD, the version on disk, a commit). Changes can be copied between
/// the sides block by block or all at once, like in WinMerge. Scrolling is synchronized through the diff's line mapping.
/// </summary>
public sealed partial class DiffView : Grid
{
    private readonly TextEditor _left;
    private readonly TextEditor _right;
    private readonly TextEditor _words;        // word-diff mode: one read-only text, like git diff --word-diff
    private readonly GridSplitter _splitter;
    private WordDiffResult? _wordDiff;
    private bool _wordMode;
    private int _currentChange = -1;     // word mode: the change F7 went to (outlined), or -1
    private readonly TextBlock _leftTitle = new() { FontSize = 12, Margin = new Thickness(8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _rightTitle = new() { FontSize = 12, Margin = new Thickness(8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _summary = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly DispatcherTimer _recompute;
    private readonly EelRegistryOptions _registry;
    private readonly TextMate.Installation? _leftTm, _rightTm;
    private DiffResult _diff;
    private bool _syncing;
    private bool? _scrollLeader;      // the side the user is scrolling (true = left)
    private DateTime _leaderUntil;
    private bool _syncScroll = true;
    private bool _closed;
    private bool _leftActive;
    private int _computeVersion;

    public DiffView(string leftTitle, TextDocument leftDocument, bool leftEditable,
        string rightTitle, TextDocument rightDocument, bool rightEditable,
        EditorPalette palette, FontFamily font, double fontSize, string? extension, ThemeName theme)
    {
        RowDefinitions = new RowDefinitions("Auto,*");
        ColumnDefinitions = new ColumnDefinitions("*,4,*");
        Palette = palette;
        LeftEditable = leftEditable;
        RightEditable = rightEditable;

        _left = CreateEditor(font, fontSize);
        _left.Document = leftDocument;
        _left.IsReadOnly = !leftEditable;
        _right = CreateEditor(font, fontSize);
        _right.Document = rightDocument;
        _right.IsReadOnly = !rightEditable;
        _words = CreateEditor(font, fontSize);
        _words.IsReadOnly = true;
        _words.IsVisible = false;
        // One text for reading (no sides to keep aligned): wrapped, so a change in a long paragraph is on screen.
        _words.WordWrap = true;
        _words.TextArea.TextView.LineTransformers.Add(new WordDiffColorizer(this));
        _words.TextArea.TextView.BackgroundRenderers.Add(new CurrentChangeRenderer(this));

        _registry = new EelRegistryOptions(theme);
        if (extension is not null && _registry.GetLanguageByExtension(extension) is { } lang)
        {
            string scope = _registry.GetScopeByLanguageId(lang.Id);
            _leftTm = _left.InstallTextMate(_registry);
            _leftTm.SetGrammar(scope);
            _rightTm = _right.InstallTextMate(_registry);
            _rightTm.SetGrammar(scope);
        }
        ApplyPalette(palette, theme);

        _diff = LineDiff.Compute(leftDocument.Text, rightDocument.Text);
        _left.TextArea.TextView.BackgroundRenderers.Add(new DiffRenderer(this, isLeft: true));
        _right.TextArea.TextView.BackgroundRenderers.Add(new DiffRenderer(this, isLeft: false));

        _leftTitle.Text = leftTitle + (leftEditable ? "" : "  " + Loc.T("Diff.ReadOnly"));
        _rightTitle.Text = rightTitle + (rightEditable ? "" : "  " + Loc.T("Diff.ReadOnly"));

        // The commands are on the main toolbar; here only the titles, the summary (right side) and ✕.
        LeftIsBase = !leftEditable && rightEditable;
        // Like the file's ✕ (which is hidden while comparing, so this one is the only ✕ there).
        var close = new Button { Content = "✕", Padding = new Thickness(10, 0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, Loc.T("Diff.Close") + " (Shift+Esc)");
        close.Click += (_, _) => CloseRequested?.Invoke();
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        _summary.FontSize = 11;
        _summary.Res(TextBlock.ForegroundProperty, "DimTextBrush");
        var rightHeader = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        rightHeader.Children.Add(close);
        DockPanel.SetDock(_summary, Dock.Right);
        _summary.MaxWidth = 320;
        rightHeader.Children.Add(_summary);
        rightHeader.Children.Add(_rightTitle);

        AddAt(Title(_leftTitle), 0, 0);
        AddAt(Title(rightHeader), 0, 2);
        AddAt(_left, 1, 0);
        _splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }.Res(BackgroundProperty, "BorderSubtleBrush");
        AddAt(_splitter, 1, 1);
        AddAt(_right, 1, 2);
        AddAt(_words, 1, 0);
        SetColumnSpan(_words, 3);

        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(fromLeft: true);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(fromLeft: false);
        _left.TextArea.GotFocus += (_, _) => { _leftActive = true; InvalidateBlocks(); };
        _right.TextArea.GotFocus += (_, _) => { _leftActive = false; InvalidateBlocks(); };
        _left.TextArea.Caret.PositionChanged += (_, _) => { if (_leftActive) InvalidateBlocks(); };
        _right.TextArea.Caret.PositionChanged += (_, _) => { if (!_leftActive) InvalidateBlocks(); };

        foreach (var e in new[] { _left, _right, _words }) AddChangeMenu(e);

        _recompute = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, async (_, _) => await RecomputeAsync());
        leftDocument.Changed += OnDocumentChanged;
        rightDocument.Changed += OnDocumentChanged;
        UpdateSummary();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => GoToChange(1), DispatcherPriority.Background);
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            leftDocument.Changed -= OnDocumentChanged;
            rightDocument.Changed -= OnDocumentChanged;
            _recompute.Stop();
            _leftTm?.Dispose();
            _rightTm?.Dispose();
            // Release the live documents so the hidden editors stop listening to them.
            _left.Document = new TextDocument();
            _right.Document = new TextDocument();
        };
    }

    /// <summary>Creates a read-only left side from plain text (HEAD, disk version, commit).</summary>
    public static TextDocument ReadOnlyDocument(string text) => new(text) { UndoStack = { SizeLimit = 0 } };

    public event Action? CloseRequested;

    /// <summary>Scroll both sides together (aligned through the diff); on by default.</summary>
    public bool SyncScroll
    {
        get => _syncScroll;
        set
        {
            if (_syncScroll == value) return;
            _syncScroll = value;
            SyncScrollChanged?.Invoke(value);
            if (value) Sync(fromLeft: _leftActive);
        }
    }

    /// <summary>
    /// Word diff (<c>git diff --word-diff</c>): one read-only text, the new version with removed words put back
    /// (struck through) in front of what replaced them. Off = the two sides next to each other.
    /// </summary>
    public bool WordMode
    {
        get => _wordMode;
        set
        {
            if (_wordMode == value) return;
            // The same place in the other mode: the current block's change, or the line the caret is on.
            int block = CurrentBlock;
            int newLine = value ? (block >= 0 ? _diff.Blocks[block].NewStart : CaretNewLine()) : NewLineOfWordsOffset(_words.CaretOffset);
            bool onChange = value && block >= 0;
            _wordMode = value;
            _left.IsVisible = _right.IsVisible = _splitter.IsVisible = !value;
            _words.IsVisible = value;
            if (value) ComputeWords();
            UpdateSummary();
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || _wordMode != value) return;
                if (value) ShowWordsAt(newLine, onChange);
                else ShowLinesAt(newLine);
            }, DispatcherPriority.Background);
            WordModeChanged?.Invoke(value);
        }
    }

    public event Action<bool>? WordModeChanged;

    internal WordDiffResult? WordDiffResult => _wordDiff;

    public TextEditor WordsEditor => _words;

    /// <summary>The caret's line of the focused side, as a line of the new (right) text (0-based).</summary>
    private int CaretNewLine() => _leftActive
        ? _diff.MapOldToNew(_left.TextArea.Caret.Line - 1)
        : _right.TextArea.Caret.Line - 1;

    /// <summary>Word mode at a line of the new text: on its change (outlined) when it is in one.</summary>
    private void ShowWordsAt(int newLine, bool onChange)
    {
        if (_wordDiff is not { } w) return;
        int offset = WordsOffsetOfNewLine(newLine);
        _currentChange = -1;
        if (onChange && w.Changes.Count > 0)
        {
            int i = Enumerable.Range(0, w.Changes.Count).FirstOrDefault(k => w.Changes[k] >= offset, w.Changes.Count - 1);
            _currentChange = i;
            offset = w.Changes[i];
        }
        _words.CaretOffset = Math.Min(offset, _words.Document.TextLength);
        Reveal(_words);
        _words.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>The sides at a line of the new text (and its counterpart on the left).</summary>
    private void ShowLinesAt(int newLine)
    {
        MoveCaret(_right, newLine);
        MoveCaret(_left, _diff.MapNewToOld(newLine));
        Reveal(_leftActive ? _left : _right);
        InvalidateBlocks();
    }

    /// <summary>
    /// Where a line of the new text starts in the word view: the word view is the new text with the removed runs put
    /// back, so its line breaks outside removed runs are the new text's.
    /// </summary>
    internal int WordsOffsetOfNewLine(int newLine)
    {
        if (_wordDiff is not { } w || newLine <= 0) return 0;
        int line = 0;
        foreach (int i in NewLineBreaks(w, w.Text.Length))
            if (++line == newLine) return i + 1;
        return w.Text.Length;
    }

    /// <summary>The line of the new text an offset of the word view is on (0-based).</summary>
    internal int NewLineOfWordsOffset(int offset) => _wordDiff is { } w ? NewLineBreaks(w, offset).Count() : 0;

    /// <summary>The word view's line breaks before <paramref name="end"/> that are the new text's (not in removed runs).</summary>
    private static IEnumerable<int> NewLineBreaks(WordDiffResult w, int end)
    {
        int span = 0;
        for (int i = 0; i < Math.Min(end, w.Text.Length); i++)
        {
            if (w.Text[i] != '\n') continue;
            while (span < w.Spans.Count && w.Spans[span].End <= i) span++;
            if (span < w.Spans.Count && w.Spans[span].Start <= i && w.Spans[span].Kind == WordChange.Deleted) continue;
            yield return i;
        }
    }

    /// <summary>
    /// Brings the caret's place into view: the line to the middle when it is off screen, and the column (a change far
    /// to the right in a long line) horizontally.
    /// </summary>
    private static void Reveal(TextEditor editor)
    {
        var tv = editor.TextArea.TextView;
        editor.UpdateLayout();
        if (tv.Bounds.Height <= 0 || editor.Document is null) return;
        var p = tv.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineMiddle);
        double height = tv.Bounds.Height, width = tv.Bounds.Width;
        if (p.Y < tv.VerticalOffset + tv.DefaultLineHeight || p.Y > tv.VerticalOffset + height - tv.DefaultLineHeight)
            editor.SetVerticalOffset(p.Y - height / 2);
        if (!editor.WordWrap && (p.X < tv.HorizontalOffset || p.X > tv.HorizontalOffset + width - 4 * tv.WideSpaceWidth))
            editor.SetHorizontalOffset(Math.Max(0, p.X - width / 3));
    }

    private void ComputeWords() => ShowWords(WordDiff.Compute(_left.Document.Text, _right.Document.Text));

    /// <summary>Left is a base version (HEAD, disk, commit) and right the working copy: copying right = reverting.</summary>
    public bool LeftIsBase { get; }

    public event Action<bool>? SyncScrollChanged;

    public TextEditor LeftEditor => _left;

    public TextEditor RightEditor => _right;

    public bool LeftEditable { get; }

    public bool RightEditable { get; }

    public EditorPalette Palette { get; private set; }

    internal DiffResult Diff => _diff;

    /// <summary>The block the caret of the focused side is in (or next to), or -1.</summary>
    internal int CurrentBlock
    {
        get
        {
            var editor = _leftActive ? _left : _right;
            int line = editor.TextArea.Caret.Line - 1;
            int lastLine = editor.Document.LineCount - 1;
            for (int i = 0; i < _diff.Blocks.Count; i++)
            {
                var b = _diff.Blocks[i];
                int start = _leftActive ? b.OldStart : b.NewStart;
                int count = _leftActive ? b.OldCount : b.NewCount;
                // Lines missing after the end of this side: the caret cannot go past the last line.
                if (count == 0) start = Math.Min(start, lastLine);
                if (line >= start && line < start + Math.Max(1, count)) return i;
            }
            return -1;
        }
    }

    /// <summary>Re-colors both sides after a theme change.</summary>
    public void ApplyPalette(EditorPalette palette, ThemeName theme)
    {
        Palette = palette;
        var tmTheme = _registry.LoadTheme(theme);
        _leftTm?.SetTheme(tmTheme);
        _rightTm?.SetTheme(tmTheme);
        foreach (var e in new[] { _left, _right, _words })
        {
            // Set after TextMate, which applies the theme's own editor colors.
            e.Background = palette.Background;
            e.Foreground = palette.Foreground;
            e.LineNumbersForeground = palette.LineNumbers;
            e.TextArea.SelectionBrush = palette.Selection;
            e.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _recompute.Stop();
        _recompute.Start();
    }

    internal Task RefreshSnapshotsAsync(string leftText, string? rightText)
    {
        if (_left.Document.Text == leftText && (rightText is null || _right.Document.Text == rightText))
            return Task.CompletedTask;
        if (_left.Document.Text != leftText) _left.Document.Text = leftText;
        if (rightText is not null && _right.Document.Text != rightText) _right.Document.Text = rightText;
        return RecomputeAsync();
    }

    private async Task RecomputeAsync()
    {
        _recompute.Stop();
        if (_closed) return;
        int version = ++_computeVersion;
        string left = _left.Document.Text, right = _right.Document.Text;
        bool words = _wordMode;
        var (diff, wordDiff) = await Task.Run(() => (LineDiff.Compute(left, right), words ? WordDiff.Compute(left, right) : null));
        if (_closed || version != _computeVersion) return;
        _diff = diff;
        if (wordDiff is not null && _wordMode) ShowWords(wordDiff);
        InvalidateBlocks();
        UpdateSummary();
    }

    /// <summary>Recomputes synchronously; used right after copying a block so the next copy sees fresh blocks.</summary>
    private void RecomputeNow()
    {
        _recompute.Stop();
        _computeVersion++;
        _diff = LineDiff.Compute(_left.Document.Text, _right.Document.Text);
        if (_wordMode) ComputeWords();
        InvalidateBlocks();
        UpdateSummary();
    }

    private void InvalidateBlocks()
    {
        _left.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _right.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        if (!_closed) CurrentLineChanged?.Invoke();
    }

    /// <summary>Raised when the caret line, the focused side or the diff changes (drives the bottom line panel).</summary>
    public event Action? CurrentLineChanged;

    /// <summary>The caret line of the focused side and its counterpart on the other side (0-based; null = no counterpart).</summary>
    public (int? Left, int? Right) CurrentLinePair()
    {
        if (_leftActive)
        {
            int l = _left.TextArea.Caret.Line - 1;
            return (l, _diff.Counterpart(l, fromNew: false));
        }
        int r = _right.TextArea.Caret.Line - 1;
        return (_diff.Counterpart(r, fromNew: true), r);
    }

    public void SetFontSize(double size)
    {
        _left.FontSize = size;
        _right.FontSize = size;
        _words.FontSize = size;
    }

    private void UpdateSummary()
    {
        if (_wordMode && _wordDiff is { } w)
        {
            _summary.Text = w.Spans.Count > 0 ? Loc.T("Diff.WordSummary", w.Changes.Count, w.DeletedWords, w.InsertedWords)
                : _diff.Identical ? Loc.T("Diff.Identical") : Loc.T("Diff.LayoutOnly");
            return;
        }
        _summary.Text = _diff.Identical
            ? Loc.T("Diff.Identical")
            : Loc.T("Diff.Summary", _diff.Blocks.Count,
                _diff.Blocks.Sum(b => b.NewCount), _diff.Blocks.Sum(b => b.OldCount));
    }

    public void GoToChange(int direction)
    {
        if (_closed) return;
        if (_wordMode)
        {
            if (_wordDiff is not { Changes.Count: > 0 } w) return;
            int caret = _words.CaretOffset;
            int index = direction > 0
                ? Enumerable.Range(0, w.Changes.Count).FirstOrDefault(i => w.Changes[i] > caret, 0)
                : Enumerable.Range(0, w.Changes.Count).LastOrDefault(i => w.Changes[i] < caret, w.Changes.Count - 1);
            _currentChange = index;
            _words.CaretOffset = w.Changes[index];
            _words.Focus();
            Reveal(_words);
            _words.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return;
        }
        if (_diff.Blocks.Count == 0) return;
        var editor = _leftActive ? _left : _right;
        int Start(DiffBlock b) => _leftActive ? b.OldStart : b.NewStart;
        int Count(DiffBlock b) => _leftActive ? b.OldCount : b.NewCount;
        int caretLine = editor.TextArea.Caret.Line - 1;
        int current = CurrentBlock;
        var blocks = _diff.Blocks.ToList();
        int idx;
        if (direction > 0)
        {
            idx = current >= 0 ? current + 1 : blocks.FindIndex(b => Start(b) >= caretLine);
            if (idx < 0 || idx >= blocks.Count) idx = 0;
        }
        else
        {
            idx = current >= 0 ? current - 1 : blocks.FindLastIndex(b => Start(b) + Math.Max(1, Count(b)) - 1 < caretLine);
            if (idx < 0) idx = blocks.Count - 1;
        }
        var block = blocks[idx];
        // On the first changed character of the block (in a long line it may be far to the right).
        int column = ChangedColumn(block);
        MoveCaret(_right, block.NewStart, FirstChange(_diff.NewInline, block.NewStart, block.NewCount, column));
        MoveCaret(_left, block.OldStart, FirstChange(_diff.OldInline, block.OldStart, block.OldCount, column));
        editor.Focus();
        Reveal(editor);
        InvalidateBlocks();
    }

    private static void MoveCaret(TextEditor editor, int line0, int column0 = 0)
    {
        int line = Math.Clamp(line0 + 1, 1, editor.Document.LineCount);
        int length = editor.Document.GetLineByNumber(line).Length;
        editor.TextArea.Caret.Position = new TextViewPosition(line, Math.Clamp(column0, 0, length) + 1);
    }

    /// <summary>
    /// The column of the first changed character on the block's first line: from the character diff, or (very long
    /// lines have none) <paramref name="fallback"/>.
    /// </summary>
    private static int FirstChange(IReadOnlyList<InlineChange> inline, int start, int count, int fallback) =>
        count == 0 ? 0 : inline.Where(c => c.Line == start).Select(c => c.Start).DefaultIfEmpty(fallback).Min();

    /// <summary>Where the first lines of a changed block start to differ (their common beginning), or 0.</summary>
    private int ChangedColumn(DiffBlock block)
    {
        if (block.OldCount == 0 || block.NewCount == 0 || block.OldStart >= _left.Document.LineCount || block.NewStart >= _right.Document.LineCount)
            return 0;
        string a = _left.Document.GetText(_left.Document.GetLineByNumber(block.OldStart + 1));
        string b = _right.Document.GetText(_right.Document.GetLineByNumber(block.NewStart + 1));
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>Copies the current block to the other side (WinMerge "copy to right/left").</summary>
    public void CopyBlock(bool toRight)
    {
        if (_closed || (toRight ? !RightEditable : !LeftEditable)) return;
        int idx = CurrentBlock;
        if (idx < 0) return;
        var b = _diff.Blocks[idx];
        if (toRight) ReplaceLines(_left.Document, b.OldStart, b.OldCount, _right.Document, b.NewStart, b.NewCount);
        else ReplaceLines(_right.Document, b.NewStart, b.NewCount, _left.Document, b.OldStart, b.OldCount);
        RecomputeNow();
        // Stay where the block was; F7 / Alt+↓ moves on to the next one.
        var editor = _leftActive ? _left : _right;
        MoveCaret(editor, _leftActive ? b.OldStart : b.NewStart);
    }

    /// <summary>Where change <paramref name="index"/> of the word view starts and ends (its touching runs together).</summary>
    private (int Start, int End) ChangeExtent(WordDiffResult w, int index)
    {
        int start = w.Changes[index], end = start;
        foreach (var span in w.Spans)
        {
            if (span.End <= start) continue;
            if (span.Start > end) break;
            end = Math.Max(end, span.End);
        }
        return (start, end);
    }

    /// <summary>The word view's change at the caret (or the one F7 went to, when the caret is still there), or -1.</summary>
    internal int CurrentWordChange
    {
        get
        {
            if (!_wordMode || _wordDiff is not { } w) return -1;
            int caret = _words.CaretOffset;
            if (_currentChange >= 0 && _currentChange < w.Changes.Count)
            {
                var (s, e) = ChangeExtent(w, _currentChange);
                if (caret >= s && caret <= e) return _currentChange;
            }
            for (int i = 0; i < w.Changes.Count; i++)
            {
                var (s, e) = ChangeExtent(w, i);
                if (caret >= s && caret <= e) return i;
                if (s > caret) break;
            }
            return -1;
        }
    }

    /// <summary>
    /// "Revert change": the current block (side by side) or the change at the caret (word view) becomes as in the
    /// left, base version. In the word view only that change goes back, exactly as it was (whitespace too), not its
    /// whole line or paragraph. False when there is nothing to revert there.
    /// </summary>
    public bool RevertCurrentChange()
    {
        if (_closed || !RightEditable) return false;
        if (!_wordMode)
        {
            if (CurrentBlock < 0) return false;
            CopyBlock(toRight: true);
            return true;
        }
        if (_wordDiff is not { } w || CurrentWordChange is not (>= 0 and var index) || index >= w.Reverts.Count) return false;
        var r = w.Reverts[index];
        var doc = _right.Document;
        if (r.Lines is { } b) ReplaceLines(_left.Document, b.OldStart, b.OldCount, doc, b.NewStart, b.NewCount);
        else
        {
            int Offset(int line, int column)
            {
                var l = doc.GetLineByNumber(Math.Clamp(line + 1, 1, doc.LineCount));
                return l.Offset + Math.Min(column, l.Length);
            }
            int start = Offset(r.Line, r.Column), end = Offset(r.EndLine, r.EndColumn);
            string nl = TextUtilities.GetNewLineFromDocument(doc, Math.Clamp(r.Line + 1, 1, doc.LineCount));
            doc.Replace(start, Math.Max(0, end - start), r.OldText.Replace("\n", nl));
        }
        int at = w.Changes[index];
        RecomputeNow();
        // Stay where the change was; F7 moves on to the next one.
        _currentChange = -1;
        _words.CaretOffset = Math.Min(at, _words.Document.TextLength);
        _words.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        return true;
    }

    /// <summary>A right click moves the caret to the clicked place first, so the menu acts on the change there.</summary>
    private void AddChangeMenu(TextEditor editor)
    {
        editor.TextArea.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(editor.TextArea).Properties.IsRightButtonPressed) return;
            if (editor.GetPositionFromPoint(e.GetPosition(editor)) is not { } pos) return;
            int offset = editor.Document.GetOffset(pos.Location);
            var sel = editor.TextArea.Selection;
            if (sel.IsEmpty || !sel.Contains(offset)) editor.TextArea.Caret.Position = pos;
            if (ReferenceEquals(editor, _left)) _leftActive = true;
            else if (ReferenceEquals(editor, _right)) _leftActive = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var revert = new MenuItem { Header = Loc.T("Diff.RevertThisChange") };
        ToolTip.SetTip(revert, Loc.T("Diff.RevertChangeTip"));
        revert.Click += (_, _) => RevertCurrentChange();
        var copy = new MenuItem { Header = Loc.T("Menu.Edit.Copy") };
        copy.Click += (_, _) => editor.Copy();
        var menu = new ContextMenu { Items = { revert, new Separator(), copy } };
        void Update()
        {
            revert.IsEnabled = LeftIsBase && RightEditable && (_wordMode ? CurrentWordChange >= 0 : CurrentBlock >= 0);
            copy.IsEnabled = editor.SelectionLength > 0;
        }
        menu.Opening += (_, _) => Update();
        menu.Opened += (_, _) => Update();
        editor.ContextMenu = menu;
    }

    /// <summary>The side Undo/Redo act on: the focused editable side, else the editable one.</summary>
    private TextEditor? UndoTarget =>
        _leftActive && LeftEditable ? _left : RightEditable ? _right : LeftEditable ? _left : null;

    public bool Undo()
    {
        if (_closed || UndoTarget is not { } e || !e.CanUndo) return false;
        e.Undo();
        RecomputeNow();
        return true;
    }

    public bool Redo()
    {
        if (_closed || UndoTarget is not { } e || !e.CanRedo) return false;
        e.Redo();
        RecomputeNow();
        return true;
    }

    /// <summary>Makes one side identical to the other (one undo step).</summary>
    public void CopyAll(bool toRight)
    {
        if (_closed || (toRight ? !RightEditable : !LeftEditable)) return;
        var src = toRight ? _left.Document : _right.Document;
        var dst = toRight ? _right.Document : _left.Document;
        // Keep the target's line endings.
        var eol = EolStats.Analyze(dst.Text).Dominant(EolStats.Analyze(src.Text).Dominant(EolConverter.PlatformDefault));
        dst.Replace(0, dst.TextLength, EolConverter.Convert(src.Text, eol));
        RecomputeNow();
    }

    /// <summary>Replaces lines [dStart, dStart+dCount) of <paramref name="dst"/> with lines [sStart, sStart+sCount) of <paramref name="src"/>.</summary>
    internal static void ReplaceLines(TextDocument src, int sStart, int sCount, TextDocument dst, int dStart, int dCount)
    {
        string nl = dst.LineCount > 1
            ? TextUtilities.GetNewLineFromDocument(dst, Math.Clamp(dStart + 1, 1, dst.LineCount - 1))
            : TextUtilities.GetNewLineFromDocument(src, 1);
        string text = sCount == 0 ? "" : string.Join(nl, Enumerable.Range(sStart, sCount)
            .Select(i => src.GetText(src.GetLineByNumber(i + 1))));
        if (dCount == 0)
        {
            if (sCount == 0) return;
            if (dst.TextLength == 0) dst.Insert(0, text);
            else if (dStart < dst.LineCount) dst.Insert(dst.GetLineByNumber(dStart + 1).Offset, text + nl);
            else dst.Insert(dst.TextLength, nl + text);
            return;
        }
        var first = dst.GetLineByNumber(dStart + 1);
        var last = dst.GetLineByNumber(Math.Min(dStart + dCount, dst.LineCount));
        if (sCount == 0)
        {
            // Remove the lines including their line break (or the preceding one at the end of the file).
            int end = last.DelimiterLength > 0 ? last.EndOffset + last.DelimiterLength : last.EndOffset;
            int start = last.DelimiterLength == 0 && first.PreviousLine is { } prev ? prev.EndOffset : first.Offset;
            dst.Remove(start, end - start);
        }
        else dst.Replace(first.Offset, last.EndOffset - first.Offset, text);
    }

    private void Sync(bool fromLeft)
    {
        if (_syncing || _closed || !SyncScroll || _left.Document is null || _right.Document is null) return;
        // The other side scrolls asynchronously and reports it after we return; without this it would
        // "synchronize" the side being scrolled back to its old position.
        var now = DateTime.UtcNow;
        if (_scrollLeader is { } leader && leader != fromLeft && now < _leaderUntil) return;
        _scrollLeader = fromLeft;
        _leaderUntil = now.AddMilliseconds(250);
        _syncing = true;
        try
        {
            var src = fromLeft ? _left : _right;
            var dst = fromLeft ? _right : _left;
            var tv = src.TextArea.TextView;
            dst.SetHorizontalOffset(src.HorizontalOffset);
            var topLine = tv.GetDocumentLineByVisualTop(tv.VerticalOffset);
            if (topLine is null) return;
            double within = tv.VerticalOffset - tv.GetVisualTopByDocumentLine(topLine.LineNumber);
            int mapped = fromLeft ? _diff.MapOldToNew(topLine.LineNumber - 1) : _diff.MapNewToOld(topLine.LineNumber - 1);
            mapped = Math.Clamp(mapped + 1, 1, dst.Document.LineCount);
            var dtv = dst.TextArea.TextView;
            dst.SetVerticalOffset(dtv.GetVisualTopByDocumentLine(mapped) + within);
        }
        finally { _syncing = false; }
    }

    private static TextEditor CreateEditor(FontFamily font, double size)
    {
        var e = new TextEditor
        {
            FontFamily = font,
            FontSize = size,
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
        };
        e.Options.EnableHyperlinks = false;
        e.Options.EnableRectangularSelection = true;
        e.Options.HighlightCurrentLine = false;
        e.AttachedToVisualTree += (_, _) => MainWindow.ApplyLineNumberFont(e);
        return e;
    }

    private static Border Title(Control content) =>
        new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 2), MinHeight = 26 }
            .Res(Border.BackgroundProperty, "DiffHeaderBrush")
            .Res(Border.BorderBrushProperty, "DiffHeaderBorderBrush");

    private void AddAt(Control c, int row, int col)
    {
        SetRow(c, row);
        SetColumn(c, col);
        Children.Add(c);
    }

    /// <summary>Word-diff mode: removed words struck through on red, added words on green.</summary>
    private sealed class WordDiffColorizer(DiffView view) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (view._wordDiff is null) return;
            int start = line.Offset, end = line.EndOffset;
            // The runs as text segments: they follow edits made in the word view until the diff is computed again.
            foreach (var s in view.RunsIn(start, line.Length))
            {
                int a = Math.Max(s.StartOffset, start), b = Math.Min(s.EndOffset, end);
                if (b <= a) continue;
                bool deleted = s.Kind == WordChange.Deleted;
                ChangeLinePart(a, b, el =>
                {
                    el.BackgroundBrush = deleted ? view.Palette.DiffRemovedInline : view.Palette.DiffAddedInline;
                    if (deleted) el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                });
            }
        }
    }

    /// <summary>Word mode: an outline around the change F7 / Shift+F7 went to.</summary>
    private sealed class CurrentChangeRenderer(DiffView view) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!view._wordMode || view._wordDiff is not { } w || view._currentChange < 0 || view._currentChange >= w.Changes.Count
                || !textView.VisualLinesValid || textView.Document is null) return;
            var (start, end) = view.ChangeExtent(w, view._currentChange);
            end = Math.Min(end, textView.Document.TextLength);
            if (end <= start) return;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, EndOffset = end });
            if (builder.CreateGeometry() is { } g) dc.DrawGeometry(null, new Pen(view.Palette.Accent, 1.5), g);
        }
    }

    /// <summary>Paints changed lines, character-level changes and an outline around the current block.</summary>
    private sealed class DiffRenderer(DiffView view, bool isLeft) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid || textView.VisualLines.Count == 0 || textView.Document is null) return;
            var diff = view.Diff;
            var p = view.Palette;
            int firstVisible = textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
            int lastVisible = textView.VisualLines[^1].LastDocumentLine.LineNumber - 1;
            double width = Math.Max(textView.Bounds.Width, 1);
            int current = view.CurrentBlock;
            var outline = new Pen(p.Accent, 1.5);

            for (int bi = 0; bi < diff.Blocks.Count; bi++)
            {
                var b = diff.Blocks[bi];
                int start = isLeft ? b.OldStart : b.NewStart;
                int count = isLeft ? b.OldCount : b.NewCount;
                if (start > lastVisible + 1 || start + count < firstVisible) continue;
                IBrush brush = b.Kind switch
                {
                    DiffKind.Modified => p.DiffChanged,
                    _ => isLeft ? p.DiffRemoved : p.DiffAdded,
                };
                if (count == 0)
                {
                    // Nothing on this side: draw a marker line where the other side's lines would be.
                    double y = LineTop(textView, start) - textView.VerticalOffset;
                    dc.FillRectangle(isLeft ? p.DiffAdded : p.DiffRemoved, new Rect(0, y - 1, width, 2));
                    if (bi == current) dc.DrawRectangle(null, outline, new Rect(0.75, y - 3, width - 1.5, 6));
                    continue;
                }
                double top = double.NaN, bottom = 0;
                for (int l = Math.Max(start, firstVisible); l < start + count && l <= lastVisible; l++)
                {
                    if (l + 1 > textView.Document.LineCount) break;
                    var vl = textView.GetVisualLine(l + 1);
                    if (vl is null) continue;
                    double y = vl.VisualTop - textView.VerticalOffset;
                    dc.FillRectangle(brush, new Rect(0, y, width, vl.Height));
                    if (double.IsNaN(top)) top = y;
                    bottom = y + vl.Height;
                }
                if (bi == current && !double.IsNaN(top))
                    dc.DrawRectangle(null, outline, new Rect(0.75, top + 0.75, width - 1.5, bottom - top - 1.5));
            }

            var inline = isLeft ? diff.OldInline : diff.NewInline;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 1 };
            bool any = false;
            foreach (var c in inline)
            {
                if (c.Line < firstVisible || c.Line > lastVisible || c.Line + 1 > textView.Document.LineCount) continue;
                var line = textView.Document.GetLineByNumber(c.Line + 1);
                int s = Math.Min(line.Offset + c.Start, line.EndOffset);
                int e = Math.Min(s + c.Length, line.EndOffset);
                if (e <= s) continue;
                builder.AddSegment(textView, new TextSegment { StartOffset = s, EndOffset = e });
                builder.CloseFigure();
                any = true;
            }
            if (any && builder.CreateGeometry() is { } g)
                dc.DrawGeometry(isLeft ? p.DiffRemovedInline : p.DiffAddedInline, null, g);
        }

        private static double LineTop(TextView tv, int line0)
        {
            int n = Math.Clamp(line0 + 1, 1, tv.Document.LineCount);
            double top = tv.GetVisualTopByDocumentLine(n);
            return line0 + 1 > tv.Document.LineCount ? top + tv.DefaultLineHeight : top;
        }
    }
}
