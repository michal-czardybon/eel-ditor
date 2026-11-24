using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>
/// Measures how many columns of the editor's monospace font a character above U+2000 really takes (one or two), for
/// <see cref="TextWidth.Measured"/>: a character the font lacks is drawn by a fallback font, often wider (✔ ✖ ◍ from
/// the emoji font). Tables count it as wide as it is drawn, so it keeps its full size and the columns stay aligned.
/// </summary>
internal static class SymbolWidths
{
    private static ConcurrentDictionary<int, int> _cache = new();
    private static Typeface _face;
    private static double _cell;

    /// <summary>Measures in this font from now on (at start and after a font change).</summary>
    public static void Use(FontFamily font)
    {
        _face = new Typeface(font);
        _cell = Width("M");
        _cache = new ConcurrentDictionary<int, int>();
        TextWidth.Measured = Columns;
    }

    private static double Width(string text) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 40, null).WidthIncludingTrailingWhitespace;

    private static int? Columns(int codePoint)
    {
        if (_cell <= 0) return null;
        return _cache.GetOrAdd(codePoint, cp =>
        {
            try
            {
                // Drawn more than a third wider than a column: two columns (a fallback font's glyph is rarely wider).
                return Width(char.ConvertFromUtf32(cp)) > _cell * 1.35 ? 2 : 1;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return 1; }
        });
    }
}
