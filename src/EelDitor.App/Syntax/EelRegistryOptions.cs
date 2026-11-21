using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;

namespace EelDitor.App.Syntax;

/// <summary>
/// TextMateSharp's grammar set plus the grammars shipped inside Eel-ditor (embedded <c>Syntax/*.tmLanguage.json</c>).
/// The base class's language lookups are not virtual, so they are hidden with <c>new</c>; code must therefore
/// hold this type, not <see cref="RegistryOptions"/>.
/// </summary>
public sealed class EelRegistryOptions : RegistryOptions, IRegistryOptions
{
    public const string AvcodeScope = "source.avcode";
    public const string AvcodeId = "avcode";

    private static readonly Language Avcode = new()
    {
        Id = AvcodeId,
        Aliases = ["AVCODE"],
        Extensions = [".avcode"],
    };

    private readonly ThemeName _theme;
    private IRawGrammar? _avcodeGrammar;
    private List<Language>? _languages;

    public EelRegistryOptions(ThemeName theme) : base(theme) => _theme = theme;

    public ThemeName Theme => _theme;

    public new IRawGrammar GetGrammar(string scopeName)
    {
        if (scopeName != AvcodeScope) return base.GetGrammar(scopeName);
        if (_avcodeGrammar is null)
        {
            using var stream = typeof(EelRegistryOptions).Assembly.GetManifestResourceStream("EelDitor.App.Syntax.avcode.tmLanguage.json")
                ?? throw new InvalidOperationException("avcode grammar resource is missing");
            using var reader = new StreamReader(stream);
            _avcodeGrammar = GrammarReader.ReadGrammarSync(reader);
        }
        return _avcodeGrammar;
    }

    public new List<Language> GetAvailableLanguages() => _languages ??= [.. base.GetAvailableLanguages(), Avcode];

    public new Language? GetLanguageByExtension(string extension) =>
        string.Equals(extension, ".avcode", StringComparison.OrdinalIgnoreCase) ? Avcode : base.GetLanguageByExtension(extension);

    public new string GetScopeByLanguageId(string languageId) =>
        languageId == AvcodeId ? AvcodeScope : base.GetScopeByLanguageId(languageId);

    public new string? GetScopeByExtension(string extension) =>
        string.Equals(extension, ".avcode", StringComparison.OrdinalIgnoreCase) ? AvcodeScope : base.GetScopeByExtension(extension);
}
