using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class ListEditTests
{
    private static string[] L(string text) => text.Split('\n');

    private static string Apply(string text, ListEdit.Change c, int? splitAt = null)
    {
        var lines = L(text).ToList();
        if (splitAt is int col) { lines[c.InsertAt - 1] = lines[c.InsertAt - 1][..col]; }
        lines.Insert(c.InsertAt, c.Text);
        foreach (var (i, t) in c.Renumbered) lines[i] = t;
        return string.Join("\n", lines);
    }

    [Theory]
    [InlineData("- a", "-", null, "")]
    [InlineData("  12. tekst", "12.", 12, "  ")]
    [InlineData("3) x", "3)", 3, "")]
    [InlineData("- [x] zrobione", "-", null, "")]
    public void Parses(string line, string marker, int? number, string prefix)
    {
        var item = ListEdit.Parse(line)!;
        Assert.Equal(marker, item.Marker);
        Assert.Equal(number, item.Number);
        Assert.Equal(prefix, item.Prefix);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("* * *")]
    [InlineData("tekst")]
    [InlineData("-tekst")]
    public void NotItems(string line) => Assert.Null(ListEdit.Parse(line));

    [Fact]
    public void InsertBelow_RenumbersTheRest()
    {
        string text = "1. a\n2. b\n   dalej b\n3. c";
        var c = ListEdit.InsertItem(L(text), 1, after: true)!;
        Assert.Equal("1. a\n2. b\n   dalej b\n3. \n4. c", Apply(text, c));
        Assert.Equal(3, c.CaretColumn);
    }

    [Fact]
    public void InsertAbove_TakesTheNumber()
    {
        string text = "1. a\n2. b";
        Assert.Equal("1. a\n2. \n3. b", Apply(text, ListEdit.InsertItem(L(text), 1, after: false)!));
    }

    [Fact]
    public void ARepeatedNumber_IsKept()
    {
        string text = "1. a\n1. b";
        Assert.Equal("1. a\n1. \n1. b", Apply(text, ListEdit.InsertItem(L(text), 0, after: true)!));
    }

    [Fact]
    public void NestedItems_AreNotRenumbered_AndBelowGoesAfterThem()
    {
        string text = "1. a\n   1. x\n   2. y\n2. b";
        Assert.Equal("1. a\n   1. x\n   2. y\n2. \n3. b", Apply(text, ListEdit.InsertItem(L(text), 0, after: true)!));
    }

    [Fact]
    public void Bullets_AndTasks()
    {
        Assert.Equal("* a\n* \n* b", Apply("* a\n* b", ListEdit.InsertItem(L("* a\n* b"), 0, after: true)!));
        Assert.Equal("- [x] a\n- [ ] ", Apply("- [x] a", ListEdit.InsertItem(L("- [x] a"), 0, after: true)!));
    }

    [Fact]
    public void Enter_SplitsTheItem_AndRenumbers()
    {
        string text = "1. ala ma kota\n2. drugi";
        var c = ListEdit.SplitItem(L(text), 0, 9)!;   // after "ala ma"
        Assert.Equal("1. ala ma\n2. kota\n3. drugi", Apply(text, c, splitAt: 9));
        Assert.Null(ListEdit.SplitItem(L(text), 0, 1)); // in the marker
        Assert.True(ListEdit.IsEmptyItem("3. "));
        Assert.False(ListEdit.IsEmptyItem("3. x"));
    }
}

public class ClearFormattingTests
{
    private static string Clear(string line, int start, int end, out int cleared) =>
        StyleConversion.Clear(line, start, end, InlineStyleEdit.LineParser, out cleared, out _);

    [Theory]
    [InlineData("a **b** *c* ~~d~~ ==e== `f` H~2~O x^2^ [g](http://x) <sub>h</sub>", "a b c d e f H2O x2 g h", 9)]
    [InlineData("**gruby *i pochyły***", "gruby i pochyły", 2)]
    public void TheWholeLine(string line, string expected, int count)
    {
        Assert.Equal(expected, Clear(line, 0, line.Length, out int cleared));
        Assert.Equal(count, cleared);
    }

    [Fact]
    public void PartOfASpan_IsTakenOutOfIt()
    {
        // "**abcdef**", "cd" selected (columns 4..6).
        Assert.Equal("**ab**cd**ef**", Clear("**abcdef**", 4, 6, out _));
        Assert.Equal("ab**cdef**", Clear("**abcdef**", 0, 4, out _));
        Assert.Equal("**abcd**ef", Clear("**abcdef**", 6, 10, out _));
    }

    [Fact]
    public void AtTheCaret_TheSpansAroundIt() =>
        Assert.Equal("x abc **y**", Clear("x **abc** **y**", 4, 4, out _));

    [Fact]
    public void ALink_GoesOnlyAsAWhole() =>
        Assert.Equal("a link b", Clear("a [link](http://x.pl) b", 3, 5, out _));
}

public class RuleTests
{
    [Theory]
    [InlineData("tekst\n\n---\n", 2, true)]
    [InlineData("tekst\n---\n", 1, false)]   // a setext heading underline
    [InlineData("***\n", 0, true)]
    [InlineData("---\ntitle: x\n---\n\ntekst\n", 0, false)] // front matter
    [InlineData("- a\n---\n", 1, true)]
    public void Rules(string text, int line, bool rule) =>
        Assert.Equal(rule, MarkdownDocument.Parse(text).GetLine(line).Kind == MdLineKind.Rule);
}
