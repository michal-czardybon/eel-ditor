using EelDitor.Core.Html;

namespace EelDitor.Core.Tests;

public class HtmlParserTests
{
    /// <summary>A compact form of the tree: elements as name(children), text in quotes.</summary>
    private static string Shape(HtmlNode node) => node switch
    {
        HtmlText t => "\"" + t.Text + "\"",
        HtmlElement e => e.Name + (e.Children.Count == 0 ? "" : "(" + string.Join(" ", e.Children.Select(Shape)) + ")"),
        _ => "?",
    };

    private static string Parse(string html) => string.Join(" ", HtmlParser.Parse(html).Children.Select(Shape));

    [Theory]
    [InlineData("<p>a <b>b</b></p>", "p(\"a \" b(\"b\"))")]
    [InlineData("<P CLASS=x>a</P>", "p(\"a\")")]
    [InlineData("<p>a<p>b", "p(\"a\") p(\"b\")")]
    [InlineData("<p>a<div>b</div>", "p(\"a\") div(\"b\")")]
    [InlineData("<ul><li>a<li>b<ul><li>c</ul><li>d</ul>", "ul(li(\"a\") li(\"b\" ul(li(\"c\"))) li(\"d\"))")]
    [InlineData("<table><tr><td>a<td>b<tr><th>c</table>", "table(tr(td(\"a\") td(\"b\")) tr(th(\"c\")))")]
    [InlineData("<dl><dt>a<dd>b<dt>c</dl>", "dl(dt(\"a\") dd(\"b\") dt(\"c\"))")]
    [InlineData("a<br>b<br/>c</br>d", "\"a\" br \"b\" br \"c\" br \"d\"")]
    [InlineData("<img src=x.png alt='a b'>t", "img \"t\"")]
    [InlineData("x<!-- <b>no</b> -->y", "\"xy\"")]
    [InlineData("<!DOCTYPE html><html><body>x</body></html>", "html(body(\"x\"))")]
    [InlineData("<script>if (a<b) x='</p>';</script>z", "script(\"if (a<b) x='</p>';\") \"z\"")]
    [InlineData("a < b and c<1", "\"a < b and c<1\"")]
    [InlineData("<b>a<i>b</b>c", "b(\"a\" i(\"b\")) \"c\"")]
    [InlineData("</p>x</span>", "\"x\"")]
    public void BuildsTheTree(string html, string shape) => Assert.Equal(shape, Parse(html));

    [Fact]
    public void Entities_AreDecoded_InTextAndAttributes()
    {
        var doc = HtmlParser.Parse("<a href=\"a?x=1&amp;y=2\" title=caf&eacute;>&lt;Zażółć&gt; &#x263A; &copy;&nbsp;x</a>");
        var a = doc.Find("a")!;
        Assert.Equal("a?x=1&y=2", a.Attr("href"));
        Assert.Equal("café", a.Attr("TITLE"));
        Assert.Equal("<Zażółć> ☺ © x", a.InnerText);
    }

    [Theory]
    [InlineData("<input type=checkbox checked disabled>", "checked", "")]
    [InlineData("<td colspan = \"2\" rowspan=3>", "colspan", "2")]
    [InlineData("<img src='a b.png'/>", "src", "a b.png")]
    public void Attributes(string html, string name, string value)
    {
        var e = HtmlParser.Parse(html).Elements.First();
        Assert.Equal(value, e.Attr(name));
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<a")]
    [InlineData("<a href=\"x")]
    [InlineData("<!--")]
    [InlineData("</")]
    [InlineData("<script>")]
    [InlineData("<![CDATA[x")]
    public void TruncatedInput_DoesNotThrow(string html) => HtmlParser.Parse(html);
}
