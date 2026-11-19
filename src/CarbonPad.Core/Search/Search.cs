using System.Text;
using System.Text.RegularExpressions;
using CarbonPad.Core.Text;

namespace CarbonPad.Core.Search;

public enum SearchMode
{
    /// <summary>Literal text.</summary>
    Normal,

    /// <summary>Literal text with escapes \n \r \t \0 \\ \xNN \uNNNN (Notepad++ "Extended").</summary>
    Extended,

    /// <summary>.NET regular expression.</summary>
    Regex,
}

public sealed record SearchOptions(
    string Pattern,
    bool MatchCase = false,
    bool WholeWord = false,
    SearchMode Mode = SearchMode.Normal,
    bool WrapAround = true)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Builds the regex for these options. Throws <see cref="ArgumentException"/> for an invalid pattern.</summary>
    public Regex ToRegex(bool rightToLeft = false)
    {
        string pattern = Mode switch
        {
            SearchMode.Normal => System.Text.RegularExpressions.Regex.Escape(Pattern),
            SearchMode.Extended => System.Text.RegularExpressions.Regex.Escape(Unescape(Pattern)),
            _ => Pattern,
        };
        if (WholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!MatchCase) options |= RegexOptions.IgnoreCase;
        if (rightToLeft) options |= RegexOptions.RightToLeft;
        return new Regex(pattern, options, Timeout);
    }

    /// <summary>Expands replacement text for the current mode (regex substitutions stay as-is).</summary>
    public string PrepareReplacement(string replacement) => Mode switch
    {
        SearchMode.Normal => replacement.Replace("$", "$$"),
        SearchMode.Extended => Unescape(replacement).Replace("$", "$$"),
        _ => Unescape(replacement, regexReplacement: true),
    };

    public static string Unescape(string s, bool regexReplacement = false)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case '\\': sb.Append(regexReplacement ? "\\" : "\\"); break;
                case 'x' when i + 2 < s.Length && IsHex(s.AsSpan(i + 1, 2)):
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16)); i += 2; break;
                case 'u' when i + 4 < s.Length && IsHex(s.AsSpan(i + 1, 4)):
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append('\\').Append(n); break;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}

/// <summary>One match, with the line it is on for display in the results table.</summary>
public sealed record SearchHit(string? FilePath, int Offset, int Length, int Line, int Column, string LineText, int LineMatchStart, int LineMatchLength)
{
    /// <summary>Identifies the open document when <see cref="FilePath"/> is null (untitled buffers).</summary>
    public object? DocumentKey { get; init; }
}

public static class TextSearch
{
    public const int MaxHitsPerFile = 10_000;
    private const int MaxPreview = 300;

    /// <summary>Finds the next match starting at <paramref name="start"/> (forward) or ending before it (backward).</summary>
    public static Match? FindNext(string text, SearchOptions options, int start, bool forward)
    {
        if (options.Pattern.Length == 0) return null;
        var regex = options.ToRegex(rightToLeft: !forward);
        start = Math.Clamp(start, 0, text.Length);
        var m = forward ? regex.Match(text, start) : regex.Match(text, 0, start);
        if (m.Success && m.Length == 0 && forward && m.Index == start && start < text.Length)
            m = regex.Match(text, start + 1);
        if (m.Success) return m;
        if (!options.WrapAround) return null;
        m = forward ? regex.Match(text, 0) : regex.Match(text);
        return m.Success ? m : null;
    }

    public static List<SearchHit> FindAll(string text, SearchOptions options, string? filePath = null, object? key = null, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        if (options.Pattern.Length == 0) return hits;
        var regex = options.ToRegex();
        var lineStarts = LineIndex.Build(text);
        foreach (Match m in regex.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            if (m.Length == 0 && options.Mode == SearchMode.Regex && hits.Count > 0 && hits[^1].Offset == m.Index) continue;
            int line = lineStarts.LineOf(m.Index);
            int ls = lineStarts.Start(line);
            int le = lineStarts.End(line, text);
            int col = m.Index - ls;
            // Long lines: show a window around the match.
            int previewStart = ls, previewEnd = le;
            if (le - ls > MaxPreview)
            {
                previewStart = Math.Max(ls, m.Index - 80);
                previewEnd = Math.Min(le, previewStart + MaxPreview);
            }
            string preview = text[previewStart..previewEnd].Replace('\t', ' ');
            int ms = m.Index - previewStart;
            int ml = Math.Min(m.Length, Math.Max(0, previewEnd - m.Index));
            hits.Add(new SearchHit(filePath, m.Index, m.Length, line + 1, col + 1, preview, ms, ml) { DocumentKey = key });
            if (hits.Count >= MaxHitsPerFile) break;
        }
        return hits;
    }

