using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace EelDitor.App.Views;

/// <summary>
/// A mini toolbar: small, low buttons a little apart, on a strip across the whole panel with the window's chrome
/// colour and a line above and below, so it separates what is above it from what is below (like the main toolbar).
/// The list modes of the open files and the actions of the Git tab look the same.
/// </summary>
internal static class ButtonBar
{
    public const double Height = 22;

    /// <summary>
    /// Styles the buttons and puts them into <paramref name="panel"/> (a new one when null); returns the strip that
    /// holds the panel, to be placed across the whole width. A wrap panel puts buttons that do not fit on a next row.
    /// </summary>
    public static Border Strip(IReadOnlyList<Button> buttons, Panel? panel = null)
    {
        panel ??= new StackPanel();
        if (panel is StackPanel stack)
        {
            stack.Orientation = Orientation.Horizontal;
            stack.Spacing = 4;
        }
        foreach (var b in buttons)
        {
            b.FontSize = 12;
            b.Padding = new Thickness(10, 0);
            b.MinHeight = 0;
            b.Height = Height;
            b.VerticalContentAlignment = VerticalAlignment.Center;
            b.CornerRadius = new CornerRadius(3);
            if (panel is WrapPanel) b.Margin = new Thickness(0, 2, 4, 2);
            panel.Children.Add(b);
        }
        return new Border { Child = panel, Padding = new Thickness(6, panel is WrapPanel ? 2 : 4), BorderThickness = new Thickness(0, 1) }
            .Res(Border.BackgroundProperty, "ChromeBrush").Res(Border.BorderBrushProperty, "BorderSubtleBrush");
    }
}
