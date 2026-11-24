using System.Text;

namespace EelDitor.Markdown;

/// <summary>Display width of text in monospace cells (East Asian wide characters take two cells).</summary>
public static class TextWidth
{
    public static int Of(ReadOnlySpan<char> text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Of(rune);
        return width;
    }

    public static int Of(Rune rune)
    {
        int v = rune.Value;
        if (v < 0x1100) return v is 0x200B or 0x200C or 0x200D or 0xFEFF ? 0 : 1;
        var cat = Rune.GetUnicodeCategory(rune);
        if (cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.Format) return 0;
        if (IsWide(v)) return 2;
        return v >= 0x2000 && Measured?.Invoke(v) is int measured ? measured : 1;
    }

    /// <summary>
    /// Columns of a character above U+2000 as the editor really draws it (set by the app, which measures it in the
    /// editor's font or the font that stands in for it): a symbol the text font lacks (✔ ✖ ◍) comes from an emoji or
    /// symbol font, about two columns wide; counted as one, it had to be shrunk to a dot in a table. Null: one column.
    /// </summary>
    public static Func<int, int?>? Measured { get; set; }

    /// <summary>
    /// East Asian Wide and Fullwidth characters (Unicode's EastAsianWidth W/F), including the emoji that are drawn as
    /// emoji by default (✅ ❌ ⭐ ⌛…): an emoji font draws them about two columns wide, so counting them as one put the
    /// following text into them in a table.
    /// </summary>
    private static bool IsWide(int v) =>
        v is 0x231A or 0x231B or 0x2329 or 0x232A or >= 0x23E9 and <= 0x23EC or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE
        or 0x2614 or 0x2615 or >= 0x2648 and <= 0x2653 or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE
        or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD or 0x2705
        or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2795 and <= 0x2797
        or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55
        or 0x1F004 or 0x1F0CF or 0x1F18E or >= 0x1F191 and <= 0x1F19A or >= 0x1F200 and <= 0x1F202 or >= 0x1F210 and <= 0x1F23B
        or >= 0x1F240 and <= 0x1F248 or 0x1F250 or 0x1F251 or >= 0x1F260 and <= 0x1F265 or >= 0x1F680 and <= 0x1F6FF
        or >= 0x1F7E0 and <= 0x1F7EB or >= 0x1FA70 and <= 0x1FAFF
        or >= 0x1100 and <= 0x115F
        or >= 0x2E80 and <= 0x303E
        or >= 0x3041 and <= 0x33FF
        or >= 0x3400 and <= 0x4DBF
        or >= 0x4E00 and <= 0x9FFF
        or >= 0xA000 and <= 0xA4CF
        or >= 0xAC00 and <= 0xD7A3
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFF60
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x1F300 and <= 0x1F64F
        or >= 0x1F900 and <= 0x1F9FF
        or >= 0x20000 and <= 0x3FFFD;
}
