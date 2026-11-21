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
public sealed class DiffView : Grid
{
    private readonly TextEditor _left;
    private readonly TextEditor _right;
    private readonly TextEditor _words;        // word-diff mode: one read-only text, like git diff --word-diff
    private readonly GridSplitter _splitter;
    private WordDiffResult? _wordDiff;
    private bool _wordMode;
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
        _words.TextArea.TextView.LineTransformers.Add(new WordDiffColorizer(this));

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
        // Labelled: the file's own ✕ is right above it.
        var close = new Button { Content = "✕  " + Loc.T("Diff.Close"), FontSize = 11, Padding = new Thickness(10, 0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
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
            _wordMode = value;
            _left.IsVisible = _right.IsVisible = _splitter.IsVisible = !value;
            _words.IsVisible = value;
            if (value) ComputeWords();
            UpdateSummary();
            WordModeChanged?.Invoke(value);
        }
    }

    public event Action<bool>? WordModeChanged;

    internal WordDiffResult? WordDiffResult => _wordDiff;

    public TextEditor WordsEditor => _words;

    private void ComputeWords() => ShowWords(WordDiff.Compute(_left.Document.Text, _right.Document.Text));

    private void ShowWords(WordDiffResult result)
    {
        int caret = _words.CaretOffset;
        double offset = _words.VerticalOffset;
        _wordDiff = result;
        _words.Document = ReadOnlyDocument(result.Text);
        _words.CaretOffset = Math.Min(caret, _words.Document.TextLength);
        _words.SetVerticalOffset(offset);
    }

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
            int target = direction > 0
                ? w.Changes.FirstOrDefault(c => c > caret, w.Changes[0])
                : w.Changes.LastOrDefault(c => c < caret, w.Changes[^1]);
            _words.CaretOffset = target;
            var loc = _words.Document.GetLocation(target);
            _words.ScrollTo(loc.Line, loc.Column);
            _words.Focus();
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
        MoveCaret(_right, block.NewStart);
        MoveCaret(_left, block.OldStart);
        editor.ScrollTo(Math.Min(Start(block) + 1, editor.Document.LineCount), 1);
        editor.Focus();
        InvalidateBlocks();
    }

    private static void MoveCaret(TextEditor editor, int line0)
    {
        int line = Math.Clamp(line0 + 1, 1, editor.Document.LineCount);
        editor.TextArea.Caret.Position = new TextViewPosition(line, 1);
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
            if (view._wordDiff is not { } w) return;
            int start = line.Offset, end = line.EndOffset;
            foreach (var s in w.Spans)
            {
                if (s.End <= start) continue;
                if (s.Start >= end) break;
                int a = Math.Max(s.Start, start), b = Math.Min(s.End, end);
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
