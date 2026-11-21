using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

/// <summary>Task lists, ==highlight==, footnotes and images.</summary>
public class ExtensionTests
{
    private static LineRender Render(string line) => MarkdownDocument.Parse(line).GetLine(0);

    private static string Display(string line) => Render(line).Display(line);

    [Theory]
    [InlineData("a ==mark== b", "a mark b")]
    [InlineData("x == y", "x == y")]
    [InlineData("if a==b and c==d", "if a==b and c==d")]
    [InlineData("=single=", "=single=")]
    [InlineData("text[^1] more", "text1 more")]
    [InlineData("[^note]: The note.", "note: The note.")]
    [InlineData("[^ not a note]", "[^ not a note]")]
    [InlineData("see ![a cat](img/cat.png) here", "see a cat here")]
    [InlineData("![](cat.png)", "cat.png")]
    public void HidesMarkup(string line, string expected) => Assert.Equal(expected, Display(line));

    [Fact]
    public void Highlight_IsStyled()
    {
        var s = Assert.Single(Render("==x==").Styles);
        Assert.Equal((2, 3, MdStyle.Highlight), (s.Start, s.End, s.Style));
    }

    [Theory]
    [InlineData("- [ ] buy milk", 2, false)]
    [InlineData("* [x] done", 2, true)]
    [InlineData("  1. [X] numbered", 5, true)]
    [InlineData("> - [ ] quoted", 4, false)]
    public void TaskBox_IsFound(string line, int column, bool done)
    {
        var r = Render(line);
        Assert.Equal(column, r.TaskColumn);
        Assert.Equal(done, r.TaskChecked);
        Assert.Equal(done, r.Styles.Any(s => s.Style == MdStyle.TaskDone));
    }

    [Theory]
    [InlineData("- [ ] a", 0)]      // the bullet is replaced by the box
    [InlineData("  * [x] a", 2)]
    [InlineData("1. [ ] a", 3)]     // a number stays
    public void TaskBox_StartsAtTheBullet(string line, int start) => Assert.Equal(start, Render(line).TaskStart);

    [Theory]
    [InlineData("- [] no")]
    [InlineData("[ ] no bullet")]
    [InlineData("- [ ]x no space")]
    public void NotATask(string line) => Assert.Equal(-1, Render(line).TaskColumn);

    [Fact]
    public void TaskText_KeepsInlineStyles() =>
        Assert.Contains(Render("- [ ] **now**").Styles, s => s.Style == MdStyle.Bold);

    [Fact]
    public void Footnote_ReferenceKnowsItsDefinition()
    {
        var doc = MarkdownDocument.Parse("Text[^a].\n\n[^a]: Note.");
        var reference = Assert.Single(doc.GetLine(0).Styles);
        Assert.Equal((MdStyle.Footnote, "a"), (reference.Style, reference.Url));
        Assert.Equal("a", doc.GetLine(2).FootnoteDefinition);
        Assert.Equal(2, doc.FootnoteDefinitionLine("a"));
        Assert.Equal(-1, doc.FootnoteDefinitionLine("b"));
    }

    [Theory]
    [InlineData("![cat](img/cat.png)", 5, 14, "img/cat.png")]
    [InlineData("![cat](<my cat.png> \"Title\")", 5, 23, "my cat.png")]
    [InlineData("![](c.png)", 9, 1, "c.png")]
    public void Image_HasAnchorAndPath(string line, int anchor, int length, string path)
    {
        var image = Assert.Single(Render(line).Images);
        Assert.Equal((anchor, length, path), (image.AnchorStart, image.AnchorLength, image.Path));
    }

    [Fact]
    public void Link_IsStillALink() => Assert.Equal(MdStyle.Link, Assert.Single(Render("[t](u)").Styles).Style);
}
