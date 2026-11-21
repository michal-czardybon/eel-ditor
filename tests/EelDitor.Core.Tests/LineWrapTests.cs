using EelDitor.Core.Text;

namespace EelDitor.Core.Tests;

public class LineWrapTests
{
    private static readonly string Long = string.Join(' ', Enumerable.Repeat("słowo", 40)); // 239 characters

    [Fact]
    public void WrapsAtSpaces_WithinTheWidth_KeepingLineBreakStyle()
    {
        string text = "krótki\r\n" + Long + "\r\nkoniec";
        string wrapped = LineWrap.Wrap(text, 80, markdown: false, "\r\n");
        var lines = wrapped.Split("\r\n");
        Assert.Equal("krótki", lines[0]);
        Assert.Equal("koniec", lines[^1]);
        Assert.All(lines, l => Assert.True(l.Length <= 80, l));
        Assert.Equal(Long, string.Join(' ', lines[1..^1]));
        Assert.DoesNotContain("\n\n", wrapped.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Markdown_KeepsStructure()
    {
        string table = "| a | b |" + new string('x', 200) + " |";
        string text = string.Join('\n',
            "# " + Long,
            "- " + Long,
            "> " + Long,
            "```",
            Long,
            "```",
            table,
            "    " + Long,
            Long + "  ");
        var lines = LineWrap.Wrap(text, 80, markdown: true, "\n").Split('\n');
        Assert.Equal("# " + Long, lines[0]);                         // heading untouched
        Assert.StartsWith("- słowo", lines[1]);
        Assert.StartsWith("  słowo", lines[2]);                      // list continuation is indented
        int quote = Array.FindIndex(lines, l => l.StartsWith("> "));
        Assert.StartsWith("> słowo", lines[quote + 1]);              // quote continues as a quote
        Assert.Contains(Long, lines);                                // inside the code fence
        Assert.Contains(table, lines);
        Assert.Contains("    " + Long, lines);                       // indented code
        Assert.EndsWith("słowo  ", lines[^1]);                       // hard break kept at the end
        Assert.All(lines.Where(l => !l.StartsWith('#') && !l.StartsWith('|') && !l.StartsWith("    ") && l != Long),
            l => Assert.True(l.TrimEnd().Length <= 80, l));
    }

    [Fact]
    public void NeverCutsALongWord()
    {
        string url = "https://example.com/" + new string('a', 150);
        string wrapped = LineWrap.Wrap("zobacz " + url + " teraz", 80, markdown: true, "\n");
        Assert.Equal(["zobacz", url, "teraz"], wrapped.Split('\n'));
        Assert.Equal(url.Length, LineWrap.LongestLine(wrapped));
    }
}
