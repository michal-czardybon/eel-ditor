using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EelDitor.App.Services;

namespace EelDitor.App.Views;

/// <summary>
/// Keyboard shortcuts as a light-dismiss popup over the window: grouped by menu, two aligned columns,
/// a filter box, scrolls when taller than the window. Closes on Esc, on ✕ or on a click outside.
/// </summary>
public sealed class ShortcutsPopup
{
    private readonly Popup _popup;
    private readonly IReadOnlyList<(string Group, string Label, string Keys)> _rows;
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly TextBox _filter = new() { Watermark = Loc.T("Shortcuts.Filter"), FontSize = 12, MinWidth = 220 };

    public ShortcutsPopup(Control placementTarget, IEnumerable<AppCommand> commands)
    {
        _rows = commands.Where(c => c.Gestures.Length > 0)
            .Select(c => (Group: GroupOf(c.Id), c.Label, Keys: string.Join("   ", c.Gestures.Select(Pretty))))
            .OrderBy(r => GroupOrder.IndexOf(r.Group))
            .ToList();

        var close = new Button { Content = "✕", Padding = new Thickness(8, 2), Background = Brushes.Transparent };
        close.Click += (_, _) => _popup!.IsOpen = false;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        DockPanel.SetDock(_filter, Dock.Right);
        _filter.Margin = new Thickness(12, 0, 6, 0);
        header.Children.Add(_filter);
        header.Children.Add(new TextBlock { Text = Loc.T("Menu.Help.Shortcuts"), FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var scroll = new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0) };
        var panel = new DockPanel { Children = { header, scroll } };
        DockPanel.SetDock(header, Dock.Top);
        var card = new Border
        {
            Child = panel,
            Padding = new Thickness(16, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Width = 620,
            BoxShadow = BoxShadows.Parse("0 6 24 0 #60000000"),
        }.Res(Border.BackgroundProperty, "PanelBrush").Res(Border.BorderBrushProperty, "BorderSubtleBrush");

        _popup = new Popup
        {
            Child = card,
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Center,
            IsLightDismissEnabled = true,
        };
        _filter.TextChanged += (_, _) => Fill();
        _popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; } };
        Fill();
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Add this to a panel of the window (a popup needs a logical parent).</summary>
    public Popup Popup => _popup;

    public void Show()
    {
        if (_popup.PlacementTarget is Control target)
        {
            // Never taller than the window: the list scrolls instead.
            ((Border)_popup.Child!).MaxHeight = Math.Max(240, target.Bounds.Height - 60);
        }
        _popup.IsOpen = true;
        _filter.Focus();
    }

    private static readonly List<string> GroupOrder =
        ["Menu.File", "Menu.Edit", "Menu.Search", "Menu.View", "Menu.Markdown", "Menu.Compare", "Menu.Settings", "Menu.Help"];

    private static string GroupOf(string id) => id.Split('.')[0] switch
    {
        "file" => "Menu.File",
        "edit" => "Menu.Edit",
        "search" => "Menu.Search",
        "view" => "Menu.View",
        "md" => "Menu.Markdown",
        "diff" or "git" => "Menu.Compare",
        "settings" => "Menu.Settings",
        _ => "Menu.Help",
    };

    /// <summary>Readable key names: "Ctrl+OemPlus" → "Ctrl++", "Ctrl+D1" → "Ctrl+1".</summary>
    internal static string Pretty(string gesture)
    {
        var parts = gesture.Split(',', StringSplitOptions.TrimEntries);
        return string.Join(", ", parts.Select(p => string.Join("+", p.Split('+').Select(k => k switch
        {
            "OemPlus" => "+",
            "OemMinus" => "−",
            "Add" => "Num +",
            "Subtract" => "Num −",
            "OemTilde" => "`",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "Oem6" => "]",
            "OemQuestion" or "Oem2" => "/",
            "OemPeriod" => ".",
            "OemComma" => ",",
            "OemSemicolon" or "Oem1" => ";",
            "OemQuotes" or "Oem7" => "'",
            "OemPipe" or "OemBackslash" or "Oem5" => "\\",
            "Up" => "↑",
            "Down" => "↓",
            "Left" => "←",
            "Right" => "→",
            "Escape" => "Esc",
            "Return" or "Enter" => "Enter",
            ['D', var d] when char.IsDigit(d) => d.ToString(),
            ['N', 'u', 'm', 'P', 'a', 'd', var d] => "Num " + d,
            _ => k,
        }))));
    }

    private void Fill()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        string f = _filter.Text?.Trim() ?? "";
        int row = 0;
        foreach (var group in _rows.Where(r => f.Length == 0 || r.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase) || r.Keys.Contains(f, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(r => r.Group))
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var head = new TextBlock { Text = Loc.T(group.Key), FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 4) }
                .Res(TextBlock.ForegroundProperty, "AccentTextBrush");
            Grid.SetRow(head, row++);
            Grid.SetColumnSpan(head, 2);
            _grid.Children.Add(head);
            foreach (var r in group)
            {
                _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = r.Label, FontSize = 12, Margin = new Thickness(8, 2, 16, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                var keys = new TextBlock { Text = r.Keys, FontSize = 12, Margin = new Thickness(0, 2), FontFamily = new FontFamily("Consolas, Menlo, monospace") }
                    .Res(TextBlock.ForegroundProperty, "DimTextBrush");
                Grid.SetRow(label, row);
                Grid.SetRow(keys, row);
                Grid.SetColumn(keys, 1);
                _grid.Children.Add(label);
                _grid.Children.Add(keys);
                row++;
            }
        }
    }
}
