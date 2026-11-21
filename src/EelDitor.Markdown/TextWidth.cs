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
        return IsWide(v) ? 2 : 1;
    }

    private static bool IsWide(int v) =>
        v is >= 0x1100 and <= 0x115F
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
