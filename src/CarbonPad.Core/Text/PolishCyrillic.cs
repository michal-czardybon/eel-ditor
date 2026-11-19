using System.Globalization;
using System.Text;

namespace CarbonPad.Core.Text;

/// <summary>Variants of "Polska cyrylica 2.0".</summary>
public enum CyrillicVariant
{
    /// <summary>A, East Slavic style: softening with ь, nasal vowels with a breve (ӑ ӗ), ó = о̆, rz = р̌, dz = дз.</summary>
    A,

    /// <summary>B, native style: softening with an acute (с́ ц́ з́ н́), nasal vowels with yuses (ѫ ѧ ѭ ѩ), ó = о́, rz = р́, dz = ѕ.</summary>
    B,
}

/// <summary>
/// Converts Polish text between the Latin alphabet and the two Cyrillic variants of "Polska cyrylica 2.0".
/// Conversion works word by word; non-letters are copied unchanged. Back-conversion accepts both variants.
/// </summary>
public static class PolishCyrillic
{
    private const string Breve = "̆", Caron = "̌", Acute = "́";

    private static readonly string[] ConsonantUnits =
        ["szcz", "sz", "cz", "ch", "rz", "dź", "dż", "dz", "b", "c", "ć", "d", "f", "g", "h", "k", "l", "ł", "m", "n", "ń", "p", "r", "s", "ś", "t", "w", "z", "ź", "ż"];

    private const string Vowels = "aąeęioóuy";

    private static string Consonant(string unit, CyrillicVariant v) => unit switch
    {
        "szcz" => "щ", "sz" => "ш", "cz" => "ч", "ch" => "х",
        "rz" => v == CyrillicVariant.A ? "р" + Caron : "р" + Acute,
        "dz" => v == CyrillicVariant.A ? "дз" : "ѕ",
        "dź" => v == CyrillicVariant.A ? "дзь" : "ѕ" + Acute,
        "dż" => v == CyrillicVariant.A ? "дж" : "џ",
        "b" => "б", "c" => "ц", "d" => "д", "f" => "ф", "g" => "г", "h" => "һ", "k" => "к",
        "l" => "љ", "ł" => "л", "m" => "м", "n" => "н", "p" => "п", "r" => "р", "s" => "с", "t" => "т",
        "w" => "в", "z" => "з", "ż" => "ж",
        "ń" => v == CyrillicVariant.A ? "њ" : "н" + Acute,
        "ś" => v == CyrillicVariant.A ? "сь" : "с" + Acute,
        "ć" => v == CyrillicVariant.A ? "ць" : "ц" + Acute,
        "ź" => v == CyrillicVariant.A ? "зь" : "з" + Acute,
        _ => unit,
    };

    private static string PlainVowel(char c, CyrillicVariant v) => c switch
    {
        'a' => "а", 'e' => "е", 'i' => "і", 'o' => "о", 'u' => "у", 'y' => "ы",
        'ą' => v == CyrillicVariant.A ? "ӑ" : "ѫ",
        'ę' => v == CyrillicVariant.A ? "ӗ" : "ѧ",
        'ó' => v == CyrillicVariant.A ? "о" + Breve : "о" + Acute,
        _ => c.ToString(),
    };

    /// <summary>A vowel after softening "i" (soft = true) or after "j".</summary>
    private static string IotatedVowel(char c, bool soft, CyrillicVariant v) => c switch
    {
        'a' => "я", 'e' => "є", 'u' => "ю",
        'ą' => v == CyrillicVariant.A ? "я" + Breve : "ѭ",
        'ę' => v == CyrillicVariant.A ? "є" + Breve : "ѩ",
        'o' => soft ? "ьо" : "йо",
        'ó' => (soft ? "ь" : "й") + (v == CyrillicVariant.A ? "о" + Breve : "о" + Acute),
        'i' => soft ? "ьі" : "йі",
        _ => c.ToString(),
    };

    private static bool HasIotatedLetter(char c) => c is 'a' or 'e' or 'u' or 'ą' or 'ę';

    // ------------------------------------------------------------------ Latin → Cyrillic

