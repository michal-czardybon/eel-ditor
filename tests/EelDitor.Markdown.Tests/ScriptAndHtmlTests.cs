using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class ScriptTests
{
    private static LineRender Line(string text) => MarkdownDocument.Parse(text).GetLine(0);

    [Theory]
    [InlineData("H~2~O", "H2O", MdStyle.Subscript, "2")]
    [InlineData("x^2^ + y", "x2 + y", MdStyle.Superscript, "2")]
    [InlineData("a <sub>low</sub> b", "a low b", MdStyle.Subscript, "low")]
    [InlineData("a <SUP>up</SUP>", "a up", MdStyle.Superscript, "up")]
    [InlineData("<del>gone</del>", "gone", MdStyle.Strike, "gone")]
    [InlineData("<mark>m</mark>", "m", MdStyle.Highlight, "m")]
    [InlineData("~~strike~~", "strike", MdStyle.Strike, "strike")]
    public void Styles(string line, string shown, MdStyle style, string styled)
    {
        var r = Line(line);
        Assert.Equal(shown, r.Display(line));
        var s = Assert.Single(r.Styles, s => s.Style.HasFlag(style));
        Assert.Equal(styled, line[s.Start..s.End]);
    }

    [Theory]
    [InlineData("a ~ b ~ c")]        // spaces: not a subscript
    [InlineData("~/path/ and more")] // no closing marker
    [InlineData("2^10 is 1024")]
    [InlineData("`H~2~O`")]          // inside code
    public void NotScripts(string line) =>
        Assert.DoesNotContain(Line(line).Styles, s => (s.Style & (MdStyle.Subscript | MdStyle.Superscript)) != 0);

    [Fact]
    public void StrikeAndSubscript_Together() =>
        Assert.Equal("gone H2O", Line("~~gone~~ H~2~O").Display("~~gone~~ H~2~O"));

    [Theory]
    [InlineData("a **(5; chamber)**<br>b", "a (5; chamber)<br>b")]   // ")" then "<": both punctuation in CommonMark
    [InlineData("x **(y)**=z", "x (y)=z")]
    [InlineData("**[a]**+", "[a]+")]
    public void EmphasisNextToSymbols(string line, string shown) => Assert.Equal(shown, Line(line).Display(line));
}

public class MarkdownHtmlTests
{
    private static string Html(string md) => MarkdownHtml.Body(md);

    [Fact]
    public void HeadingsParagraphsAndInlineStyles()
    {
        string html = Html("# Tytuł\n\nTekst **gruby** i *pochyły*,\ndruga linia ~~skreślona~~ H~2~O x^2^ ==ważne== `kod`.\n\n---\n");
        Assert.Contains("<h1>Tytuł</h1>", html);
        Assert.Contains("<p>Tekst <strong>gruby</strong> i <em>pochyły</em>,\ndruga linia <del>skreślona</del> H<sub>2</sub>O x<sup>2</sup> <mark>ważne</mark> <code>kod</code>.</p>", html);
        Assert.Contains("<hr>", html);
    }

    [Fact]
    public void Lists_NestedAndTasks()
    {
        string html = Html("- jeden\n  - pod\n- [x] zrobione\n- [ ] do zrobienia\n\n1. pierwszy\n2. drugi\n");
        Assert.Contains("<ul>\n<li>jeden\n<ul>\n<li>pod</li>\n</ul>\n</li>", html);
        Assert.Contains("<li class=\"task done\"><span class=\"box\">☑</span>zrobione</li>", html);
        Assert.Contains("<li class=\"task\"><span class=\"box\">☐</span>do zrobienia</li>", html);
        Assert.Contains("<ol>\n<li>pierwszy</li>\n<li>drugi</li>\n</ol>", html);
    }

    [Fact]
    public void Tables_WithAlignmentAndBr()
    {
        string html = Html("| a | b |\n|:--|--:|\n| x<br>y | **z** |\n");
        Assert.Contains("<thead><tr><th style=\"text-align:left\">a</th><th style=\"text-align:right\">b</th></tr></thead>", html);
        Assert.Contains("<td style=\"text-align:left\">x<br>y</td><td style=\"text-align:right\"><strong>z</strong></td>", html);
    }

    [Fact]
    public void CodeQuotesLinksImagesFootnotes()
    {
        string html = Html("```cs\nif (a < b) {}\n```\n\n> cytat **ważny**\n> dalej\n\n[link](http://x.pl) ![kot](img/kot.png) przypis[^1]\n\n[^1]: Treść przypisu.\n");
        Assert.Contains("<pre><code class=\"language-cs\">if (a &lt; b) {}\n</code></pre>", html);
        Assert.Contains("<blockquote>\n<p>cytat <strong>ważny</strong>\ndalej</p>\n</blockquote>", html);
        Assert.Contains("<a href=\"http://x.pl\">link</a>", html);
        Assert.Contains("<img src=\"img/kot.png\" alt=\"kot\">", html);
        Assert.Contains("<sup><a href=\"#fn-1\">1</a></sup>", html);
        Assert.Contains("<li id=\"fn-1\">Treść przypisu.</li>", html);
    }

    [Fact]
    public void RawHtml_IsShownAsText_AndThePageLoadsNothingFromTheNetwork()
    {
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", Html("<script>alert(1)</script>"));
        string page = MarkdownHtml.Page("t", "", "file:///C:/x/");
        Assert.Contains("default-src 'none'; img-src file: data:", page);
        Assert.Contains("<base href=\"file:///C:/x/\">", page);
    }
}
