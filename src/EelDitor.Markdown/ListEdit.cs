using System.Text.RegularExpressions;

namespace EelDitor.Markdown;

/// <summary>A list item line: <c>  - [ ] text</c>, <c>3) text</c>…</summary>
public sealed record ListItem(string Prefix, string Marker, int? Number, char Delimiter, string Space, string Task, int ContentStart)
{
    /// <summary>Indentation (and quote markers) before the list marker.</summary>
    public int Indent => Prefix.Length;

    public bool Ordered => Number is not null;

    /// <summary>The prefix of a new item after this one: same indentation and kind, the next number, an empty box for tasks.</summary>
    public string NextPrefix(int? number = null) =>
        Prefix + (Ordered ? $"{number ?? Number + 1}{Delimiter}" : Marker) + " " + (Task.Length > 0 ? "[ ] " : "");
}

/// <summary>
/// Editing lists in the source: inserting an item before or after the current one, continuing a list on Enter, and
/// renumbering an ordered list. A list is renumbered only when it is numbered in sequence (1. 2. 3.); a list written
/// with one number repeated (1. 1. 1.), which Markdown numbers by itself, keeps it — so a change touches as few lines
/// as possible.
/// </summary>
public static partial class ListEdit
{
    [GeneratedRegex(@"^(?<prefix>[ \t]*(?:>[ ]?)*)(?<marker>[-*+]|(?<num>\d{1,9})(?<delim>[.)]))(?<space>[ ]+|$)(?<task>\[[ xX]\](?:[ ]+|$))?")]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex RuleRegex();

    /// <summary>The list item starting on this line, or null.</summary>
    public static ListItem? Parse(string line)
    {
        if (RuleRegex().IsMatch(line)) return null; // "- - -" or "* * *" is a horizontal rule
        var m = ItemRegex().Match(line);
        if (!m.Success) return null;
        int? number = m.Groups["num"].Success ? int.Parse(m.Groups["num"].Value) : null;
        char delim = m.Groups["delim"].Success ? m.Groups["delim"].Value[0] : '\0';
        return new ListItem(m.Groups["prefix"].Value, m.Groups["marker"].Value, number, delim, m.Groups["space"].Value, m.Groups["task"].Value, m.Length);
    }

    /// <summary>The line where the item containing <paramref name="line"/> starts (the line itself or one above it), or -1.</summary>
    public static int ItemStart(IReadOnlyList<string> lines, int line)
    {
        for (int i = line; i >= 0; i--)
        {
            if (Parse(lines[i]) is not null) return i;
            if (lines[i].Trim().Length == 0 && i != line) return -1;
            // A continuation line is indented (or a lazy line right under the item); keep going up.
        }
        return -1;
    }

    /// <summary>The last line of the item starting at <paramref name="start"/>: its continuation lines and nested lists.</summary>
    public static int ItemEnd(IReadOnlyList<string> lines, int start)
    {
        var item = Parse(lines[start])!;
        int end = start;
        for (int i = start + 1; i < lines.Count; i++)
        {
            string l = lines[i];
            if (l.Trim().Length == 0)
            {
                // A blank line belongs to the item only when something indented under it follows.
                int next = i + 1;
                while (next < lines.Count && lines[next].Trim().Length == 0) next++;
                if (next < lines.Count && IndentOf(lines[next]) > item.Indent) { i = next - 1; continue; }
                break;
            }
            if (Parse(l) is { } other && other.Indent <= item.Indent) break;
            if (Parse(l) is null && IndentOf(l) <= item.Indent && !LazyContinuation(l)) break;
            end = i;
        }
        return end;
    }

    private static bool LazyContinuation(string line) => !RuleRegex().IsMatch(line) && !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith('|');

    private static int IndentOf(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        return i;
    }

    /// <summary>The items of the same list and level as the item at <paramref name="start"/> (their first lines), in order.</summary>
    public static List<int> Siblings(IReadOnlyList<string> lines, int start)
    {
        var item = Parse(lines[start])!;
        bool Same(ListItem? o) => o is not null && o.Indent == item.Indent && o.Ordered == item.Ordered && (!item.Ordered || o.Delimiter == item.Delimiter);
        int first = start;
        for (int i = start - 1; i >= 0; i--)
        {
            if (Parse(lines[i]) is { } o)
            {
                if (Same(o)) { first = i; continue; }
                if (o.Indent < item.Indent) break;
                continue; // a nested item
            }
            if (lines[i].Trim().Length == 0) continue; // a loose list
            // Text: a continuation of an item above (indented, or a lazy line right under it), else the list ends.
            if (IndentOf(lines[i]) <= item.Indent && ItemStart(lines, i) < 0) break;
        }
        var result = new List<int>();
        for (int i = first; i < lines.Count;)
        {
            if (!Same(Parse(lines[i]))) break;
            result.Add(i);
            int end = ItemEnd(lines, i);
            int next = end + 1;
            while (next < lines.Count && lines[next].Trim().Length == 0) next++;
            if (next >= lines.Count || !Same(Parse(lines[next]))) break;
            i = next;
        }
        return result;
    }

