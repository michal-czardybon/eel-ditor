using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace EelDitor.App.Editor;

/// <summary>
/// The last line transformer of the editor: symbols and emoji are never bold, whoever made the text bold (the Markdown
/// view, a TextMate theme in raw mode or in code files). See <see cref="SymbolText"/>.
/// </summary>
internal sealed class SymbolWeight : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        if (!SymbolText.HasSymbol(text)) return;
        foreach (var (start, length, symbols) in SymbolText.Runs(text))
            if (symbols)
                ChangeLinePart(line.Offset + start, line.Offset + start + length, el =>
                {
                    var tf = el.TextRunProperties.Typeface;
                    if (tf.Weight != FontWeight.Normal)
                        el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeight.Normal, tf.Stretch));
                });
    }

    /// <summary>
    /// Adds it after TextMate's colorizer (installed first). The Markdown view's colorizer, added later, keeps its
    /// symbols at normal weight itself.
    /// </summary>
    public static void Install(TextView view) => view.LineTransformers.Add(new SymbolWeight());
}
