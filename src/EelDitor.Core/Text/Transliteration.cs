using System.Globalization;
using System.Text;

namespace EelDitor.Core.Text;

/// <summary>
/// Latin ↔ Cyrillic for Russian, Ukrainian (scientific transliteration: š č ž x ja ju šč, ʹ for the soft sign),
/// Serbian (Gaj's Latin ↔ Vuk's Cyrillic, one to one) and Interslavic (the official alphabets; dž → дж).
/// Matching is greedy (the longest letter group first) and keeps the case: <c>Ja</c> → <c>Я</c>, <c>JA</c> → <c>Я</c>,
/// <c>Щи</c> → <c>Šči</c>, <c>ЩИ</c> → <c>ŠČI</c>. Characters that are not letters of the alphabet are kept.
/// </summary>
public sealed class Transliteration
{
    private readonly (string Latin, string Cyrillic)[] _toCyrillic;
    private readonly (string Cyrillic, string Latin)[] _toLatin;
    private readonly int _maxLatin, _maxCyrillic;

    public string Id { get; }

    private Transliteration(string id, (string Latin, string Cyrillic)[] pairs, (string Cyrillic, string Latin)[]? extraToLatin = null,
        (string Latin, string Cyrillic)[]? extraToCyrillic = null)
    {
        Id = id;
        _toCyrillic = [.. pairs, .. extraToCyrillic ?? []];
        _toLatin = [.. pairs.Select(p => (p.Cyrillic, p.Latin)), .. extraToLatin ?? []];
        _maxLatin = _toCyrillic.Max(p => p.Latin.Length);
        _maxCyrillic = _toLatin.Max(p => p.Cyrillic.Length);
    }

    public string ToCyrillic(string text) => Convert(text, _toCyrillic, _maxLatin);

    public string ToLatin(string text) => Convert(text, _toLatin, _maxCyrillic);

    private static string Convert(string text, (string From, string To)[] table, int maxLength)
    {
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            for (int len = Math.Min(maxLength, text.Length - i); len >= 1 && !matched; len--)
            {
                string chunk = text.Substring(i, len);
                string lower = chunk.ToLower(CultureInfo.InvariantCulture);
                foreach (var (from, to) in table)
                {
                    // The first entry for a letter group wins (the table lists the preferred spelling first).
                    if (from != lower) continue;
                    sb.Append(MatchCase(to, chunk, text, i, i + len));
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched) sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Applies the case of the source chunk: lower, Title, or ALL CAPS. A single capital letter that becomes
    /// several letters (Щ → šč) is all caps when a neighbouring letter is a capital too (ЩИ → ŠČI, Щи → Šči).
    /// </summary>
    private static string MatchCase(string to, string source, string text, int start, int next)
    {
        var letters = source.Where(char.IsLetter).ToList();
        if (to.Length == 0 || letters.Count == 0 || !char.IsUpper(letters[0])) return to;
        bool allCaps = letters.Count > 1
            ? letters.All(char.IsUpper)
            : next < text.Length && char.IsLetter(text[next]) ? char.IsUpper(text[next])
            : start > 0 && char.IsLetter(text[start - 1]) && char.IsUpper(text[start - 1]);
        return allCaps ? to.ToUpper(CultureInfo.InvariantCulture) : char.ToUpper(to[0], CultureInfo.InvariantCulture) + to[1..];
    }

    private static (string, string)[] P(string spec) =>
        spec.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => { var kv = x.Split('='); return (kv[0], kv[1]); }).ToArray();

    /// <summary>Russian, scientific transliteration.</summary>
    public static Transliteration Russian { get; } = new("ru",
        P("šč=щ ja=я ju=ю ë=ё è=э a=а b=б v=в g=г d=д e=е ž=ж z=з i=и j=й k=к l=л m=м n=н o=о p=п r=р s=с t=т u=у f=ф x=х c=ц č=ч š=ш y=ы ʹ=ь ʺ=ъ"),
        extraToCyrillic: P("h=х '=ь"));

    /// <summary>Ukrainian, scientific transliteration.</summary>
    public static Transliteration Ukrainian { get; } = new("uk",
        P("šč=щ ja=я ju=ю je=є ji=ї a=а b=б v=в h=г g=ґ d=д e=е ž=ж z=з y=и i=і j=й k=к l=л m=м n=н o=о p=п r=р s=с t=т u=у f=ф x=х c=ц č=ч š=ш ʹ=ь ʼ=ʼ"),
        extraToCyrillic: P("'=ʼ ’=ʼ"),
        extraToLatin: P("'=ʼ ’=ʼ"));

    /// <summary>Serbian: Gaj's Latin alphabet and Vuk's Cyrillic, letter for letter.</summary>
    public static Transliteration Serbian { get; } = new("sr",
        P("lj=љ nj=њ dž=џ đ=ђ ć=ћ č=ч š=ш ž=ж a=а b=б v=в g=г d=д e=е z=з i=и j=ј k=к l=л m=м n=н o=о p=п r=р s=с t=т u=у f=ф h=х c=ц"));

    /// <summary>Interslavic: the standard Latin and Cyrillic alphabets (etymological letters map to their standard Cyrillic).</summary>
    public static Transliteration Interslavic { get; } = new("isv",
        P("dž=дж lj=љ nj=њ ě=є y=ы j=ј č=ч š=ш ž=ж a=а b=б v=в g=г d=д e=е z=з i=и k=к l=л m=м n=н o=о p=п r=р s=с t=т u=у f=ф h=х c=ц"),
        extraToLatin: P("џ=dž ћ=ć ђ=đ ѣ=ě ѧ=ę ѫ=ų ъ=ȯ й=j щ=šč ю=ju я=ja"),
        extraToCyrillic: P("ć=ћ đ=ђ ę=ѧ ų=ѫ ȯ=ъ ò=ъ å=а ė=е ĺ=љ ľ=љ ń=њ ŕ=р ť=т ď=д ś=с ź=з"));

    public static IReadOnlyList<Transliteration> All { get; } = [Russian, Ukrainian, Serbian, Interslavic];
}
