using EelDitor.Core.Html;

namespace EelDitor.Core.Tests;

/// <summary>Formatted text pasted from Word (and other word processors) becomes Markdown.</summary>
public class HtmlToMarkdownTests
{
    // What Word puts on the clipboard (shortened): lists are paragraphs with mso-list styles and the bullet or number
    // typed in a span with mso-list:Ignore inside conditional comments.
    private const string Word = """
        <html xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:w="urn:schemas-microsoft-com:office:word">
        <head><meta name=Generator content="Microsoft Word 15"><style>p.MsoNormal {margin:0cm;}</style></head>
        <body lang=PL>
        <!--StartFragment-->
        <h1>Tytuł</h1>
        <p class=MsoNormal>Zwykły <b>pogrubiony</b> i <i>kursywa</i>
        tekst, <a href="https://example.org/a b">link</a>.<o:p></o:p></p>
        <p class=MsoListParagraphCxSpFirst style='text-indent:-18.0pt;mso-list:l0 level1 lfo1'><![if !supportLists]><span
        style='font-family:Symbol'><span style='mso-list:Ignore'>·<span style='font:7.0pt "Times New Roman"'>&nbsp;&nbsp;&nbsp;
        </span></span></span><![endif]>Pierwszy<o:p></o:p></p>
        <p class=MsoListParagraphCxSpMiddle style='margin-left:72.0pt;text-indent:-18.0pt;mso-list:l0 level2 lfo1'><![if !supportLists]><span
        style='font-family:"Courier New"'><span style='mso-list:Ignore'>o<span style='font:7.0pt "Times New Roman"'>&nbsp;&nbsp;
        </span></span></span><![endif]>Zagnieżdżony<o:p></o:p></p>
        <p class=MsoListParagraphCxSpLast style='text-indent:-18.0pt;mso-list:l0 level1 lfo1'><![if !supportLists]><span
        style='font-family:Symbol'><span style='mso-list:Ignore'>·<span>&nbsp;&nbsp;&nbsp; </span></span></span><![endif]>Drugi
        z *gwiazdką*<o:p></o:p></p>
        <p class=MsoNormal><o:p>&nbsp;</o:p></p>
        <p class=MsoListParagraphCxSpFirst style='mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>1.<span>&nbsp;&nbsp;
        </span></span><![endif]>Jeden<o:p></o:p></p>
        <p class=MsoListParagraphCxSpLast style='mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>2.<span>&nbsp;&nbsp;
        </span></span><![endif]>Dwa<o:p></o:p></p>
        <table class=MsoTableGrid border=1 cellspacing=0 cellpadding=0>
         <tr><td><p class=MsoNormal><b>Nazwa<o:p></o:p></b></p></td><td><p class=MsoNormal><b>Opis<o:p></o:p></b></p></td></tr>
         <tr><td><p class=MsoNormal>a<o:p></o:p></p></td><td><p class=MsoNormal>b | c<o:p></o:p></p><p class=MsoNormal>d<o:p></o:p></p></td></tr>
        </table>
        <!--EndFragment-->
        </body></html>
        """;

    [Fact]
    public void Word_ListsTablesHeadingsAndStyles()
    {
        Assert.True(HtmlToMarkdown.IsFromWordProcessor(Word));
        Assert.Equal(
            "# Tytuł\n\n" +
            "Zwykły **pogrubiony** i *kursywa* tekst, [link](https://example.org/a%20b).\n\n" +
            "- Pierwszy\n  - Zagnieżdżony\n- Drugi z \\*gwiazdką\\*\n\n" +
            "1. Jeden\n2. Dwa\n\n" +
            "| Nazwa | Opis |\n|---|---|\n| a | b \\| c<br>d |",
            HtmlToMarkdown.Convert(Word));
    }

    [Fact]
    public void GoogleDocs_StylesFromCss_AndAWrapperAroundTheBlocks()
    {
        const string html = """
            <meta charset="utf-8"><b style="font-weight:normal;" id="docs-internal-guid-1234"><p dir="ltr"><span style="font-weight:700;">Mocny</span><span style="font-weight:400;"> zwykły </span><span style="font-style:italic;">pochyły</span></p><ol><li dir="ltr"><p dir="ltr"><span>Raz</span></p></li><li dir="ltr"><p dir="ltr"><span>Dwa</span></p><ul><li><p><span>pod</span></p></li></ul></li></ol></b>
            """;
        Assert.True(HtmlToMarkdown.IsFromWordProcessor(html));
        Assert.Equal("**Mocny** zwykły *pochyły*\n\n1. Raz\n2. Dwa\n   - pod", HtmlToMarkdown.Convert(html));
    }

    [Fact]
    public void AWebPage_IsNotAWordProcessor() =>
        Assert.False(HtmlToMarkdown.IsFromWordProcessor("<html><body><p>Hello <b>web</b></p></body></html>"));

    [Fact]
    public void ATableWithoutAHeader_GetsAnEmptyOne()
    {
        string md = HtmlToMarkdown.Convert("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>");
        Assert.Equal("|  |  |\n|---|---|\n| a | b |\n| c | d |", md);
    }
}
