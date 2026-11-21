using EelDitor.Core.Text;

namespace EelDitor.Core.Tests;

public class ParagraphWrapTests
{
    [Theory]
    [InlineData("To jest zdanie. Drugie", 15, true)]
    [InlineData("Naprawdę? Tak", 9, true)]
    [InlineData("Koniec!” Potem", 8, true)]
    [InlineData("np. Kowalski", 3, false)]
    [InlineData("m.in. Polska", 5, false)]
    [InlineData("J. Kowalski", 2, false)]
    [InlineData("e.g. This", 4, false)]
    [InlineData("Czekaj... Co", 9, true)]
    [InlineData("bez kropki tu", 10, false)]
    public void EndsSentence(string text, int space, bool expected) => Assert.Equal(expected, ParagraphWrap.EndsSentence(text, space));

    [Fact]
    public void SentenceBreaks_NeedACapitalAfter_AndSkipCode()
    {
        string t = "Ala ma kota. Kot ma Alę. a to nie. Kod `x. Y` też nie. Koniec";
        var breaks = ParagraphWrap.SentenceBreaks(t).Select(b => t[(b.Start + b.Length)..].Split(' ')[0]).ToList();
        Assert.Equal(["Kot", "Kod", "Koniec"], breaks);
    }

    [Theory]
    [InlineData("# heading", true)]
    [InlineData("-", true)]
    [InlineData("- item", true)]
    [InlineData("12.", true)]
    [InlineData("```", true)]
    [InlineData(">", true)]
    [InlineData("|", true)]
    [InlineData("===", true)]
    [InlineData("-word", false)]
    [InlineData("12.5", false)]
    [InlineData("słowo", false)]
    public void StartsBlock(string word, bool expected) => Assert.Equal(expected, ParagraphWrap.StartsBlock(word));

    // ------------------------------------------------------------------ typing

    private static string Type(string line, WrapMode mode, bool markdown = true)
    {
        // The last character of `line` was just typed at the end.
        var r = ParagraphWrap.BreakAfterTyping(line, line.Length, 1, mode, markdown);
        return r is { } b ? line[..b.Start] + "\n" + b.Prefix + line[(b.Start + b.Length)..] : line;
    }

    [Fact]
    public void Width_BreaksWhenTheLineCrossesTheLimit()
    {
        string line = new string('a', 75) + " bbbbb";      // 81 characters, was 80
        Assert.Equal(new string('a', 75) + "\nbbbbb", Type(line, WrapMode.Width80));
    }

    [Fact]
    public void Width_LeavesAParagraphThatWasAlreadyLonger()
    {
        string line = string.Join(' ', Enumerable.Repeat("słowo", 40)); // an existing one-line paragraph
        Assert.Null(ParagraphWrap.BreakAfterTyping(line + "x", line.Length + 1, 1, WrapMode.Width80, true));
    }

    [Fact]
    public void Width_ContinuesAListItemWithItsIndent()
    {
        string line = "- " + new string('a', 74) + " bbbb";
        Assert.Equal("- " + new string('a', 74) + "\n  bbbb", Type(line, WrapMode.Width80));
    }

    [Fact]
    public void Width_NeverStartsALineWithABlockMarker()
    {
        string line = new string('a', 70) + " - " + "bbbbbbbb";   // 81 characters; "-" must not start the next line
        Assert.Equal(new string('a', 70) + " -\nbbbbbbbb", Type(line, WrapMode.Width80));
    }

    [Fact]
    public void OneLine_NeverBreaks() =>
        Assert.Null(ParagraphWrap.BreakAfterTyping(new string('a', 70) + " bbbbbbbbbbbbbbbb", 87, 1, WrapMode.OneLine, true));

    [Theory]
    [InlineData("Pierwsze zdanie. D", "Pierwsze zdanie.\nD")]
    [InlineData("> Cytat. N", "> Cytat.\n> N")]
    [InlineData("- Punkt. N", "- Punkt.\n  N")]
    [InlineData("Zobacz np. K", "Zobacz np. K")]
    [InlineData("Pytanie? „A", "Pytanie?\n„A")]
    [InlineData("małe. a", "małe. a")]
    public void Sentences_BreakWhenTheNextSentenceStarts(string line, string expected) =>
        Assert.Equal(expected, Type(line, WrapMode.Sentences));

    [Fact]
    public void Sentences_OnlyAtTheEndOfTheLine() =>
        Assert.Null(ParagraphWrap.BreakAfterTyping("Raz. Dwa reszta", 6, 1, WrapMode.Sentences, true));

    // ------------------------------------------------------------------ reflow

    private static readonly string[] Doc =
    [
        "# Tytuł",
        "",
        "Pierwsze zdanie akapitu. Drugie zdanie",
        "jest dłuższe. Trzecie!",
        "",
        "- punkt pierwszy. Dalej",
        "  ciąg dalszy",
        "- punkt drugi",
        "",
        "```",
        "kod. Kod",
        "```",
    ];