    /// <summary>Whether an ordered list counts up by one from its first number.</summary>
    public static bool IsSequential(IReadOnlyList<string> lines, List<int> siblings)
    {
        if (siblings.Count < 2) return true;
        int first = Parse(lines[siblings[0]])!.Number ?? 0;
        for (int k = 0; k < siblings.Count; k++)
            if (Parse(lines[siblings[k]])!.Number != first + k) return false;
        return true;
    }

    /// <summary>A change: insert <see cref="Text"/> as a new line before line <see cref="InsertAt"/>, then replace the given lines (indices after the insertion).</summary>
    public sealed record Change(int InsertAt, string Text, int CaretColumn, List<(int Line, string Text)> Renumbered);

    /// <summary>A new empty item before or after the item containing <paramref name="line"/>, renumbering the list after it.</summary>
    public static Change? InsertItem(IReadOnlyList<string> lines, int line, bool after)
    {
        int start = ItemStart(lines, line);
        if (start < 0) return null;
        var item = Parse(lines[start])!;
        var siblings = Siblings(lines, start);
        bool renumber = item.Ordered && IsSequential(lines, siblings);
        int number = item.Number ?? 0;
        int? newNumber = !item.Ordered ? null : after ? (renumber ? number + 1 : number) : number;
        string text = item.NextPrefix(newNumber);
        int insertAt = after ? ItemEnd(lines, start) + 1 : start;
        var changes = new List<(int, string)>();
        if (renumber)
        {
            // Every item from the new one on: one more than before.
            int n = newNumber!.Value;
            foreach (int s in siblings.Where(s => after ? s > start : s >= start))
                changes.Add((s + 1, Renumber(lines[s], ++n)));
        }
        return new Change(insertAt, text, text.Length, changes.Where(c => c.Item2 != lines[c.Item1 - 1]).ToList());
    }

    /// <summary>
    /// Enter at <paramref name="column"/> of an item's first line: the rest of the line becomes a new item (renumbered
    /// list). Null when the line is not an item or the caret is in its marker. For an empty item see <see cref="IsEmptyItem"/>.
    /// </summary>
    public static Change? SplitItem(IReadOnlyList<string> lines, int line, int column)
    {
        if (Parse(lines[line]) is not { } item || column < item.ContentStart) return null;
        var siblings = Siblings(lines, line);
        bool renumber = item.Ordered && IsSequential(lines, siblings);
        int? newNumber = !item.Ordered ? null : renumber ? item.Number + 1 : item.Number;
        string rest = lines[line][column..].TrimStart();
        string prefix = item.NextPrefix(newNumber);
        var changes = new List<(int, string)>();
        if (renumber)
        {
            int n = newNumber!.Value;
            foreach (int s in siblings.Where(s => s > line))
                changes.Add((s + 1, Renumber(lines[s], ++n)));
        }
        return new Change(line + 1, prefix + rest, prefix.Length, changes.Where(c => c.Item2 != lines[c.Item1 - 1]).ToList());
    }

    /// <summary>
    /// The ordered list containing <paramref name="line"/> numbered 1…N (its own level only; nested lists keep their
    /// numbers): the lines that change, as (index, new text). Null when the line is not in an ordered list.
    /// </summary>
    public static List<(int Line, string Text)>? RenumberList(IReadOnlyList<string> lines, int line)
    {
        int start = ItemStart(lines, line);
        if (start < 0 || Parse(lines[start]) is not { Ordered: true }) return null;
        var changes = new List<(int, string)>();
        int n = 0;
        foreach (int s in Siblings(lines, start))
        {
            string text = Renumber(lines[s], ++n);
            if (text != lines[s]) changes.Add((s, text));
        }
        return changes;
    }

    /// <summary>An item with nothing after its marker (Enter there ends the list).</summary>
    public static bool IsEmptyItem(string line) => Parse(line) is { } item && line[item.ContentStart..].Trim().Length == 0;

    private static string Renumber(string line, int number)
    {
        var item = Parse(line)!;
        return item.Prefix + number + line[(item.Prefix.Length + item.Marker.Length - 1)..];
    }
}
