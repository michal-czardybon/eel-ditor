using System.Text;
using System.Text.RegularExpressions;

namespace EelDitor.Core.Text;

/// <summary>
/// Hard-wraps long lines at spaces to a maximum width ("reflow" of very long paragraphs).
/// In Markdown it keeps the structure: code blocks, tables, headings, indented code and HTML lines are left alone;
/// list items and block quotes continue with the right indentation. Lines are only split, never joined,
/// and a word longer than the width (a URL) is never cut.
/// </summary>
public static partial class LineWrap
{
    public const int LongLineThreshold = 1000;

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<quote>(?:>[ ]?)*)(?<marker>(?:[-*+]|\d{1,9}[.)])[ ]+(?:\[[ xX]\][ ]+)?)?")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex Fence();

    /// <summary>Length of the longest line (without its line break).</summary>
    public static int LongestLine(string text)
    {
        int max = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n' || text[i] == '\r')
            {
                max = Math.Max(max, i - start);
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        return max;
    }

    /// <summary>Wraps every line longer than <paramref name="width"/>; new line breaks use <paramref name="newLine"/>.</summary>
    public static string Wrap(string text, int width, bool markdown, string newLine)
    {
        if (width < 10) throw new ArgumentOutOfRangeException(nameof(width));
        var sb = new StringBuilder(text.Length + text.Length / width * 4);
        bool inFence = false;
        int pos = 0;
        while (pos < text.Length)
        {
            int end = pos;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            int brk = end < text.Length ? (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1) : 0;
            string line = text[pos..end];
            string lineBreak = text.Substring(end, brk);
            pos = end + brk;

            if (markdown && Fence().IsMatch(line)) { inFence = !inFence; sb.Append(line).Append(lineBreak); continue; }
            if (line.Length <= width || (markdown && (inFence || IsUnwrappable(line))))
            {
                sb.Append(line).Append(lineBreak);
                continue;
            }
            AppendWrapped(sb, line, width, markdown, newLine);
            sb.Append(lineBreak);
        }
        return sb.ToString();
    }

    private static bool IsUnwrappable(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith('#') || t.StartsWith('|') || t.StartsWith('<')
            || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t')
            || (t.Contains(" | ", StringComparison.Ordinal) && t.Count(c => c == '|') >= 2);
    }

    private static void AppendWrapped(StringBuilder sb, string line, int width, bool markdown, string newLine)
    {
        string first = "", next = "";
        int bodyStart = 0;
        if (markdown)
        {
            var m = Prefix().Match(line);
            string indent = m.Groups["indent"].Value, quote = m.Groups["quote"].Value, marker = m.Groups["marker"].Value;
            first = m.Value;
            next = indent + quote + new string(' ', marker.Length);
            bodyStart = m.Length;
        }
        else
        {
            int ws = 0;
            while (ws < line.Length && line[ws] is ' ' or '\t') ws++;
            first = next = line[..ws];
            bodyStart = ws;
        }

        // A Markdown hard break (two trailing spaces) must stay at the end of the last piece.
        string body = line[bodyStart..];
        string trailing = markdown && body.EndsWith("  ", StringComparison.Ordinal) ? "  " : "";
        // Never starts a line with something Markdown reads as a block (#, -, 1., >, |, ```).
        sb.AppendJoin(newLine, ParagraphWrap.WrapWords(body.TrimEnd(), first, next, width, markdown)).Append(trailing);
    }
}
