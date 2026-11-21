using System.Text;
using System.Text.RegularExpressions;

namespace EelDitor.Core.Text;

/// <summary>How new paragraphs are broken into lines while typing (and how "reflow" formats a paragraph).</summary>
public enum WrapMode
{
    /// <summary>A paragraph is one line (the view can soft-wrap it).</summary>
    OneLine,
    Width80,
    Width100,
    Width120,
    /// <summary>Semantic line breaks: every sentence starts on a new line (sembr.org).</summary>
    Sentences,
}

/// <summary>
/// Paragraph line breaking that keeps git history clean:
/// <list type="bullet">
/// <item>while typing, only text that crosses the limit (or starts a new sentence at the end of a line) is broken,
/// so existing paragraphs written in another style are never rewritten;</item>
/// <item>"reflow" formats one paragraph on request;</item>
/// <item>lines never start with something Markdown would read as a block (#, -, 1., &gt;, |, ```), so breaking
/// a line never changes what the document means.</item>
/// </list>
/// </summary>
public static partial class ParagraphWrap
{
    public static int? Width(this WrapMode mode) => mode switch
    {
        WrapMode.Width80 => 80,
        WrapMode.Width100 => 100,
        WrapMode.Width120 => 120,
        _ => null,
    };

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<quote>(?:>[ ]?)*)(?<marker>(?:[-*+]|\d{1,9}[.)])[ ]+(?:\[[ xX]\][ ]+)?)?")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^(#{1,6}(\s|$)|>|[-*+](\s|$)|\d{1,9}[.)](\s|$)|```|~~~|\||=+$|-+$|<|\[\^[^\]]*\]:)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex Fence();

    /// <summary>True when a line starting with this word would be read by Markdown as a block (heading, list, quote…).</summary>
    public static bool StartsBlock(string word) => BlockStart().IsMatch(word);

    /// <summary>The prefix of a paragraph's first line (indent, quote, list marker) and of its continuation lines.</summary>
    public static (string First, string Next) Prefixes(string line, bool markdown)
    {
        if (!markdown)
        {
            int ws = 0;
            while (ws < line.Length && line[ws] is ' ' or '\t') ws++;
            return (line[..ws], line[..ws]);
        }
        var m = Prefix().Match(line);
        string indent = m.Groups["indent"].Value, quote = m.Groups["quote"].Value, marker = m.Groups["marker"].Value;
        return (m.Value, indent + quote + new string(' ', marker.Length));
    }

    /// <summary>A line that is not prose: headings, tables, HTML, indented code (Markdown), or blank.</summary>
    public static bool IsProse(string line, bool markdown)
    {
        if (line.Trim().Length == 0) return false;
        if (!markdown) return true;
        string t = line.TrimStart();
        if (t.StartsWith('#') || t.StartsWith('|') || t.StartsWith('<') || Fence().IsMatch(line)) return false;
        if ((line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t')) && !Prefix().Match(line).Groups["marker"].Success) return false;
        if (t.Contains(" | ", StringComparison.Ordinal) && t.Count(c => c == '|') >= 2) return false;
        if (Regex.IsMatch(t, @"^(=+|-+|\*{3,}|_{3,})\s*$")) return false; // setext underline, thematic break
        return true;
    }

    /// <summary>
    /// The YAML front matter of a Markdown file (<c>---</c> on the first line up to the closing <c>---</c> or
    /// <c>...</c>): the index of the closing line, or -1. Its lines are data, never prose.
    /// </summary>
    public static int FrontMatterEnd(IReadOnlyList<string> lines, int maxLines = 500)
    {
        if (lines.Count == 0 || lines[0].TrimEnd() != "---") return -1;
        for (int i = 1; i < Math.Min(lines.Count, maxLines); i++)
            if (lines[i].TrimEnd() is "---" or "...") return i;
        return -1;
    }

    // ------------------------------------------------------------------ sentences

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Polish
        "np", "m.in", "tzn", "tj", "itd", "itp", "wg", "ok", "godz", "ul", "al", "prof", "dr", "mgr", "inż", "hab",
        "św", "tys", "mln", "mld", "nr", "str", "cz", "rys", "tab", "por", "zob", "ang", "łac", "ds", "im", "ks", "gen",
        "płk", "kpt", "tel", "zm", "ur", "jw", "ww", "dot", "red", "oprac", "tłum", "wyd", "nt", "sp", "pkt", "ust", "art",
        "poz", "kl", "przyp", "śp", "lit", "pt", "wł", "ew", "dn", "br", "cdn", "pn", "gr", "zł",
        // English
        "e.g", "i.e", "etc", "vs", "mr", "mrs", "ms", "st", "jr", "sr", "no", "fig", "figs", "cf", "approx", "dept",
        "inc", "ltd", "co", "vol", "ch", "pp", "ed", "eds", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep",
        "sept", "oct", "nov", "dec", "a.m", "p.m", "u.s",
    };

    private static bool IsCloser(char c) => c is '"' or '\'' or '”' or '’' or '»' or ')' or ']' or '*' or '_';

    private static bool IsOpener(char c) => c is '"' or '\'' or '„' or '“' or '«' or '(' or '[' or '*' or '_';

    /// <summary>
    /// Whether the text before <paramref name="spaceStart"/> ends a sentence: . ! ? or … (optionally followed by
    /// closing quotes, brackets or emphasis), not after a known abbreviation or a single-letter initial.
    /// </summary>
    public static bool EndsSentence(string text, int spaceStart)
    {
        int i = spaceStart - 1;
        while (i >= 0 && IsCloser(text[i])) i--;
        if (i < 0 || text[i] is not ('.' or '!' or '?' or '…')) return false;
        if (text[i] != '.') return true;
        int wordEnd = i, wordStart = i;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && !IsOpener(text[wordStart - 1])) wordStart--;
        string word = text[wordStart..wordEnd];
        if (word.Length == 0) return false;                         // "..." handled as "…"? a lone dot: no
        if (word.Length == 1 && char.IsLetter(word[0])) return false; // an initial: J. Kowalski
        if (Abbreviations.Contains(word)) return false;
        if (word.EndsWith('.')) return word.EndsWith("..", StringComparison.Ordinal); // "m.in." handled above; "..." ends
        return true;
    }

    /// <summary>The next sentence starts at <paramref name="index"/>: (after openers) an uppercase letter or a digit.</summary>
    private static bool StartsSentence(string text, int index)
    {
        while (index < text.Length && IsOpener(text[index])) index++;
        return index < text.Length && (char.IsUpper(text[index]) || char.IsDigit(text[index]));
    }

    /// <summary>Whitespace runs (start, length) after which a new sentence begins; none inside inline code.</summary>
    public static List<(int Start, int Length)> SentenceBreaks(string text, bool markdown = true)
    {
        var result = new List<(int, int)>();
        var code = Unbreakable(text, markdown);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != ' ' || i == 0 || InSpan(code, i)) continue;
            int end = i;
            while (end < text.Length && text[end] == ' ') end++;
            if (end < text.Length && EndsSentence(text, i) && StartsSentence(text, end)) result.Add((i, end - i));
            i = end - 1;
        }
        return result;
    }

    // ------------------------------------------------------------------ while typing

    /// <summary>
    /// After typing at <paramref name="caretColumn"/> (the typed text ends there), the whitespace to replace by a line
    /// break, or null. Width modes break only when this input made the line cross the limit (a paragraph already
    /// longer, e.g. written as one line, is left alone). Sentence mode breaks when a sentence is started at the end
    /// of a line. The caller makes sure the line is prose (not code, a table or a heading).
    /// </summary>
    public static (int Start, int Length, string Prefix)? BreakAfterTyping(string line, int caretColumn, int typedLength, WrapMode mode, bool markdown)
    {
        var (first, next) = Prefixes(line, markdown);
        if (mode.Width() is int width)
        {
            if (line.Length <= width || line.Length - typedLength > width) return null;
            // The last space that leaves the first part within the limit and the next word safe to start a line.
            for (int i = Math.Min(width, line.Length - 1); i > first.Length; i--)
            {
                if (line[i] != ' ' || line[i - 1] == ' ') continue;
                int end = i;
                while (end < line.Length && line[end] == ' ') end++;
                if (end >= line.Length) continue;
                if (markdown && StartsBlock(NextWord(line, end))) continue;
                if (InSpan(Unbreakable(line, markdown), i)) continue;
                return (i, end - i, next);
            }
            return null;
        }
        if (mode == WrapMode.Sentences)
        {
            // "…end. N" typed at the end of the line: the N (after optional openers) is what was just typed.
            if (caretColumn < 1 || line.AsSpan(caretColumn).Trim().Length > 0) return null;
            int letter = caretColumn - 1;
            if (!char.IsUpper(line[letter]) && !char.IsDigit(line[letter])) return null;
            int start = letter;
            while (start > 0 && IsOpener(line[start - 1])) start--;
            int spaceEnd = start, spaceStart = start;
            while (spaceStart > 0 && line[spaceStart - 1] == ' ') spaceStart--;
            if (spaceStart == spaceEnd || spaceStart <= first.Length) return null;
            if (!EndsSentence(line, spaceStart) || InSpan(Unbreakable(line, markdown), spaceStart)) return null;
            if (markdown && StartsBlock(NextWord(line, spaceEnd))) return null;
            return (spaceStart, spaceEnd - spaceStart, next);
        }
        return null;
    }

    private static string NextWord(string line, int start)
    {
        int end = start;
        while (end < line.Length && line[end] != ' ') end++;
        return line[start..end];
    }

    /// <summary>
    /// Markdown inline spans (bold, italic, links…) of a text, as [start, end) ranges a line break must not split:
    /// the editor reads emphasis line by line, so a span broken over two lines would show its markers. Set by the
    /// application (the Markdown parser lives elsewhere).
    /// </summary>
    public static Func<string, IEnumerable<(int Start, int End)>>? InlineSpans { get; set; }

    /// <summary>Ranges a line break must not split: inline code, and in Markdown the styled spans.</summary>
    private static List<(int Start, int End)> Unbreakable(string text, bool markdown)
    {
        var spans = CodeSpans(text);
        if (markdown && InlineSpans is { } inline) spans.AddRange(inline(text));
        return spans;
    }

    private static bool InSpan(List<(int Start, int End)> spans, int index) => spans.Any(s => index > s.Start && index < s.End);

    /// <summary>
    /// Inline code spans [start, end) with their backticks (CommonMark: a run of N backticks up to the next run of
    /// exactly N, so <c>`` ` ``</c> is one span). A run without a partner is plain text.
    /// </summary>
    public static List<(int Start, int End)> CodeSpans(string text)
    {
        var result = new List<(int, int)>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] != '`') { i++; continue; }
            int start = i;
            while (i < text.Length && text[i] == '`') i++;
            int n = i - start;
            for (int j = i; j < text.Length;)
            {
                if (text[j] != '`') { j++; continue; }
                int run = j;
                while (j < text.Length && text[j] == '`') j++;
                if (j - run == n) { result.Add((start, j)); i = j; break; }
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ reflow

    /// <summary>
    /// The paragraph around line <paramref name="index"/>: consecutive prose lines, ending at a blank line, a hard
    /// break, the next list item or anything that is not prose. Null when the line is not prose.
    /// </summary>
    public static (int Start, int End)? FindParagraph(IReadOnlyList<string> lines, int index, bool markdown)
    {
        if (index < 0 || index >= lines.Count || !IsProse(lines[index], markdown) || markdown && InFence(lines, index)) return null;
        if (markdown && index <= FrontMatterEnd(lines)) return null;
        return ParagraphAround(lines, index, markdown);
    }

    /// <summary>
    /// How the paragraphs of a text are broken into lines: the way most paragraphs follow (at least 60 % of those
    /// that tell), <c>Mixed</c> when no way dominates, null when no paragraph tells (only short ones). A one-line
    /// paragraph longer than 120 characters counts as one line per paragraph; a broken one as sentences when most of
    /// its lines end a sentence, otherwise by its longest line (≤ 80, ≤ 100, ≤ 120).
    /// </summary>
    public static (WrapMode? Mode, bool Mixed) Detect(IReadOnlyList<string> lines, bool markdown)
    {
        var votes = new Dictionary<WrapMode, int>();
        int frontMatter = markdown ? FrontMatterEnd(lines) : -1;
        bool fence = false;
        for (int i = frontMatter + 1; i < lines.Count; i++)
        {
            if (markdown && Fence().IsMatch(lines[i])) { fence = !fence; continue; }
            if (fence || !IsProse(lines[i], markdown)) continue;
            var (start, end) = ParagraphAround(lines, i, markdown);
            start = Math.Max(start, i);
            i = end;
            var body = Enumerable.Range(start, end - start + 1).Select(k => lines[k].TrimEnd()).ToList();
            WrapMode? vote;
            if (body.Count == 1) vote = body[0].Length > 120 ? WrapMode.OneLine : null;
            else
            {
                var broken = body.Take(body.Count - 1).ToList();
                int sentences = broken.Count(l => EndsSentence(l, l.Length));
                int longest = broken.Max(l => l.Length);
                vote = sentences * 4 >= broken.Count * 3 ? WrapMode.Sentences
                    : longest <= 80 ? WrapMode.Width80
                    : longest <= 100 ? WrapMode.Width100
                    : longest <= 120 ? WrapMode.Width120
                    : WrapMode.OneLine;
            }
            if (vote is { } v) votes[v] = votes.GetValueOrDefault(v) + 1;
        }
        if (votes.Count == 0) return (null, false);
        var top = votes.MaxBy(kv => kv.Value);
        return top.Value * 5 >= votes.Values.Sum() * 3 ? (top.Key, false) : (null, true);
    }

    private static (int Start, int End) ParagraphAround(IReadOnlyList<string> lines, int index, bool markdown)
    {
        bool StartsItem(string l) => markdown && Prefix().Match(l).Groups["marker"].Success;
        bool HardBreak(string l) => markdown && (l.EndsWith("  ", StringComparison.Ordinal) || l.EndsWith('\\'));
        int start = index;
        while (start > 0 && !StartsItem(lines[start]) && IsProse(lines[start - 1], markdown) && !HardBreak(lines[start - 1])
               && Quote(lines[start - 1], markdown) == Quote(lines[start], markdown))
            start--;
        int end = index;
        while (end + 1 < lines.Count && IsProse(lines[end + 1], markdown) && !StartsItem(lines[end + 1]) && !HardBreak(lines[end])
               && Quote(lines[end + 1], markdown) == Quote(lines[end], markdown))
            end++;
        return (start, end);
    }

    private static string Quote(string line, bool markdown) => markdown ? Prefix().Match(line).Groups["quote"].Value.Replace(" ", "") : "";

    private static bool InFence(IReadOnlyList<string> lines, int index)
    {
        bool inFence = false;
        for (int i = 0; i < index; i++) if (Fence().IsMatch(lines[i])) inFence = !inFence;
        return inFence;
    }

    /// <summary>Formats lines <paramref name="start"/>..<paramref name="end"/> (one paragraph) in the given mode.</summary>
    public static List<string> Reflow(IReadOnlyList<string> lines, int start, int end, WrapMode mode, bool markdown)
    {
        var (first, next) = Prefixes(lines[start], markdown);
        var body = new StringBuilder(lines[start][first.Length..].Trim());
        for (int i = start + 1; i <= end; i++)
        {
            var (p, _) = Prefixes(lines[i], markdown);
            string part = lines[i][p.Length..].Trim();
            if (part.Length > 0) body.Append(' ').Append(part);
        }
        string trailing = markdown && lines[end].EndsWith("  ", StringComparison.Ordinal) ? "  " : "";
        string text = body.ToString();
        var result = new List<string>();
        switch (mode)
        {
            case WrapMode.Sentences:
            {
                int pos = 0;
                string prefix = first;
                foreach (var (s, len) in SentenceBreaks(text, markdown))
                {
                    if (markdown && StartsBlock(NextWord(text, s + len))) continue;
                    result.Add(prefix + text[pos..s]);
                    prefix = next;
                    pos = s + len;
                }
                result.Add(prefix + text[pos..]);
                break;
            }
            case WrapMode.Width80 or WrapMode.Width100 or WrapMode.Width120:
                result.AddRange(WrapWords(text, first, next, mode.Width()!.Value, markdown));
                break;
            default:
                result.Add(first + text);
                break;
        }
        result[^1] += trailing;
        return result;
    }

    /// <summary>Greedy word wrap; a word that would start a Markdown block stays on the previous line.</summary>
    public static List<string> WrapWords(string text, string first, string next, int width, bool markdown)
    {
        var result = new List<string>();
        var current = new StringBuilder(first);
        bool hasWord = false;
        var code = markdown ? Unbreakable(text, markdown) : [];
        int pos = 0, prevEnd = 0;
        while (pos < text.Length)
        {
            if (text[pos] == ' ') { pos++; continue; }
            int end = pos;
            while (end < text.Length && text[end] != ' ') end++;
            string w = text[pos..end];
            // Never break inside inline code (its spaces are kept as they are).
            bool inCode = InSpan(code, pos);
            bool safe = !inCode && !(markdown && StartsBlock(w));
            // A span that cannot be broken goes to the next line as a whole when the whole does not fit.
            int unitEnd = end;
            while (unitEnd < text.Length && InSpan(code, unitEnd))
            {
                while (unitEnd < text.Length && text[unitEnd] == ' ') unitEnd++;
                while (unitEnd < text.Length && text[unitEnd] != ' ') unitEnd++;
            }
            if (hasWord && safe && current.Length + 1 + (unitEnd - pos) > width)
            {
                result.Add(current.ToString());
                current.Clear().Append(next);
                hasWord = false;
            }
            if (hasWord) current.Append(inCode ? text[prevEnd..pos] : " ");
            current.Append(w);
            hasWord = true;
            prevEnd = pos = end;
        }
        result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Conversion of a whole text: every prose line longer than <paramref name="minLength"/> is split — at sentence
    /// ends (<see cref="WrapMode.Sentences"/>) or at the width. Lines are only split, never joined, so only the long
    /// lines change.
    /// </summary>
    public static string SplitLongLines(string text, WrapMode mode, bool markdown, string newLine, int minLength)
    {
        var sb = new StringBuilder(text.Length + text.Length / 40);
        bool inFence = false;
        // Front matter (YAML) is kept as it is: count its lines first.
        int frontMatterLines = markdown ? FrontMatterEnd(LineDiff_Split(text)) + 1 : 0;
        int lineIndex = -1;
        int pos = 0;
        while (pos < text.Length)
        {
            lineIndex++;
            int end = pos;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            int brk = end < text.Length ? (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1) : 0;
            string line = text[pos..end];
            string lineBreak = text.Substring(end, brk);
            pos = end + brk;
            if (lineIndex < frontMatterLines) { sb.Append(line).Append(lineBreak); continue; }
            if (markdown && Fence().IsMatch(line)) inFence = !inFence;
            else if (!inFence && line.Length > minLength && IsProse(line, markdown))
            {
                sb.Append(string.Join(newLine, Reflow([line], 0, 0, mode, markdown)));
                sb.Append(lineBreak);
                continue;
            }
            sb.Append(line).Append(lineBreak);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The opposite of <see cref="SplitLongLines"/>: every prose paragraph broken over several lines becomes one line
    /// (list items and quotes keep their prefix; hard breaks, headings, tables, code and front matter stay). Only the
    /// line breaks change, never a word. Returns the text and the number of paragraphs joined.
    /// </summary>
    public static (string Text, int Joined) JoinParagraphs(string text, bool markdown)
    {
        var lines = new List<string>();
        var breaks = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            int end = pos;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            int brk = end < text.Length ? (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1) : 0;
            lines.Add(text[pos..end]);
            breaks.Add(text.Substring(end, brk));
            pos = end + brk;
        }
        int frontMatter = markdown ? FrontMatterEnd(lines) : -1;
        var sb = new StringBuilder(text.Length);
        bool inFence = false;
        int joined = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i <= frontMatter) { sb.Append(lines[i]).Append(breaks[i]); continue; }
            if (markdown && Fence().IsMatch(lines[i])) inFence = !inFence;
            else if (!inFence && IsProse(lines[i], markdown))
            {
                var (_, last) = ParagraphAround(lines, i, markdown);
                if (last > i)
                {
                    sb.Append(Reflow(lines, i, last, WrapMode.OneLine, markdown)[0]).Append(breaks[last]);
                    joined++;
                    i = last;
                    continue;
                }
            }
            sb.Append(lines[i]).Append(breaks[i]);
        }
        return (sb.ToString(), joined);
    }

    /// <summary>The first lines of a text (enough for the front matter) without splitting all of it.</summary>
    private static string[] LineDiff_Split(string text)
    {
        int end = 0, count = 0;
        while (end < text.Length && count < 500) { if (text[end] == '\n') count++; end++; }
        return text[..end].Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>Whether line <paramref name="index"/> lies in the front matter; reads only the first lines.</summary>
    public static bool InFrontMatter(Func<int, string?> line, int index)
    {
        if (line(0)?.TrimEnd() != "---") return false;
        for (int i = 1; i <= index && i < 500; i++)
            if (line(i)?.TrimEnd() is "---" or "...") return false;
        return index < 500;
    }
}
