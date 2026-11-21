using EelDitor.Core.Text;

namespace EelDitor.Core.Tests;

public class WrapDetectionTests
{
    private static (WrapMode? Mode, bool Mixed) Detect(string text, bool markdown = true) =>
        ParagraphWrap.Detect(text.Split('\n'), markdown);

    private static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => "słowo" + (i % 10)));

    [Fact]
    public void LongParagraphs_AreOneLine() =>
        Assert.Equal((WrapMode.OneLine, false), Detect("# T\n\n" + Words(40) + "\n\n" + Words(30) + "\n"));

    [Fact]
    public void BrokenAtWidths()
    {
        string at80 = string.Join("\n", ParagraphWrap.WrapWords(Words(60), "", "", 80, true));
        string at120 = string.Join("\n", ParagraphWrap.WrapWords(Words(60), "", "", 120, true));
        Assert.Equal((WrapMode.Width80, false), Detect(at80 + "\n\n" + at80));
        Assert.Equal((WrapMode.Width120, false), Detect(at120 + "\n\n" + at120));
        string at100 = string.Join("\n", ParagraphWrap.WrapWords(Words(60), "", "", 100, true));
        Assert.Equal((WrapMode.Width100, false), Detect(at100 + "\n\n" + at100));
    }

    [Fact]
    public void JoinParagraphs_MakesOneLinePerParagraph_KeepingEverythingElse()
    {
        string text = "---\ntitle: a\nb: c\n---\n# Tytuł\n\nPierwsze zdanie.\nDrugie zdanie.\n\n- punkt jeden\n  ciąg dalszy\n- punkt dwa\n\n> cytat\n> dalej\n\n```\nkod\nkod\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nTwarde  \nzłamanie\n";
        var (joined, count) = ParagraphWrap.JoinParagraphs(text, markdown: true);
        Assert.Equal("---\ntitle: a\nb: c\n---\n# Tytuł\n\nPierwsze zdanie. Drugie zdanie.\n\n- punkt jeden ciąg dalszy\n- punkt dwa\n\n> cytat dalej\n\n```\nkod\nkod\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nTwarde  \nzłamanie\n", joined);
        Assert.Equal(3, count);
        Assert.Equal(0, ParagraphWrap.JoinParagraphs(joined, true).Joined);
    }

    [Fact]
    public void Sentences() =>
        Assert.Equal((WrapMode.Sentences, false), Detect("Pierwsze zdanie.\nDrugie, dłuższe zdanie.\nTrzecie.\n\nInny akapit.\nI koniec."));

    [Fact]
    public void Mixed() =>
        Assert.Equal((null, true), Detect(Words(40) + "\n\nPierwsze zdanie.\nDrugie zdanie.\nTrzecie."));

    [Fact]
    public void ShortParagraphsCodeAndTables_TellNothing() =>
        Assert.Equal((null, false), Detect("# T\n\nKrótko.\n\n```\n" + Words(40) + "\n```\n\n| a | b |\n|---|---|\n| " + Words(40) + " | x |\n"));
}
