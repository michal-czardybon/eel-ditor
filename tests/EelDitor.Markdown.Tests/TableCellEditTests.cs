using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class TableCellEditTests
{
    private const string Header = "| command | use it to |";
    private const string Sep = "|---|---|";

    private static string Edit(string line, int cell, string source)
    {
        var r = TableCellEdit.ApplySource(line, cell, source, Header, Sep, isHeader: false);
        Assert.True(r.Ok, r.Problem.ToString());
        return r.NewLine!;
    }

    private static TableRow Row(string line) => MarkdownDocument.Parse(Header + "\n" + Sep + "\n" + line).Tables[0].Rows[2];

    [Theory]
    [InlineData("| `lookup` | x |", 0, "`lookup <lang>`", "| `lookup <lang>` | x |")]
    [InlineData("| a | x |", 0, "**a** b", "| **a** b | x |")]
    [InlineData("| a | x |", 0, "a | b", "| a \\| b | x |")]          // a bare pipe is escaped
    [InlineData("| a | x |", 0, "a \\| b", "| a \\| b | x |")]        // an escaped one stays
    [InlineData("| a | x |", 1, "dwie\nlinie", "| a | dwie<br>linie |")]
    [InlineData("|  | x |", 0, "nowy", "| nowy | x |")]
    [InlineData("| a | x |", 0, "a`", "| a` | x |")]                 // a lone backtick at the end is plain text
    public void Edits(string line, int cell, string source, string expected) => Assert.Equal(expected, Edit(line, cell, source));

    [Fact]
    public void ABacktickThatWouldSwallowTheNextCell_IsEscaped()
    {
        // "a`" would open a code span closed in the next cell, so the pipe between them would stop counting.
        Assert.Equal("| a\\` | `b` |", Edit("| `a` | `b` |", 0, "a`"));
    }

    [Fact]
    public void ATrailingBackslash_DoesNotEscapeThePipe() =>
        Assert.Equal("|a\\\\| x |", Edit("|a| x |", 0, "a\\"));

    [Fact]
    public void RandomEdits_NeverBreakTheTable()
    {
        string[] rows =
        [
            "| `lookup <lang> <word>` | find entries (give its base form: `okno`, `grać`) |",
            "| `inflect <id\\|lemma> [Feat=Val …]` | get the **form** you need, e.g. `inflect 1989` → `okon` |",
            "|[link](http://x.pl) and ==mark==|a\\|b ~~gone~~ _it_|",
            "|  | x |",
        ];
        const string chars = "ab cd`|*_\\[]()<>~=!#";
        var random = new Random(12345);
        for (int n = 0; n < 5000; n++)
        {
            string line = rows[random.Next(rows.Length)];
            int cell = random.Next(2);
            var row = Row(line);
            int cs = row.Cells[cell].ContentStart(line), ce = row.Cells[cell].ContentEnd(line);
            string source = ce > cs ? line[cs..ce] : "";
            int a = random.Next(source.Length + 1);
            int b = Math.Min(source.Length, a + random.Next(4));
            string typed = new(Enumerable.Range(0, random.Next(4)).Select(_ => chars[random.Next(chars.Length)]).ToArray());
            string edited = source[..a] + typed + source[b..];

            var r = TableCellEdit.ApplySource(line, cell, edited, Header, Sep, false);
            Assert.True(r.Ok, $"{line} / {cell} / '{edited}': {r.Problem}");
            var after = Row(r.NewLine!);
            Assert.Equal(row.Cells.Count, after.Cells.Count);
            int other = 1 - cell;
            Assert.Equal(line[row.Cells[other].Start..row.Cells[other].End], r.NewLine![after.Cells[other].Start..after.Cells[other].End]);
        }
    }

    [Fact]
    public void HeaderCell()
    {
        var r = TableCellEdit.ApplySource(Header, 1, "use | it", Header, Sep, isHeader: true);
        Assert.Equal("| command | use \\| it |", r.NewLine);
    }

    [Fact]
    public void Unchanged_ReturnsTheSameLine() => Assert.Equal("| `a` | x |", Edit("| `a` | x |", 0, "`a`"));

    [Theory]
    [InlineData("a ` b", "a \\` b")]
    [InlineData("`a` ` b", "`a` \\` b")]
    [InlineData("``x`` `", "``x`` \\`")]
    [InlineData("`a`", "`a`")]
    public void EscapeUnmatchedBackticks(string text, string expected) => Assert.Equal(expected, TableCellEdit.EscapeUnmatchedBackticks(text));

    [Fact]
    public void LineBreaks_AreWrittenAsBr() =>
        Assert.Equal("| a<br>b<br>c | x |", Edit("| old | x |", 0, "a\nb \r\n c"));
}
