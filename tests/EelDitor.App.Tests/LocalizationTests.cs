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
}
