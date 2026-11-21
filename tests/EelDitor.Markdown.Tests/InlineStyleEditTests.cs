using EelDitor.Markdown;

namespace EelDitor.Markdown.Tests;

public class InlineStyleEditTests
{
    private const string Header = "| level | when |";
    private const string Sep = "|---|---|";

    private static Func<string, LineRender?> Row(int cells = 2) => InlineStyleEdit.TableRowParser(Header, Sep, false, cells);

    [Fact]
    public void WrapsAWord() =>
        Assert.Equal("a **bc** d", InlineStyleEdit.TryWrap("a bc d", 2, 4, "**", MdStyle.Bold, InlineStyleEdit.LineParser));

    [Fact]
    public void ALoneStar_CannotBeMadeBold_AndIsLeftAlone()
    {
        // "**" + "*" + "**" would show stars and widen the cell.
        Assert.Null(InlineStyleEdit.TryWrap("| * | x |", 2, 3, "**", MdStyle.Bold, Row()));
    }

    [Fact]
    public void PartlyBoldText_BecomesBoldAsAWhole()
    {
        string line = "| a **b** c | x |";
        Assert.Equal("| **a b c** | x |", InlineStyleEdit.TryWrap(line, 2, 11, "**", MdStyle.Bold, Row()));
    }

    [Fact]
    public void TextWithItalicAndEscapes_GetsBoldAroundIt()
    {
        string line = "| *slovo(\\*)* | x |";
        string? result = InlineStyleEdit.TryWrap(line, 2, 13, "**", MdStyle.Bold, Row());
        Assert.Equal("| ***slovo(\\*)*** | x |", result);
    }

    [Fact]
    public void Unwrap_OnlyWhenTheTextStaysTheSame()
    {
        Assert.Equal("a bc d", InlineStyleEdit.TryUnwrap("a **bc** d", 4, 6, "**", InlineStyleEdit.LineParser));
        Assert.Null(InlineStyleEdit.TryUnwrap("a bc d", 2, 4, "**", InlineStyleEdit.LineParser));
    }

    [Fact]
    public void ARowThatWouldLoseItsCells_IsRejected() =>
        Assert.Null(InlineStyleEdit.TryWrap("| `a | b` |", 2, 4, "`", MdStyle.Code, Row()));
}
