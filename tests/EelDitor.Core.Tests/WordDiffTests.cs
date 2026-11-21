using EelDitor.Core.Diff;

namespace EelDitor.Core.Tests;

public class WordDiffTests
{
    /// <summary>Renders the result like git: [-deleted-]{+inserted+}.</summary>
    private static string Git(string oldText, string newText)
    {
        var r = WordDiff.Compute(oldText, newText);
        var sb = new System.Text.StringBuilder();
        int pos = 0;
        foreach (var s in r.Spans)
        {
            sb.Append(r.Text, pos, s.Start - pos);
            sb.Append(s.Kind == WordChange.Deleted ? "[-" : "{+").Append(r.Text, s.Start, s.Length).Append(s.Kind == WordChange.Deleted ? "-]" : "+}");
            pos = s.End;
        }
        sb.Append(r.Text, pos, r.Text.Length - pos);
        return sb.ToString();
    }

    [Theory]
    [InlineData("ala ma kota", "ala ma psa", "ala ma [-kota-]{+psa+}")]
    [InlineData("ala ma kota", "ala ma kota", "ala ma kota")]
    [InlineData("a b c", "a c", "a [-b-]c")]
    [InlineData("a c", "a b c", "a {+b+} c")]
    [InlineData("one\ntwo\nthree", "one\n2\nthree", "one\n[-two-]{+2+}\nthree")]
    [InlineData("one\ntwo\nthree", "one\nthree", "one\n[-two\n-]three")]
    [InlineData("one\nthree", "one\ntwo\nthree", "one\n{+two\n+}three")]
    [InlineData("a\nb", "a", "a\n[-b-]")]
    [InlineData("a", "a\nb", "a\n{+b+}")]
    [InlineData("x = f(a, b);", "x = f(a, c);", "x = f(a, [-b);-]{+c);+}")]
    public void LooksLikeGitWordDiff(string oldText, string newText, string expected) =>
        Assert.Equal(expected, Git(oldText, newText));

    [Fact]
    public void TheNewTextIsKept_AndChangesAreCounted()
    {
        var r = WordDiff.Compute("ala ma kota\nbez zmian\nkoniec starego", "ala ma dwa koty\nbez zmian\nkoniec");
        string withoutDeleted = string.Concat(Enumerable.Range(0, r.Text.Length)
            .Where(i => !r.Spans.Any(s => s.Kind == WordChange.Deleted && i >= s.Start && i < s.End)).Select(i => r.Text[i]));
        Assert.Equal("ala ma dwa koty\nbez zmian\nkoniec", withoutDeleted);
        Assert.Equal(2, r.Changes.Count);
        Assert.Equal(2, r.DeletedWords);  // kota, starego
        Assert.Equal(2, r.InsertedWords); // dwa koty
    }
}
