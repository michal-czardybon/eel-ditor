namespace EelDitor.Markdown;

/// <summary>
/// Turns one inline style into another (highlight ↔ bold ↔ italic) by replacing only the markers of each span:
/// <c>==a==</c> → <c>**a**</c>. Every change is checked like <see cref="InlineStyleEdit"/>: the line must show the same
/// characters, the span's characters swap the style and nothing else changes (a table row stays the same row, via
/// <c>parse</c>). A span that cannot be converted safely is left as it is and counted.
/// </summary>
public static class StyleConversion
{
    public static readonly MdStyle[] Convertible = [MdStyle.Highlight, MdStyle.Bold, MdStyle.Italic];

    /// <summary>Whether <paramref name="marker"/> (the source text of a hidden marker) writes <paramref name="style"/>.</summary>
    private static bool Writes(string marker, MdStyle style) => style switch
    {
        MdStyle.Bold => marker is "**" or "__",
        MdStyle.Italic => marker is "*" or "_",
        MdStyle.Highlight => marker is "==",
        _ => false,
    };

    /// <summary>The marker for <paramref name="to"/>, in the family of the old one (<c>__</c> → <c>_</c>).</summary>
    private static string MarkerFor(MdStyle to, string old) => to switch
    {
        MdStyle.Bold => old[0] == '_' ? "__" : "**",
        MdStyle.Italic => old[0] == '_' ? "_" : "*",
        _ => "==",
    };

    /// <summary>
    /// <paramref name="line"/> with the spans of <paramref name="from"/> that touch [<paramref name="start"/>,
    /// <paramref name="end"/>) written as <paramref name="to"/>.
    /// </summary>
    public static string Convert(string line, MdStyle from, MdStyle to, int start, int end, Func<string, LineRender?> parse,
        out int converted, out int skipped)
    {
        converted = skipped = 0;
        var tried = new HashSet<int>(); // visible start of each span already handled
        for (int guard = 0; guard < 200; guard++)
        {
            if (parse(line) is not { } render) return line;
            bool changed = false;
            foreach (var s in render.Styles.OrderBy(s => s.Start))
            {
                if ((s.Style & from) == 0 || s.End <= start || s.Start >= end) continue;
                int key = VisibleBefore(line, render, s.Start);
                if (!tried.Add(key)) continue;
                var open = render.Hidden.LastOrDefault(h => h.End == s.Start);
                var close = render.Hidden.FirstOrDefault(h => h.Start == s.End);
                if (open.Length == 0 || close.Length == 0) { skipped++; continue; }
                string a = line.Substring(open.Start, open.Length), b = line.Substring(close.Start, close.Length);
                // The markers of exactly this style (bold+italic "***" is two hidden ranges: one per style).
                if (!Writes(a, from) || a != b) { if (s.Style.HasFlag(from) && IsStyleSpan(s, render)) skipped++; continue; }
                string marker = MarkerFor(to, a);
                string candidate = line[..open.Start] + marker + line[open.End..close.Start] + marker + line[close.End..];
                if (Check(line, render, candidate, parse, key, VisibleBefore(line, render, s.End) - key, from, to))
                {
                    // The range moves with the text when the markers change length ("*" → "==").
                    int d = marker.Length - open.Length;
                    int Shift(int p) => p + (p >= open.End ? d : 0) + (p >= close.End ? d : 0);
                    (start, end) = (Shift(start), Shift(end));
                    line = candidate;
                    converted++;
                    changed = true;
                    break; // offsets moved: parse again
                }
                skipped++;
            }
            if (!changed) return line;
        }
        return line;
    }

    /// <summary>The style a marker writes (<c>**</c> bold, <c>&lt;sub&gt;</c> subscript, <c>[</c> a link…), or None.</summary>
    private static MdStyle StyleOf(string marker) => marker switch
    {
        "**" or "__" => MdStyle.Bold,
        "*" or "_" => MdStyle.Italic,
        "~~" => MdStyle.Strike,
        "==" => MdStyle.Highlight,
        "~" => MdStyle.Subscript,
        "^" => MdStyle.Superscript,
        "[" => MdStyle.Link,
        _ when marker.Length > 0 && marker.All(c => c == '`') => MdStyle.Code,
        _ => marker.ToLowerInvariant() switch
        {
            "<sub>" => MdStyle.Subscript,
            "<sup>" => MdStyle.Superscript,
            "<s>" or "<del>" => MdStyle.Strike,
            "<mark>" => MdStyle.Highlight,
            _ => MdStyle.None,
        },
    };

