using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using EelDitor.App.Editor;
using EelDitor.Core.Diff;

namespace EelDitor.App.Views;

/// <summary>
/// Editing in the word view. The word view is the new (right) text with the removed runs put back; everything but the
/// removed runs is the new text itself, so it can be edited there: every edit is made in the right document at the
/// same place, and the word diff follows. The removed runs are not in the file and stay read-only.
/// <list type="bullet">
/// <item>The runs are kept as text segments of the word view, so they move with the edits until the diff is computed
/// again (after a pause in typing); when the new word view has the same text, it is not replaced (no flicker, the caret
/// stays).</item>
/// <item>Places are mapped through lines and columns of the new text, so the file's line endings (CRLF) do not matter.</item>
/// </list>
/// </summary>
public sealed partial class DiffView
{
    /// <summary>A removed or added run of the word view, following edits.</summary>
    private sealed class RunSegment : TextSegment
    {
        public WordChange Kind { get; init; }
    }

    private TextSegmentCollection<RunSegment>? _runs;
    private TextDocument? _wordsDocument;
    private bool _replacingWords;
    private (int Start, int End)? _pendingEdit;

    /// <summary>The editor the user sees: the word view, or the right (working) side.</summary>
    internal AvaloniaEdit.TextEditor VisibleEditor => _wordMode ? _words : _right;

    /// <summary>
    /// The changed lines of <see cref="VisibleEditor"/>'s document, for the document map: in the word view the lines with
    /// removed or added runs, side by side the right side's changed blocks (removed lines as a block of 0 lines).
    /// </summary>
    internal IEnumerable<(int Line, int Count, DiffKind Kind)> ChangedLines()
    {
        if (!_wordMode)
        {
            foreach (var b in _diff.Blocks) yield return (b.NewStart, b.NewCount, b.Kind);
            yield break;
        }
        if (_runs is null || _words.Document is not { } doc) yield break;
        // Per line of the word view: removed runs only, added only, or both.
        var lines = new SortedDictionary<int, (bool Removed, bool Added)>();
        foreach (var run in _runs)
        {
            if (run.Length == 0 || run.EndOffset > doc.TextLength) continue;
            int first = doc.GetLineByOffset(run.StartOffset).LineNumber - 1;
            int last = doc.GetLineByOffset(Math.Max(run.StartOffset, run.EndOffset - 1)).LineNumber - 1;
            for (int l = first; l <= last; l++)
            {
                var (removed, added) = lines.GetValueOrDefault(l);
                lines[l] = run.Kind == WordChange.Deleted ? (true, added) : (removed, true);
            }
        }
        foreach (var (line, (removed, added)) in lines)
            yield return (line, 1, removed && added ? DiffKind.Modified : removed ? DiffKind.Deleted : DiffKind.Inserted);
    }

    /// <summary>The runs of the word view (moving with edits), or null.</summary>
    private IEnumerable<RunSegment> RunsIn(int offset, int length) =>
        _runs?.FindOverlappingSegments(offset, length) ?? [];

