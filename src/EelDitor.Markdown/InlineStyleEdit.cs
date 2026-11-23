namespace EelDitor.Markdown;

/// <summary>
/// Adding or removing an inline style marker (** * ~~ ` ==) around a range of one line, checked before it is used:
/// the line must show exactly the same characters afterwards (no stray visible markers), the whole range must
/// have the style (or lose it), and — through <c>parse</c> — a table row must stay a table row. Text such as a
/// lone "*" or a range that already contains bold cannot always be wrapped; then the result is null and the
/// caller leaves that range as it is.
/// </summary>
public static class InlineStyleEdit
{
    /// <summary>The characters a line shows (hidden markup removed) and the style of each.</summary>
    public static (string Text, MdStyle[] Styles) Visible(string line, LineRender render)
    {
        var text = new System.Text.StringBuilder(line.Length);
        var styles = new List<MdStyle>(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (render.IsHidden(i)) continue;
            var style = MdStyle.None;
            foreach (var s in render.Styles)
                if (i >= s.Start && i < s.End) style |= s.Style;
            text.Append(line[i]);
            styles.Add(style);
        }
        return (text.ToString(), styles.ToArray());
    }

    private static int VisibleBefore(string line, LineRender render, int index)
    {
        int n = 0;
        for (int i = 0; i < index && i < line.Length; i++) if (!render.IsHidden(i)) n++;
        return n;
    }

    /// <summary>
    /// <paramref name="line"/> with [<paramref name="start"/>, <paramref name="end"/>) wrapped in the marker, or null.
    /// <paramref name="parse"/> renders a candidate line (null when it is no longer valid, e.g. not the same table row).
    /// </summary>
    public static string? TryWrap(string line, int start, int end, string marker, MdStyle style, Func<string, LineRender?> parse)
    {
        if (end <= start || parse(line) is not { } before) return null;
        var (oldText, oldStyles) = Visible(line, before);
        int from = VisibleBefore(line, before, start), count = VisibleBefore(line, before, end) - from;
        string inner = line[start..end];
        var candidates = new List<string>();
        // Part of the range already has this style: preferably drop the inner markers and style the whole range.
        // Markers right outside the range belong to it too ("**bold**:" selected from "bold" to the end): left
        // there they would pile up ("****bold**:**").
        int s2 = start, e2 = end;
        if (s2 >= marker.Length && line.AsSpan(s2 - marker.Length, marker.Length).SequenceEqual(marker)) s2 -= marker.Length;
        if (e2 + marker.Length <= line.Length && line.AsSpan(e2, marker.Length).SequenceEqual(marker)) e2 += marker.Length;
        if ((s2 != start || e2 != end) && line[s2..e2].Contains(marker, StringComparison.Ordinal))
            candidates.Add(line[..s2] + marker + line[s2..e2].Replace(marker, "") + marker + line[e2..]);
        if (marker.Length > 1 && inner.Contains(marker, StringComparison.Ordinal))
            candidates.Add(line[..start] + marker + inner.Replace(marker, "") + marker + line[end..]);
        candidates.Add(line[..start] + marker + inner + marker + line[end..]);
        foreach (var candidate in candidates)
        {
            if (parse(candidate) is not { } after) continue;
            var (newText, newStyles) = Visible(candidate, after);
            if (newText != oldText) continue;
            // The range gets the style and nothing else changes: no other character gains or loses a style
            // (markers that cross other spans, like "***a**b*", would restyle the text around the range).
            bool ok = true;
            for (int i = 0; i < newStyles.Length && ok; i++)
                ok = i >= from && i < from + count ? newStyles[i] == (oldStyles[i] | style) : newStyles[i] == oldStyles[i];
            if (ok) return candidate;
        }
        return null;
    }

    /// <summary><paramref name="line"/> with the markers right outside [start, end) removed, or null if that changes what is shown.</summary>
    public static string? TryUnwrap(string line, int start, int end, string marker, Func<string, LineRender?> parse)
    {
        if (start < marker.Length || end + marker.Length > line.Length) return null;
        if (line.Substring(start - marker.Length, marker.Length) != marker || line.Substring(end, marker.Length) != marker) return null;
        if (parse(line) is not { } before) return null;
        string candidate = line[..(start - marker.Length)] + line[start..end] + line[(end + marker.Length)..];
        if (parse(candidate) is not { } after) return null;
        return Visible(candidate, after).Text == Visible(line, before).Text ? candidate : null;
    }

    /// <summary>A parser for a table row in its table (header and separator lines given), keeping the cell count.</summary>
    public static Func<string, LineRender?> TableRowParser(string headerLine, string separatorLine, bool isHeader, int cellCount) => candidate =>
    {
        string text = isHeader ? candidate + "\n" + separatorLine : headerLine + "\n" + separatorLine + "\n" + candidate;
        var doc = MarkdownDocument.Parse(text);
        int index = isHeader ? 0 : 2;
        if (doc.Tables.Count == 0 || doc.Tables[0].Rows.Count <= index || doc.Tables[0].Rows[index].Cells.Count != cellCount) return null;
        return doc.GetLine(index);
    };

    /// <summary>A parser for an ordinary line.</summary>
    public static LineRender? LineParser(string candidate) => MarkdownDocument.Parse(candidate).GetLine(0);
}
