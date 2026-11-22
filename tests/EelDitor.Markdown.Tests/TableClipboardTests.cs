using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class TableClipboardTests
{
    [Fact]
    public void FromWordHtml()
    {
        // The shape Word puts on the clipboard: paragraphs, spans, &nbsp;, bold, a line break.
        const string html = """
            <html><body><!--StartFragment--><table class=MsoTableGrid border=1>
            <tr><td width=100><p class=MsoNormal><b><span lang=PL>Słowo</span></b><o:p></o:p></p></td>
                <td><p class=MsoNormal>Znaczenie&nbsp;1</p><p class=MsoNormal>druga linia</p></td></tr>
            <tr><td><p>a | b</p></td><td><p><i>kursywa</i> i tekst</p></td></tr>
            </table><!--EndFragment--></body></html>
            """;
        var rows = TableClipboard.FromHtml(html)!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(["**Słowo**", "Znaczenie 1\ndruga linia"], rows[0]);
        Assert.Equal(["a | b", "*kursywa* i tekst"], rows[1]);
        Assert.Equal("| a \\| b | *kursywa* i tekst |", TableClipboard.Row(rows[1], 2));
        Assert.Equal("| Znaczenie 1<br>druga linia |  |  |", TableClipboard.Row([rows[0][1]], 3));
    }

    [Fact]
    public void FromExcelTsv_AndMarkdownRows()
    {
        Assert.Equal([["a", "b"], ["1", "2"]], TableClipboard.FromTsv("a\tb\r\n1\t2\r\n")!);
        Assert.Null(TableClipboard.FromTsv("zwykły tekst"));
        Assert.Equal(["| a | b |", "| 1 | 2 |"], TableClipboard.MarkdownRows("| a | b |\n|---|---|\n| 1 | 2 |\n")!);
        Assert.Null(TableClipboard.MarkdownRows("| a |\nnie wiersz"));
    }

    [Theory]
    [InlineData("<table><tr><td>a</td></tr></table>", false)]                       // Word: no header
    [InlineData("<table><tr><th>a</th></tr><tr><td>b</td></tr></table>", true)]
    [InlineData("<table><thead><tr><td>a</td></tr></thead></table>", true)]
    public void HeaderOnlyWhenMarked(string html, bool header) => Assert.Equal(header, TableClipboard.HasHeader(html));

    [Fact]
    public void ExtraCells_GoToTheLastColumn() =>
        Assert.Equal("| a | b c |", TableClipboard.Row(["a", "b", "c"], 2));

    [Fact]
    public void WindowsHtml_OffsetsPointAtTheFragment()
    {
        string fragment = TableClipboard.HtmlTable([["<b>zażółć</b>", "x"]], header: true);
        byte[] data = TableClipboard.WindowsHtml(fragment);
        string text = System.Text.Encoding.UTF8.GetString(data);
        int Offset(string name) => int.Parse(System.Text.RegularExpressions.Regex.Match(text, name + @":(\d+)").Groups[1].Value);
        int start = Offset("StartFragment"), end = Offset("EndFragment");
        Assert.Equal(fragment, System.Text.Encoding.UTF8.GetString(data, start, end - start));
        Assert.Equal(data.Length, Offset("EndHTML"));
        Assert.Contains("<th", TableClipboard.HtmlFromClipboard(data));
        Assert.Equal([["**zażółć**", "x"]], TableClipboard.FromHtml(TableClipboard.HtmlFromClipboard(data))!);
    }
}