    /// <summary>Shows a word diff, keeping the word view (and its caret) when only the marks changed.</summary>
    private void ShowWords(WordDiffResult result)
    {
        int caret = _words.CaretOffset;
        double offset = _words.VerticalOffset;
        bool sameText = _wordsDocument is not null && ReferenceEquals(_words.Document, _wordsDocument) && _wordsDocument.Text == result.Text;
        // The caret's place in the file, to find it again in the new word view.
        int? rightCaret = _wordDiff is not null && _wordsDocument is not null && !sameText ? WordsToRight(caret) : null;
        _wordDiff = result;
        _currentChange = -1;
        if (!sameText)
        {
            _replacingWords = true;
            try
            {
                if (_wordsDocument is not null) { _wordsDocument.Changing -= OnWordsChanging; _wordsDocument.Changed -= OnWordsChanged; }
                _wordsDocument = ReadOnlyDocument(result.Text);
                _wordsDocument.Changing += OnWordsChanging;
                _wordsDocument.Changed += OnWordsChanged;
                _words.Document = _wordsDocument;
            }
            finally { _replacingWords = false; }
        }
        _runs = new TextSegmentCollection<RunSegment>(_wordsDocument!);
        foreach (var s in result.Spans) _runs.Add(new RunSegment { StartOffset = s.Start, Length = s.Length, Kind = s.Kind });
        _words.IsReadOnly = !RightEditable;
        _words.TextArea.ReadOnlySectionProvider = new RemovedRunsReadOnly(this);
        if (!sameText)
        {
            _words.CaretOffset = Math.Min(rightCaret is int r ? RightToWords(r) : caret, _words.Document.TextLength);
            _words.SetVerticalOffset(offset);
        }
        _words.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// A place of the word view in the right (new) document: the same line of the new text (line breaks inside removed
    /// runs are not the new text's) and the same column, removed characters not counted.
    /// </summary>
    internal int WordsToRight(int wordsOffset)
    {
        var words = _words.Document;
        var right = _right.Document;
        wordsOffset = Math.Clamp(wordsOffset, 0, words.TextLength);
        int line = 0, lineStart = 0;
        for (int i = 0; i < wordsOffset; i++)
        {
            if (words.GetCharAt(i) != '\n' || InRemovedRun(i)) continue;
            line++;
            lineStart = i + 1;
        }
        int removed = RunsIn(lineStart, wordsOffset - lineStart).Where(s => s.Kind == WordChange.Deleted)
            .Sum(s => Math.Min(s.EndOffset, wordsOffset) - Math.Max(s.StartOffset, lineStart));
        int column = wordsOffset - lineStart - removed;
        var l = right.GetLineByNumber(Math.Clamp(line + 1, 1, right.LineCount));
        return l.Offset + Math.Clamp(column, 0, l.Length);
    }

    /// <summary>The place in the word view of a place in the right (new) document.</summary>
    internal int RightToWords(int rightOffset)
    {
        var right = _right.Document;
        var words = _words.Document;
        var location = right.GetLocation(Math.Clamp(rightOffset, 0, right.TextLength));
        int i = WordsOffsetOfNewLine(location.Line - 1), column = location.Column - 1;
        while (i < words.TextLength && (column > 0 || InRemovedRun(i)))
        {
            if (!InRemovedRun(i)) column--;
            i++;
        }
        return i;
    }

    private bool InRemovedRun(int offset) =>
        RunsIn(offset, 0).Any(s => s.Kind == WordChange.Deleted && offset >= s.StartOffset && offset < s.EndOffset);

    /// <summary>Before an edit of the word view: where it goes in the right document (offsets as they are now).</summary>
    private void OnWordsChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_replacingWords || _closed || !RightEditable) { _pendingEdit = null; return; }
        _pendingEdit = (WordsToRight(e.Offset), WordsToRight(e.Offset + e.RemovalLength));
    }

    /// <summary>After an edit of the word view: the same edit in the right document (in its line endings).</summary>
    private void OnWordsChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_pendingEdit is not { } edit) return;
        _pendingEdit = null;
        var right = _right.Document;
        var at = right.GetLocation(edit.Start);
        string nl = TextUtilities.GetNewLineFromDocument(right, at.Line);
        string inserted = e.InsertedText.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", nl);
        right.Replace(edit.Start, Math.Max(0, edit.End - edit.Start), inserted);
        _words.TextArea.TextView.Redraw();
    }

    /// <summary>The removed runs are not in the file: nothing can be typed into or deleted from them.</summary>
    private sealed class RemovedRunsReadOnly(DiffView view) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) =>
            view.RightEditable && !view.RunsIn(offset, 0).Any(s => s.Kind == WordChange.Deleted && offset > s.StartOffset && offset < s.EndOffset);

        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
        {
            if (!view.RightEditable) yield break;
            int at = segment.Offset, end = segment.EndOffset;
            foreach (var run in view.RunsIn(segment.Offset, segment.Length).Where(s => s.Kind == WordChange.Deleted).OrderBy(s => s.StartOffset))
            {
                if (run.StartOffset > at) yield return new TextSegment { StartOffset = at, EndOffset = Math.Min(run.StartOffset, end) };
                at = Math.Max(at, run.EndOffset);
            }
            if (end > at) yield return new TextSegment { StartOffset = at, EndOffset = end };
        }
    }
}
