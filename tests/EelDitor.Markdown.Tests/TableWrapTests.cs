using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class TableWrapTests
{
    [Fact]
    public void NaturalWidths_WhenTheyFit() => Assert.Equal([3, 10], TableWrap.ColumnWidths([3, 10], 40));

    [Fact]
    public void NarrowColumnsKeepTheirWidth_WideOnesShareTheRest()
    {
        // 3 columns: borders take 10, content 50.
        var w = TableWrap.ColumnWidths([5, 100, 100], 60);
        Assert.Equal(5, w[0]);
        Assert.Equal(45, w[1] + w[2]);
        Assert.True(Math.Abs(w[1] - w[2]) <= 1);
        Assert.Equal(60, TableWrap.GridWidth(w));
    }

    [Fact]
    public void VeryNarrowWindow_UsesTheMinimumWidth() =>
        Assert.All(TableWrap.ColumnWidths([50, 50, 50], 10), w => Assert.Equal(TableWrap.MinColumnWidth, w));

    [Fact]
    public void WrapCell_BreaksAtSpaces_AndCutsLongWords()
    {
        Assert.Equal(["ala ma", "kota i", "psa"], TableWrap.WrapCell("ala ma kota i psa", 6));
        Assert.Equal(["https:", "//x.pl", "/abc"], TableWrap.WrapCell("https://x.pl/abc", 6));
        Assert.Equal([""], TableWrap.WrapCell("", 6));
    }

    [Fact]
    public void CellText_HidesMarkup()
    {
        var doc = MarkdownDocument.Parse("| a | **gruby** `kod` |\n|---|---|\n| 1 | 2 |");
        var row = doc.Tables[0].Rows[0];
        Assert.Equal("gruby kod", TableWrap.CellText("| a | **gruby** `kod` |", row.Cells[1], doc.GetLine(0)));
        Assert.Equal([1, 9], TableWrap.NaturalWidths(doc.Tables[0], new StringLineSource("| a | **gruby** `kod` |\n|---|---|\n| 1 | 2 |"), doc.GetLine));
    }

    [Fact]
    public void BorderPositions()
    {
        int[] w = [3, 5];
        Assert.Equal(0, TableWrap.BorderAt(w, 0));
        Assert.Equal(6, TableWrap.BorderAt(w, 1));
        Assert.Equal(14, TableWrap.BorderAt(w, 2));
        Assert.Equal(15, TableWrap.GridWidth(w));
    }
}

public class TableWrapStyleTests
{
    [Fact]
    public void StyledCellText_KeepsTheStyleOfEveryCharacter()
    {
        string t = "| a | b |\n|---|---|\n| x **bold** `c` |  |";
        var doc = MarkdownDocument.Parse(t);
        var line = new StringLineSource(t).GetLine(2);
        var (text, styles) = TableWrap.StyledCellText(line, doc.Tables[0].Rows[2].Cells[0], doc.GetLine(2));
        Assert.Equal("x bold c", text);
        Assert.Equal(MdStyle.None, styles[0]);
        Assert.True(styles[2].HasFlag(MdStyle.Bold));
        Assert.True(styles[7].HasFlag(MdStyle.Code));
    }

    [Fact]
    public void WrapRanges_PointIntoTheText()
    {
        var r = TableWrap.WrapRanges("ala ma  kota", 6);
        Assert.Equal([(0, 6), (8, 4)], r);
    }

    [Theory]
    [InlineData("<br>", 4)]
    [InlineData("<BR>", 4)]
    [InlineData("<br/>", 5)]
    [InlineData("<br />", 6)]
    [InlineData("<b>", 0)]
    [InlineData("<br", 0)]
    [InlineData("<bra>", 0)]
    public void BreakTags_AreRecognized(string tag, int length) => Assert.Equal(length, InlineParser.BreakTagLength(tag, 0, tag.Length));

    [Fact]
    public void BrInACell_IsANewLine_ButNotInsideCode()
    {
        string line = "| jeden <br> **dwa**<br/>trzy | `x<br>y` |";
        var md = MarkdownDocument.Parse("| a | b |\n|---|---|\n" + line);
        var row = md.Tables[0].Rows[2];
        var render = md.GetLine(2);
        var (text, styles) = TableWrap.StyledCellText(line, row.Cells[0], render);
        Assert.Equal("jeden\ndwa\ntrzy", text);
        Assert.Equal(MdStyle.Bold, styles[text.IndexOf("dwa", StringComparison.Ordinal)]);
        Assert.Equal("x<br>y", TableWrap.CellText(line, row.Cells[1], render));
        Assert.Equal([5, 6], TableWrap.NaturalWidths(md.Tables[0], new StringLineSource("| a | b |\n|---|---|\n" + line), md.GetLine));
        Assert.Equal(["jeden", "dwa", "trzy"], TableWrap.WrapCell(text, 10));
        Assert.Equal(["ab", "cd", "", "e"], TableWrap.WrapCell("ab cd\n\ne", 2));
    }
}
