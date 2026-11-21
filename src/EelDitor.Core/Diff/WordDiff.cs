using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;

namespace EelDitor.Core.Diff;

public enum WordChange { Deleted, Inserted }

/// <summary>A deleted or inserted run in <see cref="WordDiffResult.Text"/>.</summary>
public readonly record struct WordSpan(int Start, int Length, WordChange Kind)
{
    public int End => Start + Length;
}

/// <summary>One text with the old and the new words of every change side by side, like <c>git diff --word-diff</c>.</summary>
public sealed class WordDiffResult
{
    public required string Text { get; init; }
    public required IReadOnlyList<WordSpan> Spans { get; init; }

    /// <summary>Start offsets of the changes (adjacent deleted + inserted runs count as one), for F7 navigation.</summary>
    public required IReadOnlyList<int> Changes { get; init; }

    public int DeletedWords { get; init; }
    public int InsertedWords { get; init; }
}

/// <summary>
/// Word diff in the manner of <c>git diff --word-diff</c>: a word is a run of non-whitespace characters.
/// The texts are first compared line by line; only changed blocks are compared word by word, so unchanged
/// parts of large files cost nothing. The result is the new text with the removed words put back in
/// front of what replaced them, marked as deleted.
/// </summary>
public static class WordDiff
{
    /// <summary>Changed blocks larger than this are shown whole (old lines deleted, new inserted).</summary>
    public const int MaxBlockLength = 200_000;

    public static WordDiffResult Compute(string oldText, string newText)
    {
        var oldLines = LineDiff.SplitLines(oldText);
        var newLines = LineDiff.SplitLines(newText);
        var lines = LineDiff.Compute(oldText, newText);
        var sb = new StringBuilder(newText.Length + 256);
        var spans = new List<WordSpan>();
        var changes = new List<int>();
        int deletedWords = 0, insertedWords = 0;

        void AppendLines(string[] source, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                sb.Append(source[i]);
                if (i < source.Length - 1) sb.Append('\n');
            }
        }

        void Mark(string text, WordChange kind)
        {
            if (text.Length == 0) return;
            if (!TouchesPrevious()) changes.Add(sb.Length);
            spans.Add(new WordSpan(sb.Length, text.Length, kind));
            sb.Append(text);
            int words = CountWords(text);
            if (kind == WordChange.Deleted) deletedWords += words; else insertedWords += words;
        }

        bool TouchesPrevious() => spans.Count > 0 && spans[^1].End == sb.Length;

        int newLine = 0;
        foreach (var b in lines.Blocks)
        {
            AppendLines(newLines, newLine, b.NewStart - newLine);
            newLine = b.NewStart + b.NewCount;
            bool lastNew = newLine >= newLines.Length;

            string oldBlock = string.Join('\n', oldLines, b.OldStart, b.OldCount);
            string newBlock = string.Join('\n', newLines, b.NewStart, b.NewCount);
            if (b.Kind == DiffKind.Inserted)
            {
                Mark(newBlock + (lastNew ? "" : "\n"), WordChange.Inserted);
                continue;
            }
            if (b.Kind == DiffKind.Deleted)
            {
                // At the end of the new text the line break goes before the removed lines.
                if (b.NewStart >= newLines.Length) { sb.Append('\n'); Mark(oldBlock, WordChange.Deleted); }
                else Mark(oldBlock + "\n", WordChange.Deleted);
                continue;
            }
            if (oldBlock.Length + newBlock.Length > MaxBlockLength)
            {
                Mark(oldBlock + "\n", WordChange.Deleted);
                Mark(newBlock, WordChange.Inserted);
            }
            else AppendWordDiff(oldBlock, newBlock, Mark, s => sb.Append(s));
            if (!lastNew) sb.Append('\n');
        }
        AppendLines(newLines, newLine, newLines.Length - newLine);

        return new WordDiffResult
        {
            Text = sb.ToString(),
            Spans = spans,
            Changes = changes,
            DeletedWords = deletedWords,
            InsertedWords = insertedWords,
        };
    }

    private static void AppendWordDiff(string oldBlock, string newBlock, Action<string, WordChange> mark, Action<string> plain)
    {
        var chunker = new WhitespaceChunker();
        var oldTokens = chunker.Chunk(oldBlock);
        var newTokens = chunker.Chunk(newBlock);
        var diff = new Differ().CreateDiffs(oldBlock, newBlock, false, false, chunker);
        int ni = 0;
        foreach (var d in diff.DiffBlocks)
        {
            for (; ni < d.InsertStartB; ni++) plain(newTokens[ni]);
            // Removed words; whitespace around them is not marked (git does the same).
            string removed = string.Concat(oldTokens.Skip(d.DeleteStartA).Take(d.DeleteCountA));
            string added = string.Concat(newTokens.Skip(d.InsertStartB).Take(d.InsertCountB));
            EmitTrimmed(removed, WordChange.Deleted, mark, plain, keepWhitespace: false);
            EmitTrimmed(added, WordChange.Inserted, mark, plain, keepWhitespace: true);
            ni = d.InsertStartB + d.InsertCountB;
        }
        for (; ni < newTokens.Count; ni++) plain(newTokens[ni]);
    }

    /// <summary>Marks the text without its leading/trailing whitespace; the new text's whitespace is kept unmarked.</summary>
    private static void EmitTrimmed(string text, WordChange kind, Action<string, WordChange> mark, Action<string> plain, bool keepWhitespace)
    {
        int s = 0, e = text.Length;
        while (s < e && char.IsWhiteSpace(text[s])) s++;
        while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
        if (keepWhitespace && s > 0) plain(text[..s]);
        if (e > s) mark(text[s..e], kind);
        if (keepWhitespace && e < text.Length && e >= s) plain(text[e..]);
    }

    private static int CountWords(string text)
    {
        int n = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool w = !char.IsWhiteSpace(c);
            if (w && !inWord) n++;
            inWord = w;
        }
        return n;
    }

    /// <summary>Splits into runs of non-whitespace (words) and runs of whitespace, keeping everything.</summary>
    private sealed class WhitespaceChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                bool ws = char.IsWhiteSpace(text[i]);
                int j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]) == ws) j++;
                parts.Add(text[i..j]);
                i = j;
            }
            return parts;
        }
    }
}