    /// <summary>Replaces all matches; returns the new text and the count.</summary>
    public static (string Text, int Count) ReplaceAll(string text, SearchOptions options, string replacement)
    {
        if (options.Pattern.Length == 0) return (text, 0);
        var regex = options.ToRegex();
        int count = 0;
        string repl = options.PrepareReplacement(replacement);
        string result = regex.Replace(text, m => { count++; return m.Result(repl); });
        return (result, count);
    }

    /// <summary>Line start offsets for mapping match positions to line numbers.</summary>
    public sealed class LineIndex
    {
        private readonly List<int> _starts;

        private LineIndex(List<int> starts) => _starts = starts;

        public static LineIndex Build(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    starts.Add(i + 1);
                }
                else if (c == '\n') starts.Add(i + 1);
            }
            return new LineIndex(starts);
        }

        public int LineOf(int offset)
        {
            int i = _starts.BinarySearch(offset);
            return i >= 0 ? i : ~i - 1;
        }

        public int Start(int line) => _starts[line];

        public int End(int line, string text)
        {
            int end = line + 1 < _starts.Count ? _starts[line + 1] : text.Length;
            while (end > _starts[line] && text[end - 1] is '\n' or '\r') end--;
            return end;
        }
    }
}

public sealed record FileSearchOptions(
    string Directory,
    string Include = "*.*",
    string Exclude = "bin;obj;.git;node_modules;.vs",
    bool Recursive = true,
    bool IncludeHidden = false);

/// <summary>Searches files on disk in parallel. Binary files are skipped; encodings are detected per file.</summary>
public static class FileSearch
{
    public static IEnumerable<string> EnumerateFiles(FileSearchOptions options, CancellationToken ct)
    {
        var include = Globs(options.Include);
        var exclude = options.Exclude.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GlobToRegex).ToList();
        var pending = new Stack<string>();
        pending.Push(options.Directory);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = options.Recursive ? Directory.EnumerateDirectories(dir) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var f in SafeList(files))
            {
                string name = Path.GetFileName(f);
                if (!options.IncludeHidden && IsHidden(f, name)) continue;
                if (exclude.Any(r => r.IsMatch(name))) continue;
                if (include.Count > 0 && !include.Any(r => r.IsMatch(name))) continue;
                yield return f;
            }
            foreach (var d in SafeList(dirs).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(d);
                if (exclude.Any(r => r.IsMatch(name))) continue;
                if (!options.IncludeHidden && IsHidden(d, name)) continue;
                pending.Push(d);
            }
        }
    }

    private static List<string> SafeList(IEnumerable<string> items)
    {
        var list = new List<string>();
        try { list.AddRange(items); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static bool IsHidden(string path, string name)
    {
        if (name.StartsWith('.')) return true;
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Hidden); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static List<Regex> Globs(string patterns) =>
        patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p is not ("*" or "*.*"))
            .Select(GlobToRegex).ToList();

    public static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Searches files in parallel and reports hits per file as they are found.
    /// <paramref name="openDocumentText"/> supplies the current buffer for files open in the editor.
    /// </summary>
    public static async Task<(int Files, int Hits)> SearchAsync(
        FileSearchOptions fileOptions,
        SearchOptions searchOptions,
        Func<string, string?> openDocumentText,
        Action<IReadOnlyList<SearchHit>> onHits,
        CancellationToken ct)
    {
        searchOptions.ToRegex(); // validate early
        int files = 0, hits = 0;
        await Parallel.ForEachAsync(EnumerateFiles(fileOptions, ct),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            async (path, token) =>
            {
                Interlocked.Increment(ref files);
                string? text = openDocumentText(path);
                if (text is null)
                {
                    byte[] bytes;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length > 256L * 1024 * 1024) return;
                        bytes = await File.ReadAllBytesAsync(path, token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
                    var decoded = TextFile.Decode(bytes);
                    if (decoded.LooksBinary) return;
                    text = decoded.Text;
                }
                var found = TextSearch.FindAll(text, searchOptions, path, ct: token);
                if (found.Count == 0) return;
                Interlocked.Add(ref hits, found.Count);
                onHits(found);
            });
        return (files, hits);
    }
}