    public static string ToCyrillic(string text, CyrillicVariant variant)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i])) { sb.Append(text[i++]); continue; }
            int end = i;
            while (end < text.Length && (char.IsLetter(text[end]) || IsCombining(text[end]))) end++;
            sb.Append(WordToCyrillic(text[i..end], variant));
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>Converts one word (letters only).</summary>
    public static string WordToCyrillic(string word, CyrillicVariant v)
    {
        string lower = word.ToLowerInvariant();
        bool allUpper = word.Length > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c));
        var sb = new StringBuilder(word.Length + 4);
        bool afterConsonant = false; // the previous unit was a consonant
        int i = 0;
        while (i < lower.Length)
        {
            int start = i;
            string output;
            char c = lower[i];
            char next = i + 1 < lower.Length ? lower[i + 1] : '\0';
            if (c == 'i' && afterConsonant && Vowels.Contains(next) && next != '\0')
            {
                output = IotatedVowel(next, soft: true, v); // nie, kiosk, pieśni
                i += 2;
                afterConsonant = false;
            }
            else if (c == 'j')
            {
                if (next != '\0' && Vowels.Contains(next))
                {
                    output = (afterConsonant && HasIotatedLetter(next) ? "’" : "") + IotatedVowel(next, soft: false, v); // objazd
                    i += 2;
                }
                else
                {
                    output = "й"; // Ojczyzno
                    i++;
                }
                afterConsonant = false;
            }
            else if (Vowels.Contains(c))
            {
                output = PlainVowel(c, v);
                i++;
                afterConsonant = false;
            }
            else if (ConsonantUnits.FirstOrDefault(u => string.CompareOrdinal(lower, i, u, 0, u.Length) == 0) is { } unit)
            {
                output = Consonant(unit, v);
                i += unit.Length;
                // Variant A marks softness only on the last consonant of a soft group: ść = сць (ścieżka = сцєжка).
                if (v == CyrillicVariant.A && unit == "ś" && i < lower.Length
                    && (lower[i] == 'ć' || (lower[i] == 'c' && i + 1 < lower.Length && lower[i + 1] == 'i')))
                    output = "с";
                afterConsonant = true;
            }
            else
            {
                output = word[i].ToString(); // a letter outside the Polish alphabet: kept
                i++;
                afterConsonant = false;
            }
            sb.Append(ApplyCase(word.Substring(start, i - start), output, allUpper));
        }
        return sb.ToString();
    }

    private static string ApplyCase(string source, string output, bool allUpper)
    {
        if (allUpper) return output.ToUpperInvariant();
        if (!char.IsUpper(source[0])) return output;
        // Capitalize the first letter of the output (an apostrophe or ь may come first).
        int k = 0;
        while (k < output.Length && (output[k] is '’' or 'ь' || !char.IsLetter(output[k]))) k++;
        if (k >= output.Length) return output;
        return output[..k] + char.ToUpperInvariant(output[k]) + output[(k + 1)..];
    }

    // ------------------------------------------------------------------ Cyrillic → Latin

    private static bool IsCombining(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;

    public static string ToLatin(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!(char.IsLetter(text[i]) || text[i] == '’')) { sb.Append(text[i++]); continue; }
            int end = i;
            while (end < text.Length && (char.IsLetter(text[end]) || IsCombining(text[end]) || text[end] == '’')) end++;
            sb.Append(WordToLatin(text[i..end]));
            i = end;
        }
        return sb.ToString();
    }

    private static readonly Dictionary<string, string> CyrillicConsonants = new()
    {
        ["б"] = "b", ["ц"] = "c", ["д"] = "d", ["ф"] = "f", ["г"] = "g", ["һ"] = "h", ["к"] = "k", ["љ"] = "l", ["л"] = "ł",
        ["м"] = "m", ["н"] = "n", ["њ"] = "ń", ["н" + Acute] = "ń", ["п"] = "p", ["р"] = "r", ["р" + Caron] = "rz", ["р" + Acute] = "rz",
        ["с"] = "s", ["с" + Acute] = "ś", ["т"] = "t", ["в"] = "w", ["з"] = "z", ["з" + Acute] = "ź", ["ж"] = "ż",
        ["ц" + Acute] = "ć", ["щ"] = "szcz", ["ш"] = "sz", ["ч"] = "cz", ["х"] = "ch", ["ѕ"] = "dz", ["ѕ" + Acute] = "dź",
        ["џ"] = "dż", ["й"] = "j",
    };

    private static readonly Dictionary<string, string> CyrillicVowels = new()
    {
        ["а"] = "a", ["е"] = "e", ["і"] = "i", ["о"] = "o", ["у"] = "u", ["ы"] = "y", ["ӑ"] = "ą", ["ѫ"] = "ą",
        ["ӗ"] = "ę", ["ѧ"] = "ę", ["о" + Breve] = "ó", ["о" + Acute] = "ó",
    };

    private static readonly Dictionary<string, string> CyrillicIotated = new()
    {
        ["я"] = "a", ["є"] = "e", ["ю"] = "u", ["я" + Breve] = "ą", ["ѭ"] = "ą", ["є" + Breve] = "ę", ["ѩ"] = "ę",
    };

    /// <summary>Reads one letter with its combining mark (lowercase) and whether it was uppercase.</summary>
    private static (string Unit, bool Upper, int Length) Read(string s, int i)
    {
        int len = 1;
        while (i + len < s.Length && IsCombining(s[i + len])) len++;
        string unit = s.Substring(i, len);
        return (unit.ToLowerInvariant(), char.IsUpper(s[i]), len);
    }

    public static string WordToLatin(string word)
    {
        bool allUpper = word.Count(char.IsLetter) > 1 && word.All(c => !char.IsLetter(c) || char.IsUpper(c));
        var parts = new List<(string Latin, bool Upper, bool Consonant)>();
        int i = 0;
        bool apostrophe = false;
        while (i < word.Length)
        {
            if (word[i] == '’') { apostrophe = true; i++; continue; }
            var (u, upper, len) = Read(word, i);
            string next = i + len < word.Length ? Read(word, i + len).Unit : "";
            bool prevConsonant = parts.Count > 0 && parts[^1].Consonant;

            if (u == "ь")
            {
                // ьо, ьо̆, ьі: a soft "i" before o/ó/i; otherwise softening of the preceding consonant (сь = ś).
                if (next.StartsWith('о') || next == "і") parts.Add(("i", upper, false));
                else if (parts.Count > 0) parts[^1] = (Soften(parts[^1].Latin), parts[^1].Upper, true);
                i += len;
                continue;
            }
            if (u == "д" && next == "з")
            {
                parts.Add(("dz", upper, true)); // дз (A); дзь = dź through the soft sign
                i += len + Read(word, i + len).Length;
                continue;
            }
            if (u == "д" && next == "ж")
            {
                parts.Add(("dż", upper, true));
                i += len + Read(word, i + len).Length;
                continue;
            }
            if (CyrillicIotated.TryGetValue(u, out var iv))
            {
                // After a consonant: softening "i"; at the start, after a vowel or after ’: "j".
                parts.Add(((prevConsonant && !apostrophe ? "i" : "j") + iv, upper, false));
                apostrophe = false;
                i += len;
                continue;
            }
            if (CyrillicVowels.TryGetValue(u, out var v)) { parts.Add((v, upper, false)); i += len; apostrophe = false; continue; }
            if (CyrillicConsonants.TryGetValue(u, out var c))
            {
                // Variant A: с before a softened ц is ś (сцєжка = ścieżka).
                if (u == "с" && next == "ц" && i + len + 1 < word.Length)
                {
                    int after = i + len + Read(word, i + len).Length;
                    string third = after < word.Length ? Read(word, after).Unit : "";
                    if (third is "ь" or "і" || CyrillicIotated.ContainsKey(third)) c = "ś";
                }
                parts.Add((c, upper, u != "й"));
                i += len;
                apostrophe = false;
                continue;
            }
            parts.Add((word.Substring(i, len), upper, false)); // not a Polish Cyrillic letter: kept
            i += len;
        }

        var sb = new StringBuilder(word.Length + 4);
        foreach (var (latin, upper, _) in parts)
        {
            if (allUpper) sb.Append(latin.ToUpperInvariant());
            else if (upper) sb.Append(char.ToUpperInvariant(latin[0])).Append(latin[1..]);
            else sb.Append(latin);
        }
        return sb.ToString();
    }

    private static string Soften(string latin) => latin switch
    {
        "s" => "ś", "z" => "ź", "c" => "ć", "n" => "ń", "dz" => "dź", _ => latin,
    };
}
