using System.Globalization;
using System.Text;

namespace EelDitor.Core.Text;

/// <summary>
/// Describes how a file's bytes map to text: a code page plus whether a byte order mark is present.
/// The BOM is written explicitly by <see cref="TextFile"/> so saving reproduces the source exactly.
/// </summary>
public sealed record TextEncoding(int CodePage, bool HasBom)
{
    public const int Utf8CodePage = 65001;
    public const int Utf16LeCodePage = 1200;
    public const int Utf16BeCodePage = 1201;
    public const int Utf32LeCodePage = 12000;
    public const int Utf32BeCodePage = 12001;

    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Forces the static constructor (code page provider registration) to run.</summary>
    public static void EnsureInitialized() { }

    public static TextEncoding Utf8 { get; } = new(Utf8CodePage, false);
    public static TextEncoding Utf8Bom { get; } = new(Utf8CodePage, true);
    public static TextEncoding Utf16Le { get; } = new(Utf16LeCodePage, true);
    public static TextEncoding Utf16Be { get; } = new(Utf16BeCodePage, true);

    public bool IsUnicode => CodePage is Utf8CodePage or Utf16LeCodePage or Utf16BeCodePage or Utf32LeCodePage or Utf32BeCodePage;

    /// <summary>The system's legacy ANSI code page (1252 outside Windows).</summary>
    public static int AnsiCodePage
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 1252;
            int cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return cp is 0 or Utf8CodePage ? 1252 : cp;
        }
    }

    /// <summary>An encoding without preamble whose encoder throws on unmappable characters.</summary>
    public Encoding GetStrictEncoding() => CodePage switch
    {
        Utf8CodePage => new UTF8Encoding(false, true),
        Utf16LeCodePage => new UnicodeEncoding(false, false, true),
        Utf16BeCodePage => new UnicodeEncoding(true, false, true),
        Utf32LeCodePage => new UTF32Encoding(false, false, true),
        Utf32BeCodePage => new UTF32Encoding(true, false, true),
        _ => Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
    };

    /// <summary>An encoding without preamble that substitutes unmappable characters instead of throwing.</summary>
    public Encoding GetLenientEncoding() => CodePage switch
    {
        Utf8CodePage => new UTF8Encoding(false, false),
        Utf16LeCodePage => new UnicodeEncoding(false, false, false),
        Utf16BeCodePage => new UnicodeEncoding(true, false, false),
        Utf32LeCodePage => new UTF32Encoding(false, false, false),
        Utf32BeCodePage => new UTF32Encoding(true, false, false),
        _ => Encoding.GetEncoding(CodePage),
    };

    public byte[] Preamble => !HasBom ? [] : CodePage switch
    {
        Utf8CodePage => [0xEF, 0xBB, 0xBF],
        Utf16LeCodePage => [0xFF, 0xFE],
        Utf16BeCodePage => [0xFE, 0xFF],
        Utf32LeCodePage => [0xFF, 0xFE, 0x00, 0x00],
        Utf32BeCodePage => [0x00, 0x00, 0xFE, 0xFF],
        _ => [],
    };

    public string DisplayName => CodePage switch
    {
        Utf8CodePage => HasBom ? "UTF-8 BOM" : "UTF-8",
        Utf16LeCodePage => HasBom ? "UTF-16 LE BOM" : "UTF-16 LE",
        Utf16BeCodePage => HasBom ? "UTF-16 BE BOM" : "UTF-16 BE",
        Utf32LeCodePage => "UTF-32 LE",
        Utf32BeCodePage => "UTF-32 BE",
        _ => SafeName(CodePage),
    };

    private static string SafeName(int cp)
    {
        try
        {
            var e = Encoding.GetEncoding(cp);
            return cp switch
            {
                >= 1250 and <= 1258 => $"Windows-{cp}",
                _ => e.WebName.ToUpperInvariant(),
            };
        }
        catch (Exception) { return $"CP{cp}"; }
    }

    public override string ToString() => DisplayName;

    /// <summary>Encodings offered in the Encoding menu, grouped by a language-neutral group id (the UI translates it).</summary>
    public static IReadOnlyList<(string Group, TextEncoding Encoding)> MenuEncodings { get; } =
    [
        ("Unicode", Utf8),
        ("Unicode", Utf8Bom),
        ("Unicode", Utf16Le),
        ("Unicode", Utf16Be),
        ("Central", new(1250, false)),
        ("Central", new(28592, false)),
        ("Central", new(852, false)),
        ("Western", new(1252, false)),
        ("Western", new(28591, false)),
        ("Western", new(437, false)),
        ("Cyrillic", new(1251, false)),
        ("Cyrillic", new(20866, false)),
        ("Cyrillic", new(866, false)),
        ("Baltic", new(1257, false)),
        ("Greek", new(1253, false)),
        ("Turkish", new(1254, false)),
        ("Hebrew", new(1255, false)),
        ("Arabic", new(1256, false)),
        ("Asian", new(932, false)),
        ("Asian", new(936, false)),
        ("Asian", new(949, false)),
        ("Asian", new(950, false)),
    ];
}
