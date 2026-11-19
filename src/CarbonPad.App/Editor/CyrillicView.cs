using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CarbonPad.Core.Text;

namespace CarbonPad.App.Editor;

/// <summary>
/// Shows Polish Latin text in Cyrillic (variant A or B) without changing the document: every word is drawn
/// as its Cyrillic form. The editor is read-only while this view is on.
/// </summary>
public sealed class CyrillicViewGenerator : VisualLineElementGenerator, IDisposable
{
    private readonly List<(int Offset, int Length, string Text)> _words = [];
    private readonly TextArea _area;

    public CyrillicViewGenerator(CyrillicVariant variant, TextArea area)
    {
        Variant = variant;
        _area = area;
        area.TextView.ElementGenerators.Insert(0, this);
    }

    public CyrillicVariant Variant { get; }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _words.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        string text = context.Document.GetText(line.Offset, line.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }
            int end = i;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            string word = text[i..end];
            string converted = PolishCyrillic.WordToCyrillic(word, Variant);
            if (converted != word) _words.Add((line.Offset + i, end - i, converted));
            i = end;
        }
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        foreach (var w in _words)
            if (w.Offset >= startOffset) return w.Offset;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var w in _words)
            if (w.Offset == offset) return new FormattedTextElement(w.Text, w.Length);
        return null;
    }

    public void Dispose()
    {
        _area.TextView.ElementGenerators.Remove(this);
    }
}
