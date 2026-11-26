using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EelDitor.Markdown;

/// <summary>
/// Table rows on the clipboard. Copying puts the rows as Markdown (plain text, for editors) and as an HTML table (for
/// Word, Excel, mail); pasting takes rows from an HTML table (Word, Excel, web pages), from Markdown rows or from
/// tab-separated text (Excel), and writes them as Markdown rows.
/// </summary>
public static partial class TableClipboard
{
    [GeneratedRegex(@"<table\b[^>]*>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<(t[dh])\b[^>]*>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<br\s*/?>|</p>\s*<p\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<(/?)(b|strong|i|em|s|del|strike|code)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex(@"<!--.*?-->|<style\b.*?</style>|<script\b.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JunkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex SeparatorRegex();

    /// <summary>The rows of the first HTML table in <paramref name="html"/> (cells as Markdown), or null.</summary>
    public static List<List<string>>? FromHtml(string html)
    {
        var table = TableRegex().Match(html);
        if (!table.Success) return null;
        var rows = new List<List<string>>();
        foreach (Match row in RowRegex().Matches(table.Groups[1].Value))
        {
            var cells = CellRegex().Matches(row.Groups[1].Value).Select(c => CellFromHtml(c.Groups[2].Value)).ToList();
            if (cells.Count > 0) rows.Add(cells);
        }
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>
    /// Whether the first HTML table marks its first row as a header (<c>&lt;thead&gt;</c> or <c>&lt;th&gt;</c> cells).
    /// Word and most pages do not: their rows are all body rows.
    /// </summary>
    public static bool HasHeader(string html)
    {
        var table = TableRegex().Match(html);
        if (!table.Success) return false;
        if (Regex.IsMatch(table.Groups[1].Value, @"<thead\b", RegexOptions.IgnoreCase)) return true;
        var first = RowRegex().Match(table.Groups[1].Value);
        return first.Success && Regex.IsMatch(first.Groups[1].Value, @"<th\b", RegexOptions.IgnoreCase);
    }

    private static string CellFromHtml(string html)
    {
        html = JunkRegex().Replace(html, "");
        html = BreakRegex().Replace(html, "\u0001");                       // a line break inside the cell
        html = StyleTagRegex().Replace(html, m => m.Groups[2].Value.ToLowerInvariant() switch
        {
            "b" or "strong" => "**",
            "i" or "em" => "*",
            "s" or "del" or "strike" => "~~",
            _ => "`",
        });
        string text = WebUtility.HtmlDecode(TagRegex().Replace(html, ""));
        text = SpacesRegex().Replace(text, " ").Replace("\u0001", "\n");
        // Markers left empty or around spaces only ("** **") would show as text.
        text = text.Replace("****", "").Replace("** **", " ");
        return string.Join("\n", text.Split('\n').Select(l => l.Trim())).Trim('\n');
    }

    /// <summary>Rows of tab-separated text (Excel, Google Sheets), or null when it has no tabs.</summary>
    public static List<List<string>>? FromTsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length == 0 || !lines[0].Contains('\t')) return null;
        return lines.Select(l => l.Split('\t').Select(c => c.Trim()).ToList()).ToList();
    }

    /// <summary>Markdown table rows in <paramref name="text"/> (every non-empty line a row), separators left out; or null.</summary>
    public static List<string>? MarkdownRows(string text, bool requireClosingPipe = false)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0 || !lines.All(l => l.TrimStart().StartsWith('|')
            && (!requireClosingPipe || l.TrimEnd().EndsWith('|') && l.Trim().Length > 1))) return null;
        return lines.Where(l => !SeparatorRegex().IsMatch(l)).ToList();
    }

    /// <summary>A Markdown row with <paramref name="columns"/> cells (missing ones empty; extra ones joined to the last).</summary>
    public static string Row(IReadOnlyList<string> cells, int columns)
    {
        var list = cells.Select(Escape).ToList();
        if (columns > 0 && list.Count > columns)
            list = [.. list.Take(columns - 1), string.Join(" ", list.Skip(columns - 1).Where(c => c.Length > 0))];
        while (list.Count < columns) list.Add("");
        return "| " + string.Join(" | ", list) + " |";
    }

    /// <summary>A cell's text as Markdown table content: a bare | escaped, a line break written as &lt;br&gt;.</summary>
    public static string Escape(string cell)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cell.Length; i++)
        {
            char c = cell[i];
            if (c == '\\' && i + 1 < cell.Length) { sb.Append(c).Append(cell[++i]); continue; }
            if (c == '|') sb.Append("\\|");
            else if (c == '\n') sb.Append("<br>");
            else if (c != '\r') sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>A separator row for <paramref name="columns"/> columns.</summary>
    public static string Separator(int columns) => "|" + string.Concat(Enumerable.Repeat("---|", Math.Max(1, columns)));

    /// <summary>
    /// An HTML table of rows of cell HTML (the first row as a header when <paramref name="header"/>), with borders, so
    /// Word and Excel paste it as a table.
    /// </summary>
    public static string HtmlTable(IEnumerable<IReadOnlyList<string>> rows, bool header)
    {
        var sb = new StringBuilder("<table border=\"1\" style=\"border-collapse:collapse\">");
        bool first = true;
        foreach (var row in rows)
        {
            string tag = header && first ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append('<').Append(tag).Append(" style=\"padding:2px 6px\">").Append(cell).Append("</").Append(tag).Append('>');
            sb.Append("</tr>");
            first = false;
        }
        return sb.Append("</table>").ToString();
    }

    /// <summary>The Windows clipboard's "HTML Format" (CF_HTML): a header with byte offsets, then the page, as UTF-8.</summary>
    public static byte[] WindowsHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        string before = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->", after = "<!--EndFragment--></body></html>";
        int headerLength = Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return Encoding.UTF8.GetBytes(string.Format(header, startHtml, endHtml, startFragment, endFragment) + before + fragment + after);
    }

    /// <summary>The HTML in a CF_HTML clipboard value (or the text itself when it has no such header).</summary>
    public static string HtmlFromClipboard(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data).TrimEnd('\0');
        var m = Regex.Match(text, @"StartHTML:(\d+)");
        if (!m.Success) return text;
        int start = int.Parse(m.Groups[1].Value);
        var bytes = Encoding.UTF8.GetBytes(text);
        return start > 0 && start < bytes.Length ? Encoding.UTF8.GetString(bytes, start, bytes.Length - start) : text;
    }
}
