using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using CarbonPad.App.Editor;
using CarbonPad.Core.Diff;

namespace CarbonPad.App.Views;

/// <summary>
/// Bottom panel of the comparison: the current line from both sides, one above the other, across the whole window
/// width, with the changed characters highlighted. Whitespace inside changes is made visible (· and →).
/// </summary>
public sealed class DiffLinePanel : Border
{
    private const int MaxChars = 20_000;
    private readonly SelectableTextBlock _leftText = Line();
    private readonly SelectableTextBlock _rightText = Line();
    private readonly TextBlock _leftNumber = Number();
    private readonly TextBlock _rightNumber = Number();
    private readonly Border _leftRow;
    private readonly Border _rightRow;

    public DiffLinePanel()
    {
        BorderThickness = new Thickness(0, 1, 0, 0);
        this.Res(BorderBrushProperty, "BorderSubtleBrush");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        _leftRow = Row(grid, 0, _leftNumber, _leftText);
        _rightRow = Row(grid, 1, _rightNumber, _rightText);
        Child = new ScrollViewer
        {
            Content = grid,
            MaxHeight = 180,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    public void SetFont(FontFamily family, double size)
    {
        foreach (var t in new TextBlock[] { _leftText, _rightText, _leftNumber, _rightNumber })
        {
            t.FontFamily = family;
            t.FontSize = size;
        }
    }

    /// <summary>Shows the given lines (0-based; null = the line does not exist on that side).</summary>
    public void Show(DiffView view)
    {
        var (l, r) = view.CurrentLinePair();
        var diff = view.Diff;
        var p = view.Palette;
        Fill(_leftRow, _leftNumber, _leftText, view.LeftEditor.Document, l, diff.OldInline,
            l is { } ll && diff.BlockAt(ll, fromNew: false) is { } lb ? (lb.Kind == DiffKind.Modified && r is not null ? p.DiffChanged : p.DiffRemoved) : null,
            p.DiffRemovedInline);
        Fill(_rightRow, _rightNumber, _rightText, view.RightEditor.Document, r, diff.NewInline,
            r is { } rr && diff.BlockAt(rr, fromNew: true) is { } rb ? (rb.Kind == DiffKind.Modified && l is not null ? p.DiffChanged : p.DiffAdded) : null,
            p.DiffAddedInline);
    }

    private static void Fill(Border row, TextBlock number, SelectableTextBlock text, AvaloniaEdit.Document.TextDocument doc,
        int? line, IReadOnlyList<InlineChange> inline, IBrush? rowBrush, IBrush changeBrush)
    {
        row.Background = rowBrush ?? Brushes.Transparent;
        text.Inlines!.Clear();
        if (line is not { } n || n >= doc.LineCount)
        {
            number.Text = "—";
            return;
        }
        number.Text = (n + 1).ToString();
        string s = doc.GetText(doc.GetLineByNumber(n + 1));
        bool truncated = s.Length > MaxChars;
        if (truncated) s = s[..MaxChars];

        // Changed ranges on this line, merged and sorted.
        var ranges = inline.Where(c => c.Line == n).Select(c => (Start: Math.Min(c.Start, s.Length), End: Math.Min(c.Start + c.Length, s.Length)))
            .Where(c => c.End > c.Start).OrderBy(c => c.Start).ToList();
        int pos = 0;
        foreach (var (start, end) in ranges)
        {
            if (start < pos) continue;
            if (start > pos) text.Inlines.Add(new Run(s[pos..start]));
            text.Inlines.Add(new Run(Visible(s[start..end])) { Background = changeBrush });
            pos = end;
        }
        if (pos < s.Length) text.Inlines.Add(new Run(s[pos..]));
        if (truncated) text.Inlines.Add(new Run(" …").Res(TextElement.ForegroundProperty, "DimTextBrush"));
    }

    private static string Visible(string changed) => changed.Replace(' ', '·').Replace('\t', '→');

    private static Border Row(Grid grid, int row, TextBlock number, SelectableTextBlock text)
    {
        var border = new Border { Padding = new Thickness(0, 2), Child = text };
        Grid.SetRow(number, row);
        Grid.SetRow(border, row);
        Grid.SetColumn(border, 1);
        grid.Children.Add(number);
        grid.Children.Add(border);
        return border;
    }

    private static SelectableTextBlock Line() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0) };

    private static TextBlock Number() => new TextBlock
    {
        MinWidth = 48,
        TextAlignment = TextAlignment.Right,
        Padding = new Thickness(6, 2),
        VerticalAlignment = VerticalAlignment.Top,
    }.Res(TextBlock.ForegroundProperty, "DimTextBrush");
}
