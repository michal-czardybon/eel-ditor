using EelDitor.Core.Git;

namespace EelDitor.Core.Tests;

public class ConflictTests
{
    private static string[] L(params string[] lines) => lines;

    [Fact]
    public void FindsTwoWayAndDiff3Conflicts()
    {
        var lines = L(
            "a",
            "<<<<<<< HEAD",
            "mine",
            "=======",
            "theirs",
            ">>>>>>> feature/x",
            "b",
            "<<<<<<< ours",
            "m1",
            "m2",
            "||||||| base",
            "orig",
            "=======",
            ">>>>>>> theirs",
            "c");
        var found = Conflicts.Find(lines);
        Assert.Equal(2, found.Count);
        var first = found[0];
        Assert.Equal((1, 3, 5), (first.Start, first.Separator, first.End));
        Assert.Equal("HEAD", first.CurrentLabel);
        Assert.Equal("feature/x", first.IncomingLabel);
        Assert.Equal(["mine"], Conflicts.Resolve(lines, first, ConflictChoice.Current));
        Assert.Equal(["theirs"], Conflicts.Resolve(lines, first, ConflictChoice.Incoming));
        Assert.Equal(["mine", "theirs"], Conflicts.Resolve(lines, first, ConflictChoice.Both));

        var second = found[1];
        Assert.Equal(10, second.BaseMarker);
        Assert.Equal(["m1", "m2"], Conflicts.Resolve(lines, second, ConflictChoice.Current));
        Assert.Equal(["orig"], Conflicts.Resolve(lines, second, ConflictChoice.Base));
        Assert.Empty(Conflicts.Resolve(lines, second, ConflictChoice.Incoming));
        Assert.True(second.Contains(13));
        Assert.False(second.Contains(14));
    }

    [Theory]
    [InlineData("<<<<<<< a|x|>>>>>>> b")]            // no separator
    [InlineData("<<<<<<< a|x|=======|y")]            // no end
    [InlineData("<<<<<<<< a|x|=======|y|>>>>>>> b")]  // 8 characters: not a marker
    [InlineData("== heading ==|=======")]            // separator alone
    public void IgnoresIncompleteMarkers(string text) => Assert.Empty(Conflicts.Find(text.Split('|')));
}
