using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class InlineTests
{
    private static string Display(string line) => MarkdownDocument.Parse(line).GetLine(0).Display(line);

    private static LineRender Render(string line) => MarkdownDocument.Parse(line).GetLine(0);

    [Theory]
    [InlineData("a **bold** b", "a bold b")]
    [InlineData("a __bold__ b", "a bold b")]
    [InlineData("*it* and _it_", "it and it")]
    [InlineData("***both***", "both")]
    [InlineData("~~gone~~", "gone")]
    [InlineData("use `a*b*c` here", "use a*b*c here")]
    [InlineData("see [docs](http://x/y_z) now", "see docs now")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("**unclosed", "**unclosed")]
    [InlineData("\\*escaped\\*", "*escaped*")]
    [InlineData("a \\| b and C:\\path", "a | b and C:\\path")]
    [InlineData("# Title", "Title")]
    [InlineData("### Title ###", "Title")]
    [InlineData("#hashtag", "#hashtag")]
    public void HidesMarkup(string line, string expected) => Assert.Equal(expected, Display(line));

    [Fact]
    public void BoldContentIsStyled()
    {
        var r = Render("a **bold** b");
        var s = Assert.Single(r.Styles);
        Assert.Equal((4, 8, MdStyle.Bold), (s.Start, s.End, s.Style));
    }

    [Fact]
    public void LinkKeepsUrl()
    {
        var r = Render("[t](http://a)");
        Assert.Equal("http://a", Assert.Single(r.Styles).Url);
    }

    [Fact]
    public void CodeFenceContentIsNotStyled()
    {
        var doc = MarkdownDocument.Parse("```\n**x**\n```\n**y**");
        Assert.Empty(doc.GetLine(1).Hidden);
        Assert.Equal(MdLineKind.Code, doc.GetLine(1).Kind);
        Assert.NotEmpty(doc.GetLine(3).Hidden);
    }

    [Fact]
    public void ListBulletIsNotEmphasis() => Assert.Equal("* item x", Display("* item *x*"));
}

public class TableDisplayTests
{
    private static string[] Display(string text)
    {
        var src = new StringLineSource(text);
        var doc = MarkdownDocument.Parse(src);
        return Enumerable.Range(0, src.LineCount).Select(i => doc.GetLine(i).Display(src.GetLine(i))).ToArray();
    }

    [Fact]
    public void AlignsColumnsWithoutTouchingSource()
    {
        var lines = Display("| a | bbbbb |\n|---|---|\n| cccc | d |");
        Assert.Equal(
        [
            "| a    | bbbbb |",
            "|------|-------|",
            "| cccc | d     |",
        ], lines);
    }

    [Fact]
    public void HiddenBoldDoesNotCountTowardsWidth()
    {
        var lines = Display("| **x** | y |\n|-|-|\n| zz | w |");
        // header cells are shown bold without the markers
        Assert.Equal("| x  | y |", lines[0]);
        Assert.Equal("| zz | w |", lines[2]);
        Assert.Equal(lines[0].Length, lines[1].Length);
    }

    [Fact]
    public void RightAndCenterAlignment()
    {
        var lines = Display("| h | hhhhh |\n|--:|:-:|\n| longer | x |");
        Assert.Equal("|      h | hhhhh |", lines[0]);
        Assert.Equal("| longer |   x   |", lines[2]);
    }

    [Fact]
    public void TableWithoutOuterPipes()
    {
        var lines = Display("a | bbb\n--|--\nccc | d");
        Assert.Equal("a   | bbb", lines[0]);
        Assert.Equal("ccc | d", lines[2]);
    }

    [Fact]
    public void WideCharactersCountDouble()
    {
        var lines = Display("| 日本 | x |\n|---|---|\n| abcd | y |");
        Assert.Equal("| 日本 | x |", lines[0]);
        Assert.Equal("| abcd | y |", lines[2]);
    }

    [Fact]
    public void PipeInsideCodeSpanIsNotASeparator()
    {
        var doc = MarkdownDocument.Parse("| `a|b` | c |\n|---|---|");
        Assert.Equal(2, doc.Tables[0].Header.Cells.Count);
    }

    [Fact]
    public void NotATableWithoutSeparator()
    {
        var doc = MarkdownDocument.Parse("a | b\nc | d");
        Assert.Empty(doc.Tables);
    }

    [Fact]
    public void TableEndsAtBlankLine()
    {
        var doc = MarkdownDocument.Parse("|a|\n|-|\n|b|\n\n|c|");
        Assert.Equal(3, doc.Tables[0].Rows.Count);
    }
}