    [Fact]
    public void FindParagraph_StopsAtBlankLinesAndListItems()
    {
        Assert.Equal((2, 3), ParagraphWrap.FindParagraph(Doc, 3, true));
        Assert.Equal((5, 6), ParagraphWrap.FindParagraph(Doc, 6, true));
        Assert.Equal((7, 7), ParagraphWrap.FindParagraph(Doc, 7, true));
        Assert.Null(ParagraphWrap.FindParagraph(Doc, 0, true));   // heading
        Assert.Null(ParagraphWrap.FindParagraph(Doc, 1, true));   // blank
        Assert.Null(ParagraphWrap.FindParagraph(Doc, 10, true));  // code
    }

    [Fact]
    public void Reflow_Sentences()
    {
        Assert.Equal(["Pierwsze zdanie akapitu.", "Drugie zdanie jest dłuższe.", "Trzecie!"], ParagraphWrap.Reflow(Doc, 2, 3, WrapMode.Sentences, true));
        Assert.Equal(["- punkt pierwszy.", "  Dalej ciąg dalszy"], ParagraphWrap.Reflow(Doc, 5, 6, WrapMode.Sentences, true));
    }

    [Fact]
    public void Reflow_OneLine_AndWidth()
    {
        Assert.Equal(["Pierwsze zdanie akapitu. Drugie zdanie jest dłuższe. Trzecie!"], ParagraphWrap.Reflow(Doc, 2, 3, WrapMode.OneLine, true));
        var wrapped = ParagraphWrap.Reflow([string.Join(' ', Enumerable.Repeat("słowo", 40))], 0, 0, WrapMode.Width80, true);
        Assert.All(wrapped, l => Assert.True(l.Length <= 80));
        Assert.Equal(4, wrapped.Count); // 13 + 13 + 13 + 1 words
    }

    [Fact]
    public void FrontMatter_IsNeverWrapped()
    {
        string description = "description: Pierwsze zdanie opisu. Drugie zdanie opisu, które jest długie. " + new string('x', 150);
        string text = "---\nname: x\n" + description + "\n---\n\nTreść. Druga " + new string('y', 130) + ".\n";
        string result = ParagraphWrap.SplitLongLines(text, WrapMode.Sentences, true, "\n", 120);
        Assert.Contains(description + "\n---", result);
        Assert.Contains("Treść.\nDruga", result);
        string[] lines = text.Split('\n');
        Assert.Null(ParagraphWrap.FindParagraph(lines, 2, true));
        Assert.True(ParagraphWrap.InFrontMatter(i => i < lines.Length ? lines[i] : null, 2));
        Assert.False(ParagraphWrap.InFrontMatter(i => i < lines.Length ? lines[i] : null, 5));
    }

    [Fact]
    public void SplitLongLines_OnlyTouchesLongProseLines()
    {
        string text = "# Bardzo długi nagłówek. Z kropką\n\nKrótkie. Zdania.\n" + "Długie zdanie numer jeden. Teraz " + new string('x', 120) + " koniec. Drugie zdanie.\n";
        string result = ParagraphWrap.SplitLongLines(text, WrapMode.Sentences, true, "\n", 120);
        Assert.Equal("# Bardzo długi nagłówek. Z kropką\n\nKrótkie. Zdania.\nDługie zdanie numer jeden.\nTeraz " + new string('x', 120) + " koniec.\nDrugie zdanie.\n", result);
    }

    [Fact]
    public void Reflow_ListItemWithBackticksInCode_WrapsAtTheWidth()
    {
        string[] lines =
        [
            "2. **Prosty widok tekstowy, w duchu ASCII.** ",
            "   Markdown jest edytowany jako tekst o stałej szerokości znaków. Znaczniki",
            "   (`**`, `*`, `` ` ``, `#`, adresy linków) są ukryte, a tabele wyrównane wirtualnie: wyświetlane dopełnienie nie trafia",
            "   do pliku. Przy zawijaniu wierszy tabela rysuje się jako siatka ASCII z tekstem zawiniętym w komórkach.",
        ];
        Assert.Equal((0, 3), ParagraphWrap.FindParagraph(lines, 2, true));
        var result = ParagraphWrap.Reflow(lines, 0, 3, WrapMode.Width120, true);
        Assert.True(result.Count >= 3, string.Join(" / ", result));
        Assert.All(result, l => Assert.True(l.Length <= 120, l));
        Assert.All(result.Skip(1), l => Assert.StartsWith("   ", l));
        Assert.Contains("`` ` ``", string.Join(" ", result));
    }

    [Fact]
    public void CodeSpans_PairRunsOfTheSameLength()
    {
        string t = "a `` ` `` b `x` c ` d";
        Assert.Equal([(2, 9), (12, 15)], ParagraphWrap.CodeSpans(t));
    }
}
