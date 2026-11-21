namespace EelDitor.Markdown;

/// <summary>
/// A pragmatic subset of CommonMark inline parsing: code spans, links, images, footnote references and
/// emphasis (<c>**</c>, <c>__</c>, <c>*</c>, <c>_</c>, <c>~~</c>, <c>==</c>), sub/superscript (<c>~x~</c>, <c>^x^</c>) and the HTML tags
/// <c>sub sup s del mark br</c>. Produces hidden marker ranges and styled ranges.
/// </summary>
public static class InlineParser
{
    private readonly record struct Delim(int Pos, int Length, char Ch);

    public static void Parse(string line, int start, int end, LineRender into, MdStyle baseStyle = MdStyle.None)
    {
        // Pass 1: code spans and links are opaque to emphasis.
        var opaque = new List<(int Start, int End)>();
        int i = start;
        while (i < end)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < end)
            {
                // A backslash escape (CommonMark): the backslash itself is not displayed.
                if (char.IsAsciiLetterOrDigit(line[i + 1]) == false && line[i + 1] > ' ' && line[i + 1] < 127) into.Hidden.Add(new HiddenRange(i, 1));
                i += 2;
                continue;
            }
            if (c == '`')
            {
                int run = CountRun(line, i, end, '`');
                int close = FindBacktickRun(line, i + run, end, run);
                if (close >= 0)
                {
                    into.Hidden.Add(new HiddenRange(i, run));
                    into.Hidden.Add(new HiddenRange(close, run));
                    into.Styles.Add(new StyledRange(i + run, close, baseStyle | MdStyle.Code));
                    opaque.Add((i, close + run));
                    i = close + run;
                    continue;
                }
                i += run;
                continue;
            }
            if (c == '<' && BreakTagLength(line, i, end) is int tag and > 0)
            {
                into.Breaks.Add(new HiddenRange(i, tag));
                opaque.Add((i, i + tag));
                i += tag;
                continue;
            }
            if (c == '<' && TryHtmlStyleTag(line, i, end, out int openLength, out int closeStart, out int closeLength, out var htmlStyle))
            {
                // <sub>, <sup>, <s>, <del>, <mark>: the tags are hidden, the content keeps parsing (emphasis inside).
                into.Hidden.Add(new HiddenRange(i, openLength));
                into.Hidden.Add(new HiddenRange(closeStart, closeLength));
                into.Styles.Add(new StyledRange(i + openLength, closeStart, baseStyle | htmlStyle));
                opaque.Add((i, i + openLength));
                opaque.Add((closeStart, closeStart + closeLength));
                i += openLength;
                continue;
            }
            if (c is '~' or '^' && TryScript(line, i, start, end, c) is int scriptClose and > 0)
            {
                // Pandoc / markdown-it: H~2~O, x^2^. One marker on each side, no spaces inside.
                into.Hidden.Add(new HiddenRange(i, 1));
                into.Hidden.Add(new HiddenRange(scriptClose, 1));
                into.Styles.Add(new StyledRange(i + 1, scriptClose, baseStyle | (c == '~' ? MdStyle.Subscript : MdStyle.Superscript)));
                opaque.Add((i, i + 1));
                opaque.Add((scriptClose, scriptClose + 1));
                i = scriptClose + 1;
                continue;
            }
            if (c == '!' && i + 1 < end && line[i + 1] == '['
                && TryParseLink(line, i + 1, end, out int altEnd, out int srcStart, out int srcEnd, out int imageEnd, image: true))
            {
                string target = line[srcStart..srcEnd].Trim();
                if (target.StartsWith('<') && target.IndexOf('>') is int gt and > 0) target = target[1..gt]; // <path with spaces>
                else if (target.IndexOf(' ') is int space and > 0) target = target[..space]; // drop an optional "title"
                if (altEnd > i + 2)
                {
                    into.Hidden.Add(new HiddenRange(i, 2));
                    into.Hidden.Add(new HiddenRange(altEnd, imageEnd - altEnd));
                    into.Styles.Add(new StyledRange(i + 2, altEnd, baseStyle | MdStyle.Image, target));
                    into.Images.Add(new ImageRef(altEnd, imageEnd - altEnd, target));
                }
                else
                {
                    // No alt text: show the path, hide "![](" and ")".
                    into.Hidden.Add(new HiddenRange(i, 4));
                    into.Hidden.Add(new HiddenRange(srcEnd, imageEnd - srcEnd));
                    into.Styles.Add(new StyledRange(srcStart, srcEnd, baseStyle | MdStyle.Image, target));
                    into.Images.Add(new ImageRef(srcEnd, imageEnd - srcEnd, target));
                }
                opaque.Add((i, imageEnd));
                i = imageEnd;
                continue;
            }
            if (c == '[' && i + 1 < end && line[i + 1] == '^' && TryParseFootnote(line, i, end, out int labelEnd))
            {
                bool definition = i == 0 && labelEnd + 1 < line.Length && line[labelEnd + 1] == ':' && baseStyle == MdStyle.None;
                into.Hidden.Add(new HiddenRange(i, 2));
                into.Hidden.Add(new HiddenRange(labelEnd, 1));
                into.Styles.Add(new StyledRange(i + 2, labelEnd, (definition ? MdStyle.None : baseStyle) | MdStyle.Footnote,
                    definition ? null : line[(i + 2)..labelEnd]));
                opaque.Add((i, labelEnd + 1));
                i = labelEnd + 1;
                continue;
            }
            if (c == '[' && TryParseLink(line, i, end, out int textEnd, out int urlStart, out int urlEnd, out int linkEnd))
            {
                into.Hidden.Add(new HiddenRange(i, 1));
                into.Hidden.Add(new HiddenRange(textEnd, linkEnd - textEnd));
                into.Styles.Add(new StyledRange(i + 1, textEnd, baseStyle | MdStyle.Link, line[urlStart..urlEnd]));
                // Emphasis inside link text is allowed; only the destination part is opaque.
                opaque.Add((textEnd, linkEnd));
                i = linkEnd;
                continue;
            }
            i++;
        }

        // Pass 2: emphasis, skipping opaque regions.
        opaque.Sort();
        var stack = new List<Delim>();
        i = start;
        int oi = 0;
        while (i < end)
        {
            while (oi < opaque.Count && opaque[oi].End <= i) oi++;
            if (oi < opaque.Count && i >= opaque[oi].Start) { i = opaque[oi].End; continue; }

            char c = line[i];
            if (c == '\\' && i + 1 < end) { i += 2; continue; }
            if (c is not ('*' or '_' or '~' or '=')) { i++; continue; }

            int run = CountRun(line, i, end, c);
            if (c is '~' or '=' && run != 2) { i += run; continue; }

            char before = i > start ? line[i - 1] : ' ';
            char after = i + run < end ? line[i + run] : ' ';
            bool leftFlanking = !char.IsWhiteSpace(after) && !(char.IsPunctuation(after) && !char.IsWhiteSpace(before) && !char.IsPunctuation(before));
            bool rightFlanking = !char.IsWhiteSpace(before) && !(char.IsPunctuation(before) && !char.IsWhiteSpace(after) && !char.IsPunctuation(after));
            bool canOpen = leftFlanking, canClose = rightFlanking;
            if (c is '_' or '=') // no intra-word emphasis: a==b, snake_case
            {
                canOpen = leftFlanking && (!rightFlanking || char.IsPunctuation(before));
                canClose = rightFlanking && (!leftFlanking || char.IsPunctuation(after));
            }

            int pos = i, remaining = run;
            if (canClose)
            {
                // Match against openers from the top of the stack.
                for (int s = stack.Count - 1; s >= 0 && remaining > 0; s--)
                {
                    var op = stack[s];
                    if (op.Ch != c) continue;
                    int use = c is '~' or '=' || (op.Length >= 2 && remaining >= 2) ? 2 : 1;
                    MdStyle style = c switch { '~' => MdStyle.Strike, '=' => MdStyle.Highlight, _ => use == 2 ? MdStyle.Bold : MdStyle.Italic };
                    int openerMarkStart = op.Pos + op.Length - use;
                    into.Hidden.Add(new HiddenRange(openerMarkStart, use));
                    into.Hidden.Add(new HiddenRange(pos, use));
                    into.Styles.Add(new StyledRange(op.Pos + op.Length, pos, baseStyle | style));
                    // Discard openers above the matched one (they cannot close across).
                    stack.RemoveRange(s + 1, stack.Count - s - 1);
                    if (op.Length - use > 0) stack[s] = op with { Length = op.Length - use };
                    else { stack.RemoveAt(s); }
                    pos += use;
                    remaining -= use;
                    s = stack.Count; // restart search from the (new) top
                }
            }
            if (remaining > 0 && canOpen) stack.Add(new Delim(pos, remaining, c));
            i += run;
        }

        into.Hidden.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    /// <summary>Length of a <c>&lt;br&gt;</c>, <c>&lt;br/&gt;</c> or <c>&lt;br /&gt;</c> tag (any case) at <paramref name="i"/>, or 0.</summary>
    public static int BreakTagLength(string s, int i, int end)
    {
        if (i + 4 > end || s[i] != '<' || (s[i + 1] | 0x20) != 'b' || (s[i + 2] | 0x20) != 'r') return 0;
        int j = i + 3;
        while (j < end && s[j] == ' ') j++;
        if (j < end && s[j] == '/') j++;
        return j < end && s[j] == '>' ? j + 1 - i : 0;
    }

    private static readonly (string Name, MdStyle Style)[] HtmlStyleTags =
        [("sub", MdStyle.Subscript), ("sup", MdStyle.Superscript), ("del", MdStyle.Strike), ("s", MdStyle.Strike), ("mark", MdStyle.Highlight)];

    /// <summary><c>&lt;sub&gt;…&lt;/sub&gt;</c> and the like (no attributes, any case) starting at <paramref name="i"/>, closed on the same line.</summary>
    public static bool TryHtmlStyleTag(string s, int i, int end, out int openLength, out int closeStart, out int closeLength, out MdStyle style)
    {
        openLength = closeStart = closeLength = 0;
        style = MdStyle.None;
        foreach (var (name, st) in HtmlStyleTags)
        {
            string open = "<" + name + ">", close = "</" + name + ">";
            if (i + open.Length > end || string.Compare(s, i, open, 0, open.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            int from = i + open.Length;
            int at = s.IndexOf(close, from, end - from, StringComparison.OrdinalIgnoreCase);
            if (at <= from) return false;
            openLength = open.Length;
            closeStart = at;
            closeLength = close.Length;
            style = st;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The closing marker of a subscript <c>~x~</c> or superscript <c>^x^</c> opened at <paramref name="i"/>, or -1:
    /// single markers (<c>~~</c> is strikethrough), a non-empty content without spaces.
    /// </summary>
    private static int TryScript(string s, int i, int start, int end, char c)
    {
        if ((i > start && s[i - 1] == c) || i + 1 >= end || s[i + 1] == c || char.IsWhiteSpace(s[i + 1])) return -1;
        for (int j = i + 1; j < end; j++)
        {
            char d = s[j];
            if (d == '\\') { j++; continue; }
            if (char.IsWhiteSpace(d)) return -1;
            if (d == c) return j + 1 < end && s[j + 1] == c ? -1 : j;
        }
        return -1;
    }

    private static int CountRun(string s, int i, int end, char c)
    {
        int n = 0;
        while (i + n < end && s[i + n] == c) n++;
        return n;
    }

    private static int FindBacktickRun(string s, int from, int end, int length)
    {
        int i = from;
        while (i < end)
        {
            if (s[i] == '`')
            {
                int run = CountRun(s, i, end, '`');
                if (run == length) return i;
                i += run;
            }
            else i++;
        }
        return -1;
    }

    /// <summary><c>[^label]</c>: a label without spaces or brackets.</summary>
    private static bool TryParseFootnote(string s, int open, int end, out int close)
    {
        close = -1;
        for (int i = open + 2; i < end; i++)
        {
            char c = s[i];
            if (c == ']') { close = i; return i > open + 2; }
            if (char.IsWhiteSpace(c) || c is '[' or '^') return false;
        }
        return false;
    }

    private static bool TryParseLink(string s, int open, int end, out int textEnd, out int urlStart, out int urlEnd, out int linkEnd, bool image = false)
    {
        textEnd = urlStart = urlEnd = linkEnd = -1;
        if (!image && open > 0 && s[open - 1] == '!') return false; // handled as an image
        int depth = 0;
        for (int i = open; i < end; i++)
        {
            char c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) { textEnd = i; break; }
        }
        if (textEnd < 0 || (!image && textEnd == open + 1) || textEnd + 1 >= end || s[textEnd + 1] != '(') return false;
        int paren = 0;
        for (int i = textEnd + 1; i < end; i++)
        {
            char c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == '(') paren++;
            else if (c == ')' && --paren == 0)
            {
                urlStart = textEnd + 2;
                urlEnd = i;
                linkEnd = i + 1;
                return true;
            }
            else if (c == ' ' && paren == 1 && i == textEnd + 2) return false;
        }
        return false;
    }
}