    /// <summary>
    /// <paramref name="line"/> with all inline formatting removed from [<paramref name="start"/>, <paramref name="end"/>)
    /// (an empty range: the spans around that column). A span reaching outside the range is closed before it and
    /// reopened after it; a link is removed only as a whole (its text stays). Every change is verified.
    /// </summary>
    public static string Clear(string line, int start, int end, Func<string, LineRender?> parse, out int cleared, out int skipped)
    {
        cleared = skipped = 0;
        var tried = new HashSet<(int, MdStyle)>();
        for (int guard = 0; guard < 400; guard++)
        {
            if (parse(line) is not { } render) return line;
            bool changed = false;
            foreach (var s in render.Styles.OrderBy(s => s.Start))
            {
                bool touches = start == end ? s.Start <= start && s.End >= end : s.Start < end && s.End > start;
                if (!touches) continue;
                var open = render.Hidden.LastOrDefault(h => h.End == s.Start);
                var close = render.Hidden.FirstOrDefault(h => h.Start == s.End);
                if (open.Length == 0 || close.Length == 0) continue; // not a span of its own markers (a heading, a table header)
                string a = line.Substring(open.Start, open.Length), b = line.Substring(close.Start, close.Length);
                var own = StyleOf(a);
                if (own == MdStyle.None || (s.Style & own) == 0) continue;
                int key = VisibleBefore(line, render, s.Start);
                if (!tried.Add((key, own))) continue;
                // Edits (position, removed length, inserted text), left to right.
                var edits = new List<(int Pos, int Remove, string Insert)>();
                int cs = Math.Max(start, s.Start), ce = Math.Min(end, s.End);
                if (own == MdStyle.Link || start == end || cs == s.Start && ce == s.End)
                {
                    (cs, ce) = (s.Start, s.End);
                    edits.Add((open.Start, open.Length, ""));
                    edits.Add((close.Start, close.Length, ""));
                }
                else
                {
                    // Only the selected part loses the style: the span is closed before it and reopened after it.
                    if (cs == s.Start) edits.Add((open.Start, open.Length, "")); else edits.Add((cs, 0, b));
                    if (ce == s.End) edits.Add((close.Start, close.Length, "")); else edits.Add((ce, 0, a));
                }
                var sb = new System.Text.StringBuilder();
                int at = 0;
                foreach (var (pos, remove, insert) in edits) { sb.Append(line, at, pos - at).Append(insert); at = pos + remove; }
                string candidate = sb.Append(line, at, line.Length - at).ToString();
                int from = VisibleBefore(line, render, cs), to = VisibleBefore(line, render, ce);
                if (CheckCleared(line, render, candidate, parse, from, to, own))
                {
                    // The range follows the text: a marker put at its start goes before it, one put at its end after it.
                    int Shift(int p, bool isStart)
                    {
                        int q = p;
                        foreach (var (pos, remove, insert) in edits)
                        {
                            if (pos < p || pos == p && isStart && remove == 0) q += insert.Length;
                            if (pos < p) q -= Math.Min(remove, p - pos);
                        }
                        return q;
                    }
                    (start, end) = (Shift(start, true), Shift(end, false));
                    if (end < start) end = start;
                    line = candidate;
                    cleared++;
                    changed = true;
                    break;
                }
                skipped++;
            }
            if (!changed) return line;
        }
        return line;
    }

    private static bool CheckCleared(string line, LineRender before, string candidate, Func<string, LineRender?> parse, int from, int to, MdStyle removed)
    {
        if (parse(candidate) is not { } after) return false;
        var (oldText, oldStyles) = InlineStyleEdit.Visible(line, before);
        var (newText, newStyles) = InlineStyleEdit.Visible(candidate, after);
        if (oldText != newText) return false;
        for (int i = 0; i < newStyles.Length; i++)
        {
            var expected = i >= from && i < to ? oldStyles[i] & ~removed : oldStyles[i];
            if (newStyles[i] != expected) return false;
        }
        return true;
    }

    /// <summary>A span written with this style's own markers (not, e.g., the bold of a table header).</summary>
    private static bool IsStyleSpan(StyledRange s, LineRender render) =>
        render.Hidden.Any(h => h.End == s.Start) && render.Hidden.Any(h => h.Start == s.End);

    private static bool Check(string line, LineRender before, string candidate, Func<string, LineRender?> parse, int from, int count, MdStyle oldStyle, MdStyle newStyle)
    {
        if (parse(candidate) is not { } after) return false;
        var (oldText, oldStyles) = InlineStyleEdit.Visible(line, before);
        var (newText, newStyles) = InlineStyleEdit.Visible(candidate, after);
        if (oldText != newText) return false;
        for (int i = 0; i < newStyles.Length; i++)
        {
            var expected = i >= from && i < from + count ? (oldStyles[i] & ~oldStyle) | newStyle : oldStyles[i];
            if (newStyles[i] != expected) return false;
        }
        return true;
    }

    private static int VisibleBefore(string line, LineRender render, int index)
    {
        int n = 0;
        for (int i = 0; i < index && i < line.Length; i++) if (!render.IsHidden(i)) n++;
        return n;
    }
}
