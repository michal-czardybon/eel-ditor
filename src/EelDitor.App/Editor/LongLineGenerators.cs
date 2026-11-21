using System.Reflection;
using AvaloniaEdit.Rendering;

namespace EelDitor.App.Editor;

/// <summary>
/// AvaloniaEdit builds lines longer than <see cref="VisualLine.LENGTH_LIMIT"/> (3000 characters) as plain text, without
/// running the element generators: no hidden Markdown markers, no table padding, no table grid. Tables written by tools
/// often have such rows (long cells). This line transformer, which runs first, builds the elements of those lines
/// itself, with the same rules as AvaloniaEdit's own construction, and puts them in with the public
/// <see cref="VisualLine.ReplaceElement(int, int, VisualLineElement[])"/>.
/// <para>New elements need their text run properties, which AvaloniaEdit sets through an internal method; when that
/// method is missing (another AvaloniaEdit version), nothing is done and the colorizer's fallback applies.</para>
/// </summary>
internal sealed class LongLineGenerators(TextView textView) : IVisualLineTransformer
{
    private static readonly MethodInfo? SetTextRunProperties =
        typeof(VisualLineElement).GetMethod("SetTextRunProperties", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(VisualLineElementTextRunProperties)]);

    /// <summary>Long lines get their generated elements (false: AvaloniaEdit's plain text).</summary>
    public static bool Supported => SetTextRunProperties is not null;

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        var visual = context.VisualLine;
        var line = visual.FirstDocumentLine;
        if (!Supported || line.Length <= VisualLine.LENGTH_LIMIT || elements.Count == 0) return;
        var generators = textView.ElementGenerators.ToArray();
        if (generators.Length == 0) return;
        var built = new List<VisualLineElement>();
        foreach (var g in generators) g.StartGeneration(context);
        try
        {
            // AvaloniaEdit's VisualLine.PerformVisualElementConstruction, for one document line.
            int offset = line.Offset, end = line.Offset + line.Length, ask = 0;
            var interest = new int[generators.Length];
            while (offset + ask <= end)
            {
                int pieceEnd = end;
                for (int i = 0; i < generators.Length; i++)
                {
                    interest[i] = generators[i].GetFirstInterestedOffset(offset + ask);
                    if (interest[i] >= offset && interest[i] < pieceEnd) pieceEnd = interest[i];
                }
                if (pieceEnd > offset)
                {
                    built.Add(new VisualLineText(visual, pieceEnd - offset));
                    offset = pieceEnd;
                }
                ask = 1;
                for (int i = 0; i < generators.Length; i++)
                {
                    if (interest[i] != offset || generators[i].ConstructElement(offset) is not { } element) continue;
                    // An element reaching past the line (a fold) would change the visual line: not supported here.
                    if (offset + element.DocumentLength > end) return;
                    built.Add(element);
                    if (element.DocumentLength > 0)
                    {
                        ask = 0;
                        offset += element.DocumentLength;
                        break;
                    }
                }
            }
        }
        finally
        {
            foreach (var g in generators) g.FinishGeneration();
        }
        if (built.Sum(e => e.DocumentLength) != elements.Sum(e => e.DocumentLength)) return;
        var properties = elements[0].TextRunProperties;
        foreach (var e in built) SetTextRunProperties!.Invoke(e, [new VisualLineElementTextRunProperties(properties)]);
        visual.ReplaceElement(0, elements.Count, [.. built]);
    }
}
