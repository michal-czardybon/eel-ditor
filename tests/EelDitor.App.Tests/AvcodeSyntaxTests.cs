using EelDitor.App.Syntax;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace EelDitor.App.Tests;

public class AvcodeSyntaxTests
{
    private static readonly EelRegistryOptions Options = new(ThemeName.DarkPlus);

    /// <summary>Tokenizes whole text; returns (text, scopes joined by space) per token, lines separated by a null-text entry.</summary>
    private static List<(string Text, string Scopes)> Tokens(string text)
    {
        var grammar = new Registry(Options).LoadGrammar(EelRegistryOptions.AvcodeScope);
        Assert.NotNull(grammar);
        var result = new List<(string, string)>();
        IStateStack? stack = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var r = grammar.TokenizeLine(line, stack, TimeSpan.MaxValue);
            foreach (var t in r.Tokens)
                result.Add((line.Substring(t.StartIndex, Math.Min(t.EndIndex, line.Length) - t.StartIndex), string.Join(' ', t.Scopes)));
            stack = r.RuleStack;
        }
        return result;
    }

    private static string ScopesOf(List<(string Text, string Scopes)> tokens, string text, int occurrence = 0) =>
        tokens.Where(t => t.Text == text).Skip(occurrence).First().Scopes;

    [Fact]
    public void Extension_And_Language_Are_Registered()
    {
        Assert.Equal(EelRegistryOptions.AvcodeId, Options.GetLanguageByExtension(".avcode")?.Id);
        Assert.Equal(EelRegistryOptions.AvcodeScope, Options.GetScopeByLanguageId("avcode"));
        Assert.Contains(Options.GetAvailableLanguages(), l => l.Id == "avcode");
        Assert.Equal("csharp", Options.GetLanguageByExtension(".cs")?.Id); // the stock languages still work
    }

    [Fact]
    public void Core_Constructs_Get_Distinct_Scopes()
    {
        const string src = """
            //:USETYPE WORKERTASK
            //:HIDDENPORT[inRepeat] TRUE
            public Main()
            {

            section ACQUIRE:
            	//Plain comment
            	EnumerateImages
            	(
            		inDirectory: "images\\Code128"
            		inRepeat: True
            		inDelay: 0.5
            		outImage: con_ef9f635f_55bb_4367_9a5c_6a4f0b1e2b5f
            		outFilePath: Nil
            		inFileType: Bilinear
            		inScanPath: Path<bars.f2cae71c.Path.avdata>
            	)
            }
            """;
        var t = Tokens(src);
        Assert.Contains("meta.preprocessor", ScopesOf(t, "USETYPE"));
        Assert.Contains("storage.modifier", ScopesOf(t, "public"));
        Assert.Contains("entity.name.function", ScopesOf(t, "Main"));
        Assert.Contains("keyword.control.section", ScopesOf(t, "section"));
        Assert.Contains("entity.name.section", ScopesOf(t, "ACQUIRE"));
        Assert.Contains("comment.line", ScopesOf(t, "//Plain comment"));
        Assert.Contains("entity.name.function", ScopesOf(t, "EnumerateImages"));
        Assert.Contains("variable.parameter", ScopesOf(t, "inDirectory"));
        Assert.Contains("string.quoted", ScopesOf(t, "images"));
        Assert.Contains("constant.character.escape", ScopesOf(t, "\\\\"));
        Assert.Contains("constant.language", ScopesOf(t, "True"));
        Assert.Contains("constant.language", ScopesOf(t, "Nil"));
        Assert.Contains("constant.numeric", ScopesOf(t, "0.5"));
        Assert.Contains("variable.other.connection", ScopesOf(t, "con_ef9f635f_55bb_4367_9a5c_6a4f0b1e2b5f"));
        Assert.Contains("constant.other.enum", ScopesOf(t, "Bilinear"));
        Assert.Contains("string.unquoted.avdata", ScopesOf(t, "bars.f2cae71c.Path.avdata"));
    }

    [Fact]
    public void Enum_Members_Declarations_And_Expressions()
    {
        const string src = """
            enum Difficulty
            {
            	Easy
            	Hard
            }

            extern public Real PixelPerMilimeter = 9.330000
            public ObstaclesCollisions(RegionArray inObstacles, Bool outFlip) step
            {
            	expression
            	{
            		Integer inValue
            		Bool outFlip as Flip = "not(inValue mod 2 == 0)"
            	}
            }
            """;
        var t = Tokens(src);
        Assert.Contains("storage.type.enum", ScopesOf(t, "enum"));
        Assert.Contains("variable.other.enummember", ScopesOf(t, "Easy"));
        Assert.Contains("variable.other.enummember", ScopesOf(t, "Hard"));
        Assert.Contains("storage.modifier", ScopesOf(t, "extern"));
        Assert.Contains("entity.name.type", ScopesOf(t, "Real"));
        Assert.Contains("constant.numeric", ScopesOf(t, "9.330000"));
        Assert.Contains("entity.name.function", ScopesOf(t, "ObstaclesCollisions"));
        Assert.Contains("entity.name.type", ScopesOf(t, "RegionArray"));
        Assert.Contains("storage.modifier.step", ScopesOf(t, "step"));
        Assert.Contains("string.quoted", ScopesOf(t, "not(inValue mod 2 == 0)"));
        // After the enum block ends, its member rule must not leak: a bare filter name is a function again.
        Assert.DoesNotContain("enummember", ScopesOf(t, "expression"));
    }

    [Fact]
    public void Every_Example_File_Tokenizes_Without_Leaking_State()
    {
        const string dir = @"C:\ProgramData\Aurora Vision\Aurora Vision Studio Trunk Professional\Examples";
        if (!Directory.Exists(dir)) return; // examples are only on machines with Aurora Vision installed
        var grammar = new Registry(Options).LoadGrammar(EelRegistryOptions.AvcodeScope);
        foreach (var file in Directory.EnumerateFiles(dir, "*.avcode", SearchOption.AllDirectories))
        {
            IStateStack? stack = null;
            foreach (var line in File.ReadAllLines(file))
                stack = grammar.TokenizeLine(line, stack, TimeSpan.FromSeconds(2)).RuleStack;
            Assert.True(stack is null || stack.Depth <= 1, $"{file}: grammar rule left open at end of file");
        }
    }
}