public class TableEditTests
{
    /// <summary>Applies edits like the editor does: bottom-up so offsets stay valid.</summary>
    private static string Apply(string text, IReadOnlyList<TableEdit> edits)
    {
        var lines = text.Split('\n').ToList();
        foreach (var e in edits.OrderByDescending(e => e.Line).ThenByDescending(e => e.Column))
        {
            switch (e.Kind)
            {
                case TableEditKind.Replace:
                    lines[e.Line] = lines[e.Line].Remove(e.Column, e.Length).Insert(e.Column, e.Text);
                    break;
                case TableEditKind.InsertLineAfter: lines.Insert(e.Line + 1, e.Text); break;
                case TableEditKind.InsertLineBefore: lines.Insert(e.Line, e.Text); break;
                case TableEditKind.DeleteLine: lines.RemoveAt(e.Line); break;
            }
        }
        return string.Join('\n', lines);
    }

    private const string Table = "| a | bb |\n|---|----|\n| 1 | 2 |\n| 3 | 4 |";

    private static (MarkdownTable, StringLineSource) Parse(string text)
    {
        var src = new StringLineSource(text);
        return (MarkdownDocument.Parse(src).Tables[0], src);
    }

    [Fact]
    public void InsertColumnRight_AddsOneCellPerRow_LeavesOtherCellsUntouched()
    {
        var (t, src) = Parse(Table);
        var result = Apply(Table, TableEditor.InsertColumn(t, src, 0, right: true));
        Assert.Equal("| a |   | bb |\n|---| --- |----|\n| 1 |   | 2 |\n| 3 |   | 4 |", result);
        Assert.Single(MarkdownDocument.Parse(result).Tables);
        Assert.Equal(3, MarkdownDocument.Parse(result).Tables[0].ColumnCount);
    }

    [Fact]
    public void InsertColumnLeftOfFirst()
    {
        var (t, src) = Parse(Table);
        var result = Apply(Table, TableEditor.InsertColumn(t, src, 0, right: false));
        Assert.StartsWith("|   | a | bb |", result);
        Assert.Equal(3, MarkdownDocument.Parse(result).Tables[0].ColumnCount);
    }

    [Fact]
    public void DeleteColumn()
    {
        var (t, _) = Parse(Table);
        Assert.Equal("| bb |\n|----|\n| 2 |\n| 4 |", Apply(Table, TableEditor.DeleteColumn(t, 0)));
    }

    [Theory]
    [InlineData(0, "b", "2")]
    [InlineData(1, "a", "1")]
    public void DeleteColumnWithoutOuterPipes_KeepsTheRemainingColumnInATable(int column, string header, string body)
    {
        const string source = "a | b\n--- | ---\n1 | 2";
        var (table, _) = Parse(source);
        var result = Apply(source, TableEditor.DeleteColumn(table, column));
        var remaining = Assert.Single(MarkdownDocument.Parse(result).Tables);
        Assert.Equal(1, remaining.ColumnCount);
        Assert.Equal(3, remaining.Rows.Count);
        Assert.Contains(header, result.Split('\n')[0]);
        Assert.Contains(body, result.Split('\n')[2]);
    }

    [Fact]
    public void InsertAndDeleteRow()
    {
        var (t, src) = Parse(Table);
        var inserted = Apply(Table, TableEditor.InsertRow(t, src, 2, below: true));
        Assert.Equal("| a | bb |\n|---|----|\n| 1 | 2 |\n|   |   |\n| 3 | 4 |", inserted);
        Assert.Equal("| a | bb |\n|---|----|\n| 3 | 4 |", Apply(Table, TableEditor.DeleteRow(t, 2)));
        Assert.Empty(TableEditor.DeleteRow(t, 0));
    }

    [Fact]
    public void InsertRowAboveHeader_GoesBelowSeparator()
    {
        var (t, src) = Parse(Table);
        var result = Apply(Table, TableEditor.InsertRow(t, src, 0, below: false));
        Assert.Equal("|   |   |", result.Split('\n')[2]);
    }

    [Fact]
    public void MoveRowAndColumn()
    {
        var (t, src) = Parse(Table);
        Assert.Equal("| a | bb |\n|---|----|\n| 3 | 4 |\n| 1 | 2 |", Apply(Table, TableEditor.MoveRow(t, src, 2, down: true)));
        Assert.Equal("| bb | a |\n|----|---|\n| 2 | 1 |\n| 4 | 3 |", Apply(Table, TableEditor.MoveColumn(t, src, 0, right: true)));
    }

    [Fact]
    public void FormatInSource_PadsPhysically()
    {
        var src = new StringLineSource(Table);
        var doc = MarkdownDocument.Parse(src);
        var result = Apply(Table, TableEditor.FormatInSource(doc.Tables[0], src, doc));
        Assert.Equal("| a | bb |\n|---|----|\n| 1 | 2  |\n| 3 | 4  |", result);
    }

    [Fact]
    public void TabMovesBetweenCells_SkippingSeparator()
    {
        var (t, src) = Parse(Table);
        Assert.Equal((0, 8), TableEditor.AdjacentCell(t, src, 0, 2, forward: true));
        Assert.Equal((2, 3), TableEditor.AdjacentCell(t, src, 0, 8, forward: true));
        Assert.Equal((0, 8), TableEditor.AdjacentCell(t, src, 2, 3, forward: false));
        Assert.Null(TableEditor.AdjacentCell(t, src, 3, 7, forward: true));
    }
}
