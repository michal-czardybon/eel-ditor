using System.Reflection;
using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace EelDitor.App.Services;

/// <summary>
/// UI string table. Every user-visible text goes through here so translations can be added later
/// as <c>Lang/&lt;code&gt;.json</c> embedded resources. Missing keys fall back to English, then Polish, then the key itself.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _strings = new();
    private static Dictionary<string, string> _fallback = new();

    public static string Language { get; private set; } = "pl";

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("pl", "Polski"),
        ("en", "English"),
        ("isv", "Medžuslovjansky"),
        ("isv-Cyrl", "Меджусловјанскы"),
    ];

    /// <summary>Polish for a Polish system, English otherwise.</summary>
    public static string SystemDefault =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl" ? "pl" : "en";

    /// <summary>Loads a language; missing keys fall back to English, then Polish (the complete source table).</summary>
    public static void Load(string language)
    {
        var pl = Read("pl");
        var en = Read("en");
        _fallback = new Dictionary<string, string>(pl, StringComparer.Ordinal);
        if (language != "pl") foreach (var (k, v) in en) _fallback[k] = v;
        _strings = language switch { "pl" => pl, "en" => en, _ => Read(language) };
        Language = _strings.Count > 0 ? language : "pl";
        if (_strings.Count == 0) { _strings = pl; _fallback = pl; }
    }

    private static Dictionary<string, string> Read(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"EelDitor.App.Lang.{code}.json");
        if (stream is null) return new();
        using var doc = JsonDocument.Parse(stream);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.GetString() ?? "";
        return dict;
    }

    public static string T(string key) =>
        _strings.TryGetValue(key, out var s) ? s : _fallback.TryGetValue(key, out s) ? s : key;

    public static string T(string key, params object[] args) => string.Format(T(key), args);
}

/// <summary>XAML: <c>{loc:T Menu.File}</c>.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
