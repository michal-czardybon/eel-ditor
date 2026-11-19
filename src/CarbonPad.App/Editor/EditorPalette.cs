using Avalonia.Media;

namespace CarbonPad.App.Editor;

/// <summary>Restrained colours for editor decorations: one accent, otherwise greys.</summary>
public sealed class EditorPalette
{
    public required IBrush Background { get; init; }
    public required IBrush Foreground { get; init; }
    public required IBrush Dim { get; init; }
    public required IBrush Accent { get; init; }
    public required IBrush CodeBackground { get; init; }
    public required IBrush CodeForeground { get; init; }
    public required IBrush CurrentLine { get; init; }
    public required IBrush Selection { get; init; }
    public required IBrush LineNumbers { get; init; }
    public required IBrush Whitespace { get; init; }
    public required IBrush SearchHighlight { get; init; }
    public required IBrush TableSelection { get; init; }
    public required IBrush DiffAdded { get; init; }
    public required IBrush DiffRemoved { get; init; }
    public required IBrush DiffChanged { get; init; }
    public required IBrush DiffAddedInline { get; init; }
    public required IBrush DiffRemovedInline { get; init; }
    public required IBrush DiffFiller { get; init; }
    public required IBrush Highlight { get; init; }

    private static SolidColorBrush B(uint argb) => new(Color.FromUInt32(argb));

    public static EditorPalette Dark { get; } = new()
    {
        Background = B(0xFF1E1F22),
        Foreground = B(0xFFD4D4D4),
        Dim = B(0xFF6E7681),
        Accent = B(0xFF6CB6FF),
        CodeBackground = B(0xFF2B2D31),
        CodeForeground = B(0xFFCE9178),
        CurrentLine = B(0xFF26282C),
        Selection = B(0xFF264F78),
        LineNumbers = B(0xFF5C6370),
        Whitespace = B(0xFF4B5263),
        SearchHighlight = B(0x6646A0FF),
        TableSelection = B(0x5546A0FF),
        DiffAdded = B(0x3326A148),
        DiffRemoved = B(0x33E5534B),
        DiffChanged = B(0x22D29922),
        DiffAddedInline = B(0x6626A148),
        DiffRemovedInline = B(0x66E5534B),
        DiffFiller = B(0x18FFFFFF),
        Highlight = B(0x55C8A600),
    };

    public static EditorPalette Light { get; } = new()
    {
        Background = B(0xFFFFFFFF),
        Foreground = B(0xFF1F2328),
        Dim = B(0xFF8C959F),
        Accent = B(0xFF0969DA),
        CodeBackground = B(0xFFF0F2F4),
        CodeForeground = B(0xFF953800),
        CurrentLine = B(0xFFF5F7FA),
        Selection = B(0xFFB6D7FF),
        LineNumbers = B(0xFF8C959F),
        Whitespace = B(0xFFC0C6CC),
        SearchHighlight = B(0x660969DA),
        TableSelection = B(0x440969DA),
        DiffAdded = B(0x3326A148),
        DiffRemoved = B(0x33CF222E),
        DiffChanged = B(0x22BF8700),
        DiffAddedInline = B(0x6626A148),
        DiffRemovedInline = B(0x66CF222E),
        DiffFiller = B(0x14000000),
        Highlight = B(0x66FFD33D),
    };
}
