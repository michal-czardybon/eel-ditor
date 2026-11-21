using EelDitor.Core.Text;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>Reflow on random Markdown paragraphs: rules that always hold.</summary>
public class ParagraphWrapPropertyTests
{
    private static readonly string[] Words =
    [
        "Ala", "ma", "kota.", "To", "jest", "**pogrubiony**", "*kursywa*", "`kod`", "`` ` ``", "`a b`", "[link](http://x.pl)",
        "zdanie.", "Koniec!", "np.", "m.in.", "długieeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeewordddd",
        "**dwa słowa**", "*trzy słowa kursywą*", "~~skreślone~~", "==zakreślone==", "„cytat”", "(nawias)", "e.g.", "Q", "123", "`kod z spacją`",
    ];

    private static string Paragraph(Random rnd, int words) => string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rnd.Next(Words.Length)]));

    /// <summary>The visible words of the lines (what Markdown shows), outside code joined as one text.</summary>
    private static string Shown(IEnumerable<string> lines)
    {
        string text = string.Join("\n", lines);
        var md = MarkdownDocument.Parse(text);
        var all = text.Split('\n');
        // Spacing aside (spaces inside code at the edges of a line are not kept apart).
        // A quote's ">" shows at the start of every line: not part of the paragraph's text.
        return string.Join(" ", all.Select((l, i) => EelDitor.Markdown.InlineStyleEdit.Visible(l, md.GetLine(i)).Text)
            .Select(t => t.StartsWith("> ", StringComparison.Ordinal) ? t[2..] : t)
            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Theory]
    [InlineData(WrapMode.Width80, "", "")]
    [InlineData(WrapMode.Width120, "", "")]
    [InlineData(WrapMode.Sentences, "", "")]
    [InlineData(WrapMode.Width80, "- ", "  ")]
    [InlineData(WrapMode.Width80, "2. ", "   ")]
    [InlineData(WrapMode.Width120, "> ", "> ")]
    public void Reflow_KeepsTheWordsAndTheMeaning_StaysWithinTheWidth_AndIsStable(WrapMode mode, string first, string next)
    {
        EelDitor.App.Editor.MarkdownParagraphs.Register();
        var failures = new List<string>();
        for (int seed = 0; seed < 300; seed++)
        {
            var rnd = new Random(seed);
            // A paragraph written over a few lines, as a person would.
            var lines = new List<string>();
            int count = rnd.Next(1, 5);
            for (int i = 0; i < count; i++) lines.Add((i == 0 ? first : next) + Paragraph(rnd, rnd.Next(1, 25)));
            if (ParagraphWrap.FindParagraph(lines, 0, true) is not { } p || p != (0, lines.Count - 1)) continue;
            var result = ParagraphWrap.Reflow(lines, 0, lines.Count - 1, mode, true);
            string words = string.Join(" ", lines.Select((l, i) => l[(i == 0 ? first : next).Length..]).SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            string after = string.Join(" ", result.Select((l, i) => l[(i == 0 ? first : next).Length..]).SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            string where = $"seed {seed}: {string.Join(" ⏎ ", lines)}\n  → {string.Join(" ⏎ ", result)}";
            if (after != words) failures.Add("words changed: " + where);
            else if (Shown(result) != Shown([first + words]) && !result.Any(l => l.TrimEnd().EndsWith("  ")))
                failures.Add("shows something else: " + where + "\n  shows: " + Shown(result) + "\n  not:   " + Shown([first + words]));
            else if (mode.Width() is int width && result.Any(l => l.Length > width && l.TrimStart().Contains(' ') && !l.Contains('`')))
                failures.Add("too long: " + where);
            else if (!ParagraphWrap.Reflow(result, 0, result.Count - 1, mode, true).SequenceEqual(result))
                failures.Add("not stable: " + where);
            else if (result.Skip(1).Any(l => !l.StartsWith(next, StringComparison.Ordinal)))
                failures.Add("lost the indentation: " + where);
            if (failures.Count >= 5) break;
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }
}
