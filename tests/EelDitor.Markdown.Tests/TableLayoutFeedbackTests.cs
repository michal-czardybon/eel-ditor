using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

/// <summary>How grid columns share the width, how wide emoji are, and styles over a &lt;br&gt; in the cell editor.</summary>
public class TableLayoutFeedbackTests
{
    private static TableWrap.ColumnText[] Columns(string table)
    {
        var doc = MarkdownDocument.Parse(table);
        return TableWrap.ColumnTexts(doc.Tables[0], new StringLineSource(table), doc.GetLine);
    }

    private static string Words(int count, string word = "słowo") => string.Join(" ", Enumerable.Repeat(word, count));

    [Fact]
    public void AColumnWithMoreText_GetsMoreRoom()
    {
        // A short id, a column of short notes and a column of long descriptions: equal shares made the descriptions
        // tall and left the notes half empty.
        string table = "| id | uwagi | opis |\n|---|---|---|\n" + string.Concat(Enumerable.Range(1, 6).Select(i =>
            $"| {i} | {Words(5)} | {Words(30)} |\n"));
        var widths = TableWrap.ColumnWidths(Columns(table), 100);
        Assert.Equal(100, TableWrap.GridWidth(widths));
        Assert.Equal(2, widths[0]);                       // a narrow column keeps its width
        Assert.True(widths[2] > widths[1] * 1.5, string.Join(",", widths));
        Assert.True(widths[1] >= 6, string.Join(",", widths));  // still at least its longest word
    }

    [Fact]
    public void ColumnsThatFit_KeepTheirNaturalWidths()
    {
        string table = "| a | bb |\n|---|---|\n| ccc | d |\n";
        Assert.Equal([3, 2], TableWrap.ColumnWidths(Columns(table), 40));
    }

    [Fact]
    public void SharingTheWidth_NeverMakesTheTableTaller_ThanEqualShares()
    {
        var random = new Random(7);
        for (int run = 0; run < 50; run++)
        {
            int columns = random.Next(2, 6), rows = random.Next(1, 12);
            var sb = new System.Text.StringBuilder("|" + string.Concat(Enumerable.Repeat(" h |", columns)) + "\n|" + string.Concat(Enumerable.Repeat("---|", columns)) + "\n");
            for (int r = 0; r < rows; r++)
                sb.Append('|').Append(string.Concat(Enumerable.Range(0, columns).Select(_ => " " + Words(random.Next(0, 25), "ab" + new string('c', random.Next(0, 6))) + " |"))).Append('\n');
            string table = sb.ToString();
            var doc = MarkdownDocument.Parse(table);
            var t = doc.Tables[0];
            int available = random.Next(40, 140);
            var shared = TableWrap.ColumnWidths(Columns(table), available);
            var equal = TableWrap.ColumnWidths(TableWrap.NaturalWidths(t, new StringLineSource(table), doc.GetLine), available);
            Assert.True(TableWrap.GridWidth(shared) <= available || shared.All(w => w <= TableWrap.MinColumnWidth), table);
            int Height(int[] widths) => t.Rows.Where(r => r.Kind != MdLineKind.TableSeparator).Sum(r =>
                Enumerable.Range(0, columns).Max(c => TableWrap.WrapCell(TableWrap.CellText(table.Split('\n')[r.LineIndex], r.Cells[c], doc.GetLine(r.LineIndex)), widths[c]).Count));
            Assert.True(Height(shared) <= Height(equal) + 1, $"{Height(shared)} > {Height(equal)}: {table}");
        }
    }

    [Theory]
    [InlineData("✅", 2)]
    [InlineData("❌", 2)]
    [InlineData("⭐", 2)]
    [InlineData("✓", 1)]
    [InlineData("a", 1)]
    [InlineData("界", 2)]
    public void EmojiShownAsEmoji_AreTwoColumnsWide(string text, int width) => Assert.Equal(width, TextWidth.Of(text));

    [Fact]
    public void ASymbolDrawnWide_CountsAsMeasuredByTheApp()
    {
        // ✔ is one column by Unicode, but the emoji font that draws it makes it two: counted as one, a table shrank it.
        var before = TextWidth.Measured;
        try
        {
            TextWidth.Measured = cp => cp == 0x2714 ? 2 : null;
            Assert.Equal(2, TextWidth.Of("✔"));
            Assert.Equal(1, TextWidth.Of("✓"));
            Assert.Equal(1, TextWidth.Of("a"));       // below U+2000: never asked
        }
        finally { TextWidth.Measured = before; }
    }

    [Fact]
    public void StylesOverABr_AreSplitForTheCellEditor_AndJoinedAgain()
    {
        const string line = "| x | **a ==jeden<br>dwa== b** | y |";
        var doc = MarkdownDocument.Parse("| h | h | h |\n|---|---|---|\n" + line);
        var cell = doc.Tables[0].Rows[2].Cells[1];
        var (text, splits) = TableCellEdit.CellEditorText(line, cell, doc.GetLine(2));
        Assert.Equal("**a ==jeden==**\n**==dwa== b**", text);
        Assert.Equal("**a ==jeden\ndwa== b**", TableCellEdit.JoinSplitStyles(text, splits));
        // An edited marker at the break is left as the user made it.
        string edited = text.Replace("==**\n", "**\n");
        Assert.Equal(edited, TableCellEdit.JoinSplitStyles(edited, splits.Select(s => s with { Newline = s.Newline - 2 })));
    }

    [Fact]
    public void ACellWithoutStylesOverABr_IsJustSplitIntoLines()
    {
        const string line = "| a <br/> b |";
        var doc = MarkdownDocument.Parse("| h |\n|---|\n" + line);
        var (text, splits) = TableCellEdit.CellEditorText(line, doc.Tables[0].Rows[2].Cells[0], doc.GetLine(2));
        Assert.Equal("a\nb", text);
        Assert.Empty(splits);
    }
}
