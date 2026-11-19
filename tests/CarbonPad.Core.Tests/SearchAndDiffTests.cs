using CarbonPad.Core.Diff;
using CarbonPad.Core.Search;

namespace CarbonPad.Core.Tests;

public class SearchTests
{
    [Fact]
    public void WholeWordAndCase()
    {
        const string text = "cat Cat category cat_x cat";
        Assert.Equal(5, TextSearch.FindAll(text, new SearchOptions("cat")).Count);
        Assert.Equal(3, TextSearch.FindAll(text, new SearchOptions("cat", WholeWord: true)).Count);
        Assert.Equal(2, TextSearch.FindAll(text, new SearchOptions("cat", MatchCase: true, WholeWord: true)).Count);
    }

    [Fact]
    public void ExtendedModeMatchesLineBreaks()
    {
        var hits = TextSearch.FindAll("a\r\nb", new SearchOptions(@"a\r\nb", Mode: SearchMode.Extended));
        Assert.Single(hits);
    }

    [Fact]
    public void RegexReplaceWithGroups()
    {
        var (text, count) = TextSearch.ReplaceAll("x=1, y=2", new SearchOptions(@"(\w)=(\d)", Mode: SearchMode.Regex), "$2:$1");
        Assert.Equal("1:x, 2:y", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void NormalModeReplacementIsLiteral()
    {
        var (text, _) = TextSearch.ReplaceAll("price", new SearchOptions("price"), "$5");
        Assert.Equal("$5", text);
    }

    [Fact]
    public void FindNextWrapsAround()
    {
        var m = TextSearch.FindNext("abc abc", new SearchOptions("abc"), 5, forward: true);
        Assert.Equal(0, m!.Index);
        var back = TextSearch.FindNext("abc abc", new SearchOptions("abc"), 4, forward: false);
        Assert.Equal(0, back!.Index);
    }

    [Fact]
    public void HitsReportLineAndColumn()
    {
        var h = Assert.Single(TextSearch.FindAll("one\r\ntwo\nthree", new SearchOptions("ree")));
        Assert.Equal((3, 3, "three"), (h.Line, h.Column, h.LineText));
    }

    [Fact]
    public async Task FileSearchHonoursFiltersAndExcludes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cp-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.cs"), "needle");
        File.WriteAllText(Path.Combine(dir, "sub", "b.cs"), "x\nneedle needle");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "needle");
        File.WriteAllText(Path.Combine(dir, "bin", "d.cs"), "needle");
        File.WriteAllBytes(Path.Combine(dir, "e.cs"), [0x6E, 0, 0x65, 0x65, 0x64, 0x6C, 0x65]);
        try
        {
            var hits = new List<SearchHit>();
            var (files, count) = await FileSearch.SearchAsync(new FileSearchOptions(dir, "*.cs", "bin"), new SearchOptions("needle"),
                _ => null, found => { lock (hits) hits.AddRange(found); }, CancellationToken.None);
            Assert.Equal(3, count);
            Assert.Equal(3, files);
            Assert.DoesNotContain(hits, h => h.FilePath!.Contains("bin"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DiffTests
{
    [Fact]
    public void DetectsInsertDeleteModify()
    {
        var d = LineDiff.Compute("a\nb\nc\nd", "a\nB\nc\nd\ne");
        Assert.Contains(d.Blocks, b => b.Kind == DiffKind.Modified && b.OldStart == 1 && b.NewStart == 1);
        Assert.Contains(d.Blocks, b => b.Kind == DiffKind.Inserted && b.NewStart == 4);
        Assert.NotEmpty(d.NewInline);
    }

    [Fact]
    public void IgnoresLineTerminatorDifferences()
    {
        Assert.True(LineDiff.Compute("a\r\nb", "a\nb").Identical);
    }

    [Fact]
    public void MapsLinesAcrossInsertedBlock()
    {
        var d = LineDiff.Compute("a\nb\nc", "a\nx\ny\nb\nc");
        Assert.Equal(1, d.MapNewToOld(3)); // b
        Assert.Equal(2, d.MapNewToOld(4)); // c
        Assert.Equal(4, d.MapOldToNew(2));
    }

    [Fact]
    public void CounterpartPairsModifiedLinesAndIsNullForInsertions()
    {
        var d = LineDiff.Compute("a\nb\nc\nd", "a\nB\nx\nc\nd\ne");
        Assert.Equal(0, d.Counterpart(0, fromNew: true));     // unchanged
        Assert.Equal(1, d.Counterpart(1, fromNew: true));     // B ↔ b
        Assert.Null(d.Counterpart(2, fromNew: true));         // x only on the right
        Assert.Equal(2, d.Counterpart(3, fromNew: true));     // c
        Assert.Null(d.Counterpart(5, fromNew: true));         // e appended
        Assert.Equal(1, d.Counterpart(1, fromNew: false));
        Assert.Equal(4, d.Counterpart(3, fromNew: false));    // d
        Assert.NotNull(d.BlockAt(2, fromNew: true));
        Assert.Null(d.BlockAt(0, fromNew: true));
    }
}
