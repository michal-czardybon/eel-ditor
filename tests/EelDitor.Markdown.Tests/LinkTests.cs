using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

/// <summary>Bare web addresses and autolinks (display only: the text is never changed).</summary>
public class LinkTests
{
    private static LineRender Render(string line) => MarkdownDocument.Parse(line).GetLine(0);

    [Theory]
    [InlineData("see https://example.org/a.", "https://example.org/a", "https://example.org/a")]
    [InlineData("(https://en.wikipedia.org/wiki/Eel_(fish))", "https://en.wikipedia.org/wiki/Eel_(fish)", "https://en.wikipedia.org/wiki/Eel_(fish)")]
    [InlineData("go to www.example.org, now", "www.example.org", "https://www.example.org")]
    [InlineData("HTTP://X.ORG?", "HTTP://X.ORG", "HTTP://X.ORG")]
    [InlineData("\"http://a.b/c\"", "http://a.b/c", "http://a.b/c")]
    [InlineData("write mailto:a@b.org!", "mailto:a@b.org", "mailto:a@b.org")]
    public void FindsBareAddresses(string text, string shown, string url)
    {
        var found = Assert.Single(UrlFinder.FindAll(text));
        Assert.Equal(shown, text[found.Start..found.End]);
        Assert.Equal(url, found.Url);
    }

    [Theory]
    [InlineData("xhttps://example.org")]
    [InlineData("https://")]
    [InlineData("www.example")]
    [InlineData("a/www.example.org")]
    [InlineData("https:// space")]
    public void IgnoresNonAddresses(string text) => Assert.Empty(UrlFinder.FindAll(text));

    [Fact]
    public void BareUrl_IsALink_AndUnderscoresInsideAreNotEmphasis()
    {
        const string line = "see https://x.org/a_b_c here";
        var r = Render(line);
        var s = Assert.Single(r.Styles);
        Assert.Equal(MdStyle.Link, s.Style);
        Assert.Equal("https://x.org/a_b_c", line[s.Start..s.End]);
        Assert.Equal("https://x.org/a_b_c", s.Url);
        Assert.Empty(r.Hidden);
    }

    [Theory]
    [InlineData("see https://x.org here")]
    [InlineData("see www.x.org here")]
    [InlineData("mailto:a@b.org")]
    public void LineWithNoMarkupCharacters_StillGetsItsLink(string line) =>
        Assert.Contains(Render(line).Styles, s => s.Style == MdStyle.Link);

    [Fact]
    public void BoldBareUrl_KeepsTheBoldOutsideTheAddress()
    {
        const string line = "**https://x.org**";
        var r = Render(line);
        Assert.Equal("https://x.org", r.Display(line));
        var link = Assert.Single(r.Styles, s => s.Style.HasFlag(MdStyle.Link));
        Assert.Equal("https://x.org", link.Url);
    }

    [Theory]
    [InlineData("a <https://x.org/p> b", "a https://x.org/p b", "https://x.org/p")]
    [InlineData("<jan@example.org>", "jan@example.org", "mailto:jan@example.org")]
    public void Autolink_HidesTheBrackets(string line, string display, string url)
    {
        var r = Render(line);
        Assert.Equal(display, r.Display(line));
        Assert.Equal(url, Assert.Single(r.Styles, s => s.Style.HasFlag(MdStyle.Link)).Url);
    }

    [Theory]
    [InlineData("a <b> c")]
    [InlineData("x < y > z")]
    [InlineData("`https://x.org`")]
    public void NotLinks(string line) => Assert.DoesNotContain(Render(line).Styles, s => s.Style.HasFlag(MdStyle.Link));

    [Fact]
    public void MarkdownLink_WithAnAddressAsText_IsOneLink()
    {
        const string line = "[https://a.org](https://b.org)";
        var link = Assert.Single(Render(line).Styles);
        Assert.Equal("https://b.org", link.Url);
    }
}

public class RenumberListTests
{
    private static string[] L(string text) => text.Split('\n');

    private static string Apply(string text, int line)
    {
        var lines = L(text);
        foreach (var (i, t) in ListEdit.RenumberList(lines, line)!) lines[i] = t;
        return string.Join("\n", lines);
    }

    [Fact]
    public void NumbersFromOne_WithoutGaps() =>
        Assert.Equal("1. a\n2. b\n3. c\n4. d", Apply("3. a\n4. b\n7. c\n7. d", 2));

    [Fact]
    public void KeepsTheDelimiterAndIndent_AndNestedListsKeepTheirNumbers() =>
        Assert.Equal("  1) a\n     5. x\n     9. y\n  2) b", Apply("  2) a\n     5. x\n     9. y\n  2) b", 0));

    [Fact]
    public void ContinuationLines_AndLooseLists()
    {
        Assert.Equal("1. a\n   more\n\n2. b\n3. c", Apply("1. a\n   more\n\n5. b\n1. c", 1));
    }

    [Fact]
    public void InANestedList_NumbersOnlyThatList() =>
        Assert.Equal("1. a\n   1. x\n   2. y\n1. b", Apply("1. a\n   3. x\n   3. y\n1. b", 2));

    [Fact]
    public void AlreadyNumbered_ChangesNothing() => Assert.Empty(ListEdit.RenumberList(L("1. a\n2. b"), 0)!);

    [Theory]
    [InlineData("- a\n- b")]
    [InlineData("text")]
    public void NotAnOrderedList(string text) => Assert.Null(ListEdit.RenumberList(L(text), 0));
}
