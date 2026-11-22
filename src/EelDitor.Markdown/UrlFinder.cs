namespace EelDitor.Markdown;

/// <summary>A web address found in text: its characters [Start, End) and the address to open.</summary>
public readonly record struct FoundUrl(int Start, int End, string Url);

/// <summary>
/// Finds bare web addresses in text (<c>https://…</c>, <c>http://…</c>, <c>www.…</c>, <c>mailto:…</c>), following
/// GFM's autolink extension: the address ends at whitespace or <c>&lt;</c>; trailing punctuation (<c>. , : ; ! ? ' " * _ ~</c>)
/// and an unbalanced closing parenthesis or bracket are not part of it ("see https://x.org/a_(b)." → "https://x.org/a_(b)").
/// </summary>
public static class UrlFinder
{
    private static readonly string[] Schemes = ["https://", "http://", "www.", "mailto:"];

    /// <summary>The address starting at <paramref name="i"/>, or null. <paramref name="i"/> must be at a word start.</summary>
    public static FoundUrl? At(string text, int i, int end)
    {
        if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '/' or '.' or '@' or ':' or '-')) return null;
        string? scheme = null;
        foreach (var s in Schemes)
            if (end - i > s.Length && string.Compare(text, i, s, 0, s.Length, StringComparison.OrdinalIgnoreCase) == 0) { scheme = s; break; }
        if (scheme is null) return null;
        int j = i + scheme.Length;
        while (j < end && !char.IsWhiteSpace(text[j]) && text[j] != '<') j++;
        // Trailing punctuation and unbalanced closers are not part of the address.
        while (j > i + scheme.Length)
        {
            char c = text[j - 1];
            if (c is '.' or ',' or ':' or ';' or '!' or '?' or '\'' or '"' or '*' or '_' or '~') { j--; continue; }
            if (c == ')' && Count(text, i, j, '(') < Count(text, i, j, ')')) { j--; continue; }
            if (c == ']' && Count(text, i, j, '[') < Count(text, i, j, ']')) { j--; continue; }
            break;
        }
        if (j <= i + scheme.Length) return null;
        // "www." needs a host with a dot after it; "https://" needs a host at all.
        string body = text[(i + scheme.Length)..j];
        if (scheme == "www." && !body.Contains('.')) return null;
        if (!char.IsLetterOrDigit(body[0])) return null;
        string url = text[i..j];
        if (scheme == "www.") url = "https://" + url;
        return new FoundUrl(i, j, url);
    }

    /// <summary>All addresses in the text.</summary>
    public static List<FoundUrl> FindAll(string text) => FindAll(text, 0, text.Length);

    public static List<FoundUrl> FindAll(string text, int start, int end)
    {
        var result = new List<FoundUrl>();
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c is not ('h' or 'H' or 'w' or 'W' or 'm' or 'M')) continue;
            if (At(text, i, end) is { } found) { result.Add(found); i = found.End - 1; }
        }
        return result;
    }

    private static int Count(string s, int from, int to, char c)
    {
        int n = 0;
        for (int i = from; i < to; i++) if (s[i] == c) n++;
        return n;
    }
}
