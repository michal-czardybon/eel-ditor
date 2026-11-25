using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.Core.Git;
using EelDitor.Core.Text;

namespace EelDitor.App.Views;

/// <summary>
/// Merge conflicts: blocks between &lt;&lt;&lt;&lt;&lt;&lt;&lt; and &gt;&gt;&gt;&gt;&gt;&gt;&gt; markers are highlighted;
/// a bar above the editor counts them, moves between them and resolves the one at the caret
/// (current, incoming or both). When no markers are left in a file git still reports as conflicted,
/// the bar offers to mark it resolved (git add).
/// </summary>
public partial class MainWindow
{
    private List<ConflictBlock> _conflicts = [];
    private ConflictRenderer? _conflictRenderer;
    private CancellationTokenSource? _conflictScan;

    internal IReadOnlyList<ConflictBlock> CurrentConflicts => _conflicts;

    private void SetupConflicts()
    {
        _conflictRenderer = new ConflictRenderer(this);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_conflictRenderer);
        Editor.TextChanged += (_, _) => ScheduleConflictScan();
    }

    private void ScheduleConflictScan()
    {
        _conflictScan?.Cancel();
        var cts = _conflictScan = new CancellationTokenSource();
        _ = Task.Delay(200, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) ScanConflicts(); });
        }, TaskScheduler.Default);
    }

    /// <summary>Finds the conflict blocks of the active document and updates the bar.</summary>
    internal void ScanConflicts()
    {
        var doc = Editor.Document;
        // Merge conflicts only happen in git repositories; elsewhere such lines are ordinary text.
        if (_active is null || _active.IsLarge || doc is null || !InGitRepo) _conflicts = [];
        else
        {
            var lines = new string[doc.LineCount];
            for (int i = 0; i < lines.Length; i++)
            {
                var l = doc.GetLineByNumber(i + 1);
                // Only marker lines matter; skip reading long lines in full.
                lines[i] = doc.GetText(l.Offset, Math.Min(l.Length, 200));
            }
            _conflicts = Conflicts.Find(lines);
        }
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        UpdateConflictBar();
        RefreshCommandStates();
    }

    private int CaretLine => Editor.TextArea.Caret.Line - 1;

    private ConflictBlock? ConflictAtCaret() => _conflicts.FirstOrDefault(c => c.Contains(CaretLine));

    private void UpdateConflictBar()
    {
        ConflictBarButtons.Children.Clear();
        var d = _active;
        bool gitConflict = d?.GitConflict == true;
        if (d is null || (_conflicts.Count == 0 && !gitConflict))
        {
            ConflictBar.IsVisible = false;
            return;
        }
        ConflictBar.IsVisible = true;
        void B(string key, Action a, string? tip = null)
        {
            var b = new Button { Content = Loc.T(key), Padding = new Thickness(10, 2), FontSize = 12, Margin = new Thickness(6, 1, 0, 1) };
            if (tip is not null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { a(); Editor.Focus(); };
            ConflictBarButtons.Children.Add(b);
        }
        if (_conflicts.Count == 0)
        {
            ConflictBarText.Text = Loc.T("Conflict.NoneLeft");
            B("Conflict.MarkResolved", () => _ = MarkResolvedAsync(d));
            return;
        }
        int current = _conflicts.FindIndex(c => c.Contains(CaretLine));
        ConflictBarText.Text = current >= 0
            ? Loc.T("Conflict.CountAt", _conflicts.Count, current + 1)
            : Loc.T("Conflict.Count", _conflicts.Count);
        B("Conflict.Prev", () => GoToConflict(-1), "Shift+F8");
        B("Conflict.Next", () => GoToConflict(1), "F8");
        B("Conflict.AcceptCurrent", () => ResolveConflict(ConflictChoice.Current), Loc.T("Conflict.AcceptCurrentTip"));
        B("Conflict.AcceptIncoming", () => ResolveConflict(ConflictChoice.Incoming), Loc.T("Conflict.AcceptIncomingTip"));
        B("Conflict.AcceptBoth", () => ResolveConflict(ConflictChoice.Both));
        if (gitConflict) B("Conflict.CompareSides", () => _ = CompareConflictSidesAsync(d));
    }

    private void GoToConflict(int direction)
    {
        if (_conflicts.Count == 0) return;
        int line = CaretLine;
        var target = direction > 0
            ? _conflicts.FirstOrDefault(c => c.Start > line) ?? _conflicts[0]
            : _conflicts.LastOrDefault(c => c.End < line && !c.Contains(line)) ?? _conflicts[^1];
        Editor.TextArea.Caret.Position = new AvaloniaEdit.TextViewPosition(target.Start + 1, 1);
        Editor.ScrollTo(target.Start + 1, 1);
        Editor.Focus();
        UpdateConflictBar();
    }

    /// <summary>Replaces the conflict at the caret (or the first one) with the chosen side; one undo step.</summary>
    internal void ResolveConflict(ConflictChoice choice)
    {
        if (_conflicts.Count == 0) return;
        var block = ConflictAtCaret() ?? _conflicts[0];
        var doc = Editor.Document;
        var lines = Enumerable.Range(1, doc.LineCount).Select(n => doc.GetText(doc.GetLineByNumber(n))).ToList();
        var resolved = Conflicts.Resolve(lines, block, choice);
        var first = doc.GetLineByNumber(block.Start + 1);
        var last = doc.GetLineByNumber(block.End + 1);
        string nl = last.DelimiterLength > 0 ? doc.GetText(last.EndOffset, last.DelimiterLength) : (_active?.Eol ?? EolKind.CrLf).ToNewLine();
        int start = first.Offset;
        int end = last.EndOffset + last.DelimiterLength;
        string replacement = resolved.Count == 0 ? "" : string.Join(nl, resolved) + (last.DelimiterLength > 0 ? nl : "");
        if (resolved.Count == 0 && last.DelimiterLength == 0 && first.PreviousLine is { } prev)
            start = prev.EndOffset; // removing the last lines: take the preceding line break too
        doc.Replace(start, end - start, replacement);
        Editor.CaretOffset = Math.Min(start, doc.TextLength);
        ScanConflicts();
    }

    /// <summary>Opens "ours" (:2) and "theirs" (:3) from the git index side by side.</summary>
    private async Task CompareConflictSidesAsync(EditorDocument doc)
    {
        if (doc.FilePath is null || GitService.FindRoot(doc.FilePath) is not { } root) return;
        string rel = GitService.RelativePath(root, doc.FilePath);
        string Decode(byte[]? b) => b is null ? "" : TextFile.Decode(b).Text;
        string ours = Decode(await GitService.ShowAsync(root, ":2", rel));
        string theirs = Decode(await GitService.ShowAsync(root, ":3", rel));
        Activate(doc);
        OpenDiff($"{Loc.T("Conflict.Ours")}: {rel}", DiffView.ReadOnlyDocument(ours), false,
            $"{Loc.T("Conflict.Theirs")}: {rel}", DiffView.ReadOnlyDocument(theirs), false, doc.FilePath);
    }

    private async Task MarkResolvedAsync(EditorDocument doc)
    {
        if (doc.FilePath is null || GitService.FindRoot(doc.FilePath) is not { } root) return;
        if (doc.IsDirty && !await SaveAsync(doc)) return;
        await GitService.StageAsync(root, [GitService.RelativePath(root, doc.FilePath)]); // a failure opens the git console
        await RefreshGitAsync(root);
        UpdateConflictBar();
    }

    /// <summary>Colors conflict blocks: markers, current side, base (diff3) and incoming side.</summary>
    private sealed class ConflictRenderer(MainWindow window) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            var conflicts = window._conflicts;
            if (conflicts.Count == 0 || !textView.VisualLinesValid || textView.Document is null) return;
            var p = window.Palette;
            IBrush marker = p.CurrentLine, current = p.DiffAdded, incoming = IncomingBrush(p), baseBrush = p.DiffRemoved;
            double width = Math.Max(textView.Bounds.Width, 1);
            foreach (var vl in textView.VisualLines)
            {
                int line = vl.FirstDocumentLine.LineNumber - 1;
                var c = conflicts.FirstOrDefault(b => b.Contains(line));
                if (c is null) continue;
                IBrush brush = line == c.Start || line == c.Separator || line == c.End || line == c.BaseMarker ? marker
                    : line < (c.BaseMarker ?? c.Separator) ? current
                    : line < c.Separator ? baseBrush
                    : incoming;
                double y = vl.VisualTop - textView.VerticalOffset;
                dc.FillRectangle(brush, new Rect(0, y, width, vl.Height));
            }
        }

        private static IBrush? _incomingDark, _incomingLight;

        private static IBrush IncomingBrush(EelDitor.App.Editor.EditorPalette p) => p == EelDitor.App.Editor.EditorPalette.Light
            ? _incomingLight ??= new SolidColorBrush(Color.FromArgb(0x40, 0x09, 0x69, 0xDA))
            : _incomingDark ??= new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x8E, 0xEA));
    }
}
