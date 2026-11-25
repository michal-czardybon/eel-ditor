using EelDitor.Core.Diff;

namespace EelDitor.Core.Tests;

/// <summary>Each change of the word diff can be reverted on its own, exactly (whitespace included).</summary>
public class WordDiffRevertTests
{
    [Fact]
    public void RevertingOneWordChange_ChangesOnlyThatWord()
    {
        const string oldText = "Ala ma kota.\nPies jest duży.";
        const string newText = "Ala ma psa.\nPies jest mały.";
        var w = WordDiff.Compute(oldText, newText);
        Assert.Equal(2, w.Changes.Count);
        Assert.Equal("Ala ma kota.\nPies jest mały.", WordDiff.Revert(oldText, newText, w.Reverts[0]));
        Assert.Equal("Ala ma psa.\nPies jest duży.", WordDiff.Revert(oldText, newText, w.Reverts[1]));
    }

    [Fact]
    public void ARemovedWord_ComesBackWithItsSpace()
    {
        // The word view marks "b" only; reverting must put back "b " too, not "bc".
        const string oldText = "a b c";
        const string newText = "a c";
        var w = WordDiff.Compute(oldText, newText);
        Assert.Equal(oldText, WordDiff.Revert(oldText, newText, Assert.Single(w.Reverts)));
    }

    [Fact]
    public void WholeLines_InsertedOrDeleted_AreRevertedAsLines()
    {
        const string oldText = "one\ntwo\nthree";
        const string newText = "one\nthree\nfour";
        var w = WordDiff.Compute(oldText, newText);
        string text = newText;
        // Reverting changes one by one (recomputing after each) ends with the old text.
        for (int guard = 0; guard < 10; guard++)
        {
            var now = WordDiff.Compute(oldText, text);
            if (now.Changes.Count == 0) break;
            text = WordDiff.Revert(oldText, text, now.Reverts[0]);
        }
        Assert.Equal(oldText, text);
        Assert.NotEmpty(w.Changes);
    }

    [Fact]
    public void RevertingEveryChange_GivesTheOldText_RandomTexts()
    {
        var random = new Random(11);
        string[] words = ["ala", "ma", "kota", "psa", "i", "dom", "duży", "mały", "", "  "];
        string Text()
        {
            var lines = Enumerable.Range(0, random.Next(1, 6)).Select(_ =>
                string.Join(" ", Enumerable.Range(0, random.Next(0, 8)).Select(_ => words[random.Next(words.Length)])));
            return string.Join("\n", lines);
        }
        for (int run = 0; run < 300; run++)
        {
            string oldText = Text(), newText = Text(), text = newText;
            for (int guard = 0; guard < 100; guard++)
            {
                var now = WordDiff.Compute(oldText, text);
                if (now.Changes.Count == 0) break;
                // Any change, not just the first one.
                text = WordDiff.Revert(oldText, text, now.Reverts[random.Next(now.Reverts.Count)]);
            }
            // Only whitespace the word diff does not show may differ.
            Assert.True(WordDiff.Compute(oldText, text).Changes.Count == 0, $"{oldText}\n---\n{newText}\n---\n{text}");
        }
    }
}
