using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using EelDitor.Core.Text;

namespace EelDitor.App.Editor;

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
            if (w.Offset == offset) return new CyrillicWordElement(w.Text, w.Length);
        return null;
    }

    public void Dispose()
    {
        _area.TextView.ElementGenerators.Remove(this);
    }
}

/// <summary>A word drawn in Cyrillic: one visual column per Latin character (see <see cref="WholeElement"/>).</summary>
internal sealed class CyrillicWordElement(string text, int documentLength) : WholeElement(documentLength)
{
    public override Avalonia.Media.TextFormatting.TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var p = TextRunProperties;
        var formatted = new Avalonia.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            Avalonia.Media.FlowDirection.LeftToRight, p.Typeface, p.FontRenderingEmSize, p.ForegroundBrush);
        return new WordRun(p, formatted, context.TextView.DefaultLineHeight, RunLength(startVisualColumn));
    }

    private sealed class WordRun(Avalonia.Media.TextFormatting.TextRunProperties properties, Avalonia.Media.FormattedText text, double height, int length)
        : Avalonia.Media.TextFormatting.DrawableTextRun
    {
        public override int Length { get; } = length;
        public override Avalonia.Media.TextFormatting.TextRunProperties Properties { get; } = properties;
        public override Avalonia.Size Size => new(text.WidthIncludingTrailingWhitespace, height);
        public override double Baseline => text.Baseline + (height - text.Height) / 2;
        public override void Draw(Avalonia.Media.DrawingContext context, Avalonia.Point origin) =>
            context.DrawText(text, new Avalonia.Point(origin.X, origin.Y + (height - text.Height) / 2));
    }
}
