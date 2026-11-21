using DiffPlex;
using DiffPlex.Chunkers;

namespace EelDitor.Core.Diff;

public enum DiffKind { Deleted, Inserted, Modified }

/// <summary>A changed region. Line numbers are 0-based; a count of 0 means "nothing on this side".</summary>
public sealed record DiffBlock(DiffKind Kind, int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>A changed character range inside one line.</summary>
public readonly record struct InlineChange(int Line, int Start, int Length);

public sealed class DiffResult
{
    public required IReadOnlyList<DiffBlock> Blocks { get; init; }
    public required IReadOnlyList<InlineChange> OldInline { get; init; }
    public required IReadOnlyList<InlineChange> NewInline { get; init; }
    public required int OldLineCount { get; init; }
    public required int NewLineCount { get; init; }

    public bool Identical => Blocks.Count == 0;

    /// <summary>Maps a line of the new text to the corresponding line of the old text (for synchronized scrolling).</summary>
    public int MapNewToOld(int newLine) => Map(newLine, fromNew: true);

    public int MapOldToNew(int oldLine) => Map(oldLine, fromNew: false);

    /// <summary>
    /// The line on the other side that corresponds to <paramref name="line"/>: the same line for unchanged text,
    /// the paired line inside a modified block, or null when the line has no counterpart (inserted/deleted).
    /// </summary>
    public int? Counterpart(int line, bool fromNew)
    {
        foreach (var b in Blocks)
        {
            int start = fromNew ? b.NewStart : b.OldStart;
            int count = fromNew ? b.NewCount : b.OldCount;
            int otherStart = fromNew ? b.OldStart : b.NewStart;
            int otherCount = fromNew ? b.OldCount : b.NewCount;
            if (line < start) break;
            if (line < start + count)
            {
                int i = line - start;
                return i < otherCount ? otherStart + i : null;
            }
        }
        int mapped = Map(line, fromNew);
        int max = fromNew ? OldLineCount : NewLineCount;
        return mapped < max ? mapped : null;
    }

    /// <summary>The block containing <paramref name="line"/> on the given side, or null for an unchanged line.</summary>
    public DiffBlock? BlockAt(int line, bool fromNew) =>
        Blocks.FirstOrDefault(b => fromNew
            ? line >= b.NewStart && line < b.NewStart + b.NewCount
            : line >= b.OldStart && line < b.OldStart + b.OldCount);

    private int Map(int line, bool fromNew)
    {
        int delta = 0;
        foreach (var b in Blocks)
        {
            int start = fromNew ? b.NewStart : b.OldStart;
            int count = fromNew ? b.NewCount : b.OldCount;
            int otherStart = fromNew ? b.OldStart : b.NewStart;
            int otherCount = fromNew ? b.OldCount : b.NewCount;
            if (line < start) break;
            if (line < start + count)
                return otherStart + (otherCount == 0 ? 0 : Math.Min(otherCount - 1, (line - start) * otherCount / Math.Max(1, count)));
            delta = (otherStart + otherCount) - (start + count);
        }
        int max = fromNew ? OldLineCount : NewLineCount;
        return Math.Clamp(line + delta, 0, Math.Max(0, max - 1));
    }
}

public static class LineDiff
{
    /// <summary>Line diff with character-level detail for modified lines. Line terminators are ignored.</summary>
    public static DiffResult Compute(string oldText, string newText, bool ignoreWhitespace = false)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var differ = new Differ();
        var result = differ.CreateDiffs(string.Join('\n', oldLines), string.Join('\n', newLines), ignoreWhitespace, false, new LineChunker());

        var blocks = new List<DiffBlock>();
        var oldInline = new List<InlineChange>();
        var newInline = new List<InlineChange>();
        foreach (var b in result.DiffBlocks)
        {
            var kind = b.DeleteCountA == 0 ? DiffKind.Inserted : b.InsertCountB == 0 ? DiffKind.Deleted : DiffKind.Modified;
            blocks.Add(new DiffBlock(kind, b.DeleteStartA, b.DeleteCountA, b.InsertStartB, b.InsertCountB));
            if (kind != DiffKind.Modified) continue;
            int pairs = Math.Min(b.DeleteCountA, b.InsertCountB);
            for (int i = 0; i < pairs; i++)
            {
                int ol = b.DeleteStartA + i, nl = b.InsertStartB + i;
                if (oldLines[ol].Length + newLines[nl].Length > 4000) continue;
                var chars = differ.CreateDiffs(oldLines[ol], newLines[nl], false, false, new CharacterChunker());
                foreach (var c in chars.DiffBlocks)
                {
                    if (c.DeleteCountA > 0) oldInline.Add(new InlineChange(ol, c.DeleteStartA, c.DeleteCountA));
                    if (c.InsertCountB > 0) newInline.Add(new InlineChange(nl, c.InsertStartB, c.InsertCountB));
                }
            }
        }
        return new DiffResult
        {
            Blocks = blocks,
            OldInline = oldInline,
            NewInline = newInline,
            OldLineCount = oldLines.Length,
            NewLineCount = newLines.Length,
        };
    }

    public static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
