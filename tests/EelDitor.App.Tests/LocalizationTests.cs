using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using EelDitor.App.Services;

namespace EelDitor.App.Tests;

public class LocalizationTests
{
    private static Dictionary<string, string> Read(string code)
    {
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream($"EelDitor.App.Lang.{code}.json");
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
    }

    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var (code, _) in Loc.Languages) data.Add(code);
        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryLanguage_HasTheSameKeysAndPlaceholdersAsPolish(string code)
    {
        var pl = Read("pl");
        var other = Read(code);
        Assert.Empty(pl.Keys.Except(other.Keys));
        Assert.Empty(other.Keys.Except(pl.Keys));
        var placeholder = new Regex(@"\{\d+\}");
        foreach (var (key, text) in pl)
        {
            var expected = placeholder.Matches(text).Select(m => m.Value).Distinct().Order();
            var actual = placeholder.Matches(other[key]).Select(m => m.Value).Distinct().Order();
            Assert.True(expected.SequenceEqual(actual), $"{code}: {key}");
        }
    }

    [Fact]
    public void Interslavic_HasNoKnownMistakes()
    {
        var isv = Read("isv");
        Assert.Equal("Anuluj", isv["Button.Cancel"]);
        Assert.Equal("Izsěči", isv["Menu.Edit.Cut"]);
        Assert.Equal("Kopijuj", isv["Menu.Edit.Copy"]);
        Assert.Equal("Iznova pročitaj iz diska", isv["Menu.File.Reload"]);
        foreach (var (key, text) in isv)
        {
            Assert.DoesNotContain("prěčrk", text);
            Assert.DoesNotContain("Izrěži", text);
            Assert.DoesNotContain("opir", text); // kopiruj / kopirovano (preferred: kopijuj / kopijovano)
            Assert.DoesNotContain(" s diska", text);
            Assert.DoesNotContain("v vs", text);
            Assert.DoesNotContain("odstranjenje togda", text);
            // Menu and button labels are imperatives, not infinitives.
            if ((key.StartsWith("Menu.") || key.StartsWith("Button.")) && !text.Contains('?'))
                Assert.DoesNotMatch(@"^\p{Lu}\p{L}*(ati|iti|ěti|yti)", text);
        }
    }

    [Fact]
    public void Polish_ButtonsAreImperatives()
    {
        var pl = Read("pl");
        Assert.Equal("Ujednolij do {0}", pl["Info.Unify"]);
        foreach (var (key, text) in pl)
            if ((key.StartsWith("Menu.") || key.StartsWith("Button.") || key.StartsWith("Info.")) && !text.Contains('?'))
                Assert.DoesNotMatch(@"^\p{Lu}\p{L}*(ać|ić|yć|eć|uć)", text);
    }

    [Fact]
    public void InterslavicCyrillic_KeepsTechnicalTermsInLatin()
    {
        var cyr = Read("isv-Cyrl");
        Assert.Equal("Анулуј", cyr["Button.Cancel"]);
        Assert.Contains("(git add)", cyr["Git.Add"]);
        Assert.Contains("Ctrl+Z", cyr["Convert.Tip"]);
        Assert.Contains("{0}", cyr["Find.Position"]);
        Assert.DoesNotMatch("[a-zA-Z]", cyr["Menu.File.Open"]);
    }
}
