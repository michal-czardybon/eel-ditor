using System.Globalization;
using System.Text;

namespace EelDitor.App.Editor;

/// <summary>
/// Symbols and emoji (✔ ✖ ◍ ✅ …), which the text fonts lack and an emoji font (Segoe UI Emoji on Windows) draws in
/// colour. They are never drawn bold: Avalonia makes the emoji font bold by itself, and that draws them white; worse,
/// once a symbol has been drawn bold, the white glyph is kept and used in normal text too, until the program restarts
/// (verified with Avalonia 11.3: ◍ in normal text after a bold ◍). So bold text keeps its symbols at normal weight.
/// </summary>
internal static class SymbolText
{
    /// <summary>A symbol or emoji, or a character joining them (variation selector, zero-width joiner, skin tone).</summary>
    public static bool IsSymbol(Rune rune)
    {
        int v = rune.Value;
        if (v < 0x2190) return false; // Latin-1 symbols (© ° …) are in the text fonts
        if (v >= 0x1F000) return true;
        if (v is 0xFE0F or 0x200D or 0x20E3) return true;
        return Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol;
    }

    /// <summary>Whether the text has any symbol (a quick test before splitting).</summary>
    public static bool HasSymbol(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (IsSymbol(rune)) return true;
        return false;
    }

    /// <summary>
    /// Loads the normal weight of the fonts that draw symbols and emoji before anything is drawn. Avalonia keeps the
    /// glyph typefaces it made and, asked for normal weight later, takes the nearest one it already has: when the first
    /// emoji of the session was bold (a bold line in raw mode, a header), every later emoji was drawn from that
    /// emboldened, white face. With the normal ones made first, normal text always gets them.
    /// </summary>
    public static void WarmUp(Avalonia.Media.FontFamily editorFont)
    {
        var fonts = Avalonia.Media.FontManager.Current;
        foreach (int cp in new[] { 0x2714, 0x2705, 0x25CD, 0x2716, 0x1F600, 0x2192, 0x2211, 0x2500, 0x2615, 0x4E00 })
            foreach (var style in new[] { Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontStyle.Italic })
                try
                {
                    if (fonts.TryMatchCharacter(cp, style, Avalonia.Media.FontWeight.Normal, Avalonia.Media.FontStretch.Normal,
                            editorFont, CultureInfo.CurrentCulture, out var face))
                        fonts.TryGetGlyphTypeface(face, out _);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { } // a font that cannot be read
    }

    /// <summary>The text cut into runs of symbols and runs of other text, in order, as (start, length, symbols).</summary>
    public static IEnumerable<(int Start, int Length, bool Symbols)> Runs(string text)
    {
        int start = 0, i = 0;
        bool? current = null;
        while (i < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int used);
            bool symbol = IsSymbol(rune);
            if (current is bool c && c != symbol)
            {
                yield return (start, i - start, c);
                start = i;
            }
            current = symbol;
            i += Math.Max(1, used);
        }
        if (text.Length > start) yield return (start, text.Length - start, current ?? false);
    }
}
