using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class StyleConversionTests
{
    private static string Convert(string line, MdStyle from, MdStyle to, out int converted, out int skipped) =>
        StyleConversion.Convert(line, from, to, 0, line.Length, InlineStyleEdit.LineParser, out converted, out skipped);

    [Theory]
    [InlineData("a ==b== c ==d==", MdStyle.Highlight, MdStyle.Bold, "a **b** c **d**", 2)]
    [InlineData("a **b** __c__", MdStyle.Bold, MdStyle.Italic, "a *b* _c_", 2)]
    [InlineData("*a* i _b_", MdStyle.Italic, MdStyle.Highlight, "==a== i ==b==", 2)]
    [InlineData("**gruby *i pochyły* tekst**", MdStyle.Italic, MdStyle.Highlight, "**gruby ==i pochyły== tekst**", 1)]
    [InlineData("# Tytuł z ==ważnym==", MdStyle.Highlight, MdStyle.Italic, "# Tytuł z *ważnym*", 1)]
    [InlineData("`==kod==` i ==tekst==", MdStyle.Highlight, MdStyle.Bold, "`==kod==` i **tekst**", 1)]
    public void Converts(string line, MdStyle from, MdStyle to, string expected, int count)
    {
        Assert.Equal(expected, Convert(line, from, to, out int converted, out _));
        Assert.Equal(count, converted);
    }

    [Fact]
    public void BoldItalicTogether_OnlyTheBoldChanges()
    {
        // ***x*** is bold and italic; converting bold to highlight leaves the italic.
        string result = Convert("***x***", MdStyle.Bold, MdStyle.Highlight, out int converted, out _);
        var (text, styles) = InlineStyleEdit.Visible(result, InlineStyleEdit.LineParser(result)!);
        Assert.Equal("x", text);
        Assert.Equal(MdStyle.Highlight | MdStyle.Italic, styles[0]);
        Assert.Equal(1, converted);
    }

    [Fact]
    public void OnlyTheSelectedSpans()
    {
        string line = "==a== ==b==";
        Assert.Equal("==a== **b**", StyleConversion.Convert(line, MdStyle.Highlight, MdStyle.Bold, 8, 9, InlineStyleEdit.LineParser, out _, out _));
    }

    [Fact]
    public void InATableRow_TheRowStaysTheSame()
    {
        var parse = InlineStyleEdit.TableRowParser("| a | b |", "|---|---|", false, 2);
        Assert.Equal("| **x** | y |", StyleConversion.Convert("| ==x== | y |", MdStyle.Highlight, MdStyle.Bold, 0, 13, parse, out int c, out _));
        Assert.Equal(1, c);
    }
}
