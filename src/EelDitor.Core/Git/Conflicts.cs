namespace EelDitor.Core.Git;

/// <summary>
/// One merge conflict: line numbers (0-based) of the markers. <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; current</c>,
/// optional <c>||||||| base</c> (diff3 style), <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; incoming</c>.
/// </summary>
public sealed record ConflictBlock(int Start, int? BaseMarker, int Separator, int End, string CurrentLabel, string IncomingLabel)
{
    /// <summary>Lines of the current ("ours") side: between the start marker and the base or separator marker.</summary>
    public (int First, int Count) Current => (Start + 1, (BaseMarker ?? Separator) - Start - 1);

    public (int First, int Count) Base => BaseMarker is { } b ? (b + 1, Separator - b - 1) : (Separator, 0);

    /// <summary>Lines of the incoming ("theirs") side.</summary>
    public (int First, int Count) Incoming => (Separator + 1, End - Separator - 1);

    public bool Contains(int line) => line >= Start && line <= End;
}

public enum ConflictChoice { Current, Incoming, Both, Base }

public static class Conflicts
{
    private const int MarkerLength = 7;

    private static bool IsMarker(string line, char c) =>
        line.Length >= MarkerLength && line.AsSpan(0, MarkerLength).IndexOfAnyExcept(c) < 0
        && (line.Length == MarkerLength || line[MarkerLength] == ' ' || (c == '=' && line.AsSpan(MarkerLength).Trim().Length == 0));

    private static string Label(string line) => line.Length > MarkerLength ? line[(MarkerLength + 1)..].Trim() : "";

    /// <summary>Finds complete conflict blocks; incomplete or nested markers are ignored.</summary>
    public static List<ConflictBlock> Find(IReadOnlyList<string> lines)
    {
        var result = new List<ConflictBlock>();
        int i = 0;
        while (i < lines.Count)
        {
            if (!IsMarker(lines[i], '<')) { i++; continue; }
            int start = i;
            int? baseMarker = null, separator = null, end = null;
            int j = i + 1;
            for (; j < lines.Count; j++)
            {
                string l = lines[j];
                if (IsMarker(l, '<')) break; // a new conflict starts: this one is broken
                if (separator is null && baseMarker is null && IsMarker(l, '|')) baseMarker = j;
                else if (separator is null && IsMarker(l, '=')) separator = j;
                else if (separator is not null && IsMarker(l, '>')) { end = j; break; }
            }
            if (separator is { } sep && end is { } e)
            {
                result.Add(new ConflictBlock(start, baseMarker, sep, e, Label(lines[start]), Label(lines[e])));
                i = e + 1;
            }
            else i = Math.Max(j, i + 1);
        }
        return result;
    }

    /// <summary>The lines that replace the whole block (markers included) for a choice.</summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> lines, ConflictBlock block, ConflictChoice choice)
    {
        IEnumerable<string> Range((int First, int Count) r) => Enumerable.Range(r.First, r.Count).Select(k => lines[k]);
        return choice switch
        {
            ConflictChoice.Current => Range(block.Current).ToList(),
            ConflictChoice.Incoming => Range(block.Incoming).ToList(),
            ConflictChoice.Base => Range(block.Base).ToList(),
            _ => Range(block.Current).Concat(Range(block.Incoming)).ToList(),
        };
    }
}
