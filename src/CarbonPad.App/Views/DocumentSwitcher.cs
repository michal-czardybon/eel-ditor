using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CarbonPad.App.Documents;
using CarbonPad.App.Services;

namespace CarbonPad.App.Views;

/// <summary>
/// Popup for switching documents: Ctrl+Tab cycles in most-recently-used order (commit on Ctrl release),
/// Ctrl+P filters all open documents by name or path.
/// </summary>
public sealed class DocumentSwitcher
{
    private readonly Popup _popup;
    private readonly ListBox _list = new() { MaxHeight = 420, Background = Brushes.Transparent };
    private readonly TextBox _filter = new() { Margin = new Thickness(6), FontSize = 13 };
    private readonly Action<EditorDocument> _activate;
    private IReadOnlyList<EditorDocument> _all = [];
    private bool _quickOpen;

    public DocumentSwitcher(Control placementTarget, Action<EditorDocument> activate)
    {
        _activate = activate;
        // The template can be built for a null item while the list is being refilled.
        _list.ItemTemplate = new FuncDataTemplate<EditorDocument?>((d, _) => d is null ? new StackPanel() : new StackPanel
        {
            Margin = new Thickness(2, 1),
            Children =
            {
                new TextBlock { Text = d.Title, FontSize = 13 },
                new TextBlock { Text = d.DirectoryPath, FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });
        _list.DoubleTapped += (_, _) => Commit();
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter: Commit(); e.Handled = true; break;
                case Key.Escape: Close(); e.Handled = true; break;
            }
        };

        var panel = new DockPanel { Width = 520 };
        DockPanel.SetDock(_filter, Dock.Top);
        panel.Children.Add(_filter);
        panel.Children.Add(_list);
        _popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = 0,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Child = panel,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 4 16 0 #60000000"),
            }.Res(Border.BorderBrushProperty, "BorderSubtleBrush").Res(Border.BackgroundProperty, "PanelBrush"),
        };
        _popup.Placement = PlacementMode.Center;

    }

    public Popup Popup => _popup;

    public bool IsOpen => _popup.IsOpen;

    public void ShowMru(IReadOnlyList<EditorDocument> mru, bool backwards)
    {
        if (!_popup.IsOpen || _quickOpen)
        {
            _quickOpen = false;
            _filter.IsVisible = false;
            _all = mru;
            _list.ItemsSource = mru;
            _list.SelectedIndex = Math.Min(mru.Count - 1, backwards ? mru.Count - 1 : 1);
            _popup.IsOpen = true;
        }
        else Move(backwards ? -1 : 1);
    }

    public void ShowQuickOpen(IReadOnlyList<EditorDocument> docs)
    {
        _quickOpen = true;
        _all = docs;
        _filter.IsVisible = true;
        _filter.Watermark = Loc.T("Switcher.Filter");
        _filter.Text = "";
        ApplyFilter();
        _popup.IsOpen = true;
        _filter.Focus();
    }

    /// <summary>Handles keys while the MRU switcher is open. Returns true if handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_quickOpen) return false;
        switch (e.Key)
        {
            case Key.Down: Move(1); return true;
            case Key.Up: Move(-1); return true;
            case Key.Enter: Commit(); return true;
            case Key.Escape: Close(); return true;
        }
        return false;
    }

    public void Commit()
    {
        var doc = _list.SelectedItem as EditorDocument;
        Close();
        if (doc is not null) _activate(doc);
    }

    public void Close() => _popup.IsOpen = false;

    private void Move(int delta)
    {
        int n = _list.ItemCount;
        if (n == 0) return;
        _list.SelectedIndex = ((_list.SelectedIndex + delta) % n + n) % n;
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    private void ApplyFilter()
    {
        string f = _filter.Text ?? "";
        var items = _all.Where(d => OpenFilesModel.FuzzyMatch(f, d))
            .OrderBy(d => d.FileName.StartsWith(f, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(d => d.LastActivated)
            .ToList();
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
    }
}
