using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace EelDitor.App.Views;

/// <summary>
/// Shows a path that fits the available width by dropping whole leading folders:
/// "…\src\EelDitor.Core\Git" rather than "…rbonPad\src\EelDitor.Core\Git". The full path is in the tooltip.
/// </summary>
public sealed class PathTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> FullPathProperty =
        AvaloniaProperty.Register<PathTextBlock, string?>(nameof(FullPath));

    static PathTextBlock() => AffectsMeasure<PathTextBlock>(FullPathProperty);

    public string? FullPath
    {
        get => GetValue(FullPathProperty);
        set => SetValue(FullPathProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        string full = FullPath ?? "";
        string text = Fit(full, availableSize.Width - Padding.Left - Padding.Right);
        if (Text != text) SetCurrentValue(TextProperty, text);
        TextTrimming = TextTrimming.CharacterEllipsis; // only when even the last folder does not fit
        return base.MeasureOverride(availableSize);
    }

    internal string Fit(string full, double width)
    {
        if (double.IsInfinity(width) || full.Length == 0 || TextWidth(full) <= width) return full;
        char sep = full.Contains('\\') ? '\\' : '/';
        var parts = full.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        for (int keep = parts.Length - 1; keep >= 1; keep--)
        {
            string candidate = "…" + sep + string.Join(sep, parts[^keep..]);
            if (TextWidth(candidate) <= width || keep == 1) return candidate;
        }
        return full;
    }

    private double TextWidth(string text)
    {
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight), FontSize, Foreground);
        return layout.Width;
    }
}
