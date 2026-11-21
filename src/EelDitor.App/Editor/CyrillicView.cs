using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using EelDitor.Core.Text;
using EelDitor.Markdown;

namespace EelDitor.App.Editor;

/// <summary>The alphabets Latin text can be shown in (Encoding → Show Latin text in Cyrillic).</summary>
public static class CyrillicViews
{
    /// <summary>Ids in menu order: the transliterations, then Polish A and B.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Transliteration.All.Select(t => t.Id), "plA", "plB"];

    /// <summary>Converts one word (a run of letters) for the view with this id.</summary>
    public static Func<string, string> Converter(string id) => id switch
    {
        "plA" => w => PolishCyrillic.WordToCyrillic(w, CyrillicVariant.A),
        "plB" => w => PolishCyrillic.WordToCyrillic(w, CyrillicVariant.B),
        _ => Transliteration.All.First(t => t.Id == id).ToCyrillic,
    };

    public static string LabelKey(string id) => id switch { "plA" => "Cyrillic.ViewA", "plB" => "Cyrillic.ViewB", _ => $"Cyrillic.Lang.{id}" };
}

/// <summary>
/// Shows Latin text in Cyrillic without changing the document: every word is drawn as its Cyrillic form. The editor
/// is read-only while this view is on. Markdown formatting stays: the generator runs after the Markdown ones, skips
/// hidden markup and code, splits words where a style changes (so the colorizer styles them), and in a table row
/// keeps each word as wide as its Latin source, so the columns stay aligned.
/// </summary>
public sealed class CyrillicViewGenerator : VisualLineElementGenerator, IDisposable
{
    private readonly List<(int Offset, int Length, string Text)> _words = [];
    private readonly TextArea _area;
    private readonly Func<string, string> _convert;
    private bool _tableRow;

    public CyrillicViewGenerator(string id, TextArea area, MarkdownView? markdown)
    {
        Id = id;
        _convert = CyrillicViews.Converter(id);
        _area = area;
        Markdown = markdown;
        if (markdown is not null) markdown.WordTransform = _convert;
        area.TextView.ElementGenerators.Add(this);
    }

    public string Id { get; }

    public MarkdownView? Markdown { get; }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _words.Clear();
        var line = context.VisualLine.FirstDocumentLine;
        string text = context.Document.GetText(line.Offset, line.Length);
        var render = Markdown?.RenderFor(line);
        _tableRow = render?.IsTableRow == true;
        if (render?.Kind is MdLineKind.Code or MdLineKind.FenceDelimiter) return;
        // Style boundaries (a word is cut there) and characters left in Latin (code, hidden markup).
        var cuts = new HashSet<int>();
        var keep = new bool[text.Length];
        if (render is not null)
        {
            foreach (var s in render.Styles)
            {
                cuts.Add(s.Start);
                cuts.Add(s.End);
                if (s.Style.HasFlag(MdStyle.Code))
                    for (int k = s.Start; k < Math.Min(s.End, text.Length); k++) keep[k] = true;
            }
            foreach (var h in render.Hidden)
                for (int k = h.Start; k < Math.Min(h.End, text.Length); k++) keep[k] = true;
        }
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]) || keep[i]) { i++; continue; }
            int end = i + 1;
            while (end < text.Length && char.IsLetter(text[end]) && !keep[end] && !cuts.Contains(end)) end++;
            string word = text[i..end];
            string converted = _convert(word);
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
            if (w.Offset == offset) return new CyrillicWordElement(w.Text, w.Length, _tableRow);
        return null;
    }

    public void Dispose()
    {
        _area.TextView.ElementGenerators.Remove(this);
        if (Markdown is not null && ReferenceEquals(Markdown.WordTransform, _convert)) Markdown.WordTransform = null;
    }

    /// <summary>
    /// The shown text of a table cell in the grid, converted word by word; every character keeps the style of the
    /// Latin text it comes from (a word takes the style of its first letter).
    /// </summary>
    public static (string Text, MdStyle[] Styles) ConvertStyled(string text, MdStyle[] styles, Func<string, string> convert)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var outStyles = new List<MdStyle>(styles.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]) || styles[i].HasFlag(MdStyle.Code)) { sb.Append(text[i]); outStyles.Add(styles[i]); i++; continue; }
            int end = i + 1;
            while (end < text.Length && char.IsLetter(text[end]) && styles[end] == styles[i]) end++;
            string converted = convert(text[i..end]);
            sb.Append(converted);
            for (int k = 0; k < converted.Length; k++) outStyles.Add(styles[i]);
            i = end;
        }
        return (sb.ToString(), outStyles.ToArray());
    }
}

/// <summary>
/// A word drawn in Cyrillic: one visual column per Latin character (see <see cref="WholeElement"/>). In a table row it
/// is as wide as the Latin word, so the virtual padding still lines the columns up.
/// </summary>
internal sealed class CyrillicWordElement(string text, int documentLength, bool keepWidth = false) : WholeElement(documentLength)
{
    public override Avalonia.Media.TextFormatting.TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var p = TextRunProperties;
        var formatted = new Avalonia.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            Avalonia.Media.FlowDirection.LeftToRight, p.Typeface, p.FontRenderingEmSize, p.ForegroundBrush);
        if (p.TextDecorations is { } decorations) formatted.SetTextDecorations(decorations);
        double width = formatted.WidthIncludingTrailingWhitespace;
        if (keepWidth) width = Math.Max(width, DocumentLength * context.TextView.WideSpaceWidth);
        double height = Math.Max(context.TextView.DefaultLineHeight, formatted.Height);
        return new WordRun(p, formatted, width, height, RunLength(startVisualColumn));
    }

    private sealed class WordRun(Avalonia.Media.TextFormatting.TextRunProperties properties, Avalonia.Media.FormattedText text, double width, double height, int length)
        : Avalonia.Media.TextFormatting.DrawableTextRun
    {
        public override int Length { get; } = length;
        public override Avalonia.Media.TextFormatting.TextRunProperties Properties { get; } = properties;
        public override Avalonia.Size Size => new(width, height);
        public override double Baseline => text.Baseline + (height - text.Height) / 2;
        public override void Draw(Avalonia.Media.DrawingContext context, Avalonia.Point origin) =>
            context.DrawText(text, new Avalonia.Point(origin.X, origin.Y + (height - text.Height) / 2));
    }
}
