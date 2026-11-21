using EelDitor.Core.Text;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>
/// Gives paragraph breaking (<see cref="ParagraphWrap"/>, in Core) the Markdown inline spans it must not split:
/// the editor reads emphasis line by line, so "*three words italic*" broken over two lines would show its stars.
/// </summary>
public static class MarkdownParagraphs
{
    public static void Register() => ParagraphWrap.InlineSpans = Spans;

    private static IEnumerable<(int Start, int End)> Spans(string text)
    {
        if (MarkdownDocument.Parse(text).GetLine(0) is not { } render) return [];
        // Whole-line styles (a heading's text, a done task) are not inline spans.
        return render.Styles
            .Where(s => (s.Style & (MdStyle.Heading | MdStyle.TaskDone)) == 0 && s.End > s.Start)
            .Select(s => (s.Start, s.End))
            .ToList();
    }
}
