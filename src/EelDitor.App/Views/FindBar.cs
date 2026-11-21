using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EelDitor.App.Services;
using EelDitor.Core.Search;

namespace EelDitor.App.Views;

public enum SearchScope { Document, Selection, OpenDocuments }

/// <summary>
/// Quick find / replace bar over the top-right corner of the editor (Visual Studio style). Searches as you type,
/// shows "n of m", options are toggles (Alt+C case, Alt+W whole word, Alt+E regex, Alt+X extended escapes).
/// Find all goes to the results panel; searching in folders is the separate "Find and replace in files" window.
/// </summary>
public sealed class FindBar : Border
{
    private readonly TextBox _find = new() { MinWidth = 220, Watermark = Loc.T("Find.Watermark") };
    private readonly TextBox _replace = new() { MinWidth = 220, Watermark = Loc.T("Find.ReplaceWatermark") };
    private readonly ToggleButton _matchCase = Toggle("Aa", "Find.MatchCase", "Alt+C");
    private readonly ToggleButton _wholeWord = Toggle("ab", "Find.WholeWord", "Alt+W", underline: true);
    private readonly ToggleButton _regex = Toggle(".*", "Find.ModeRegex", "Alt+E");
    private readonly ToggleButton _extended = Toggle("\\n", "Find.ModeExtended", "Alt+X");
    private readonly ToggleButton _showReplace = new() { Content = "›", Classes = { "findToggle" }, VerticalAlignment = VerticalAlignment.Stretch, Width = 22 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Center, FontSize = 12 };
    private readonly ComboBox _scope = new() { FontSize = 12, MinWidth = 190 };
    private readonly DockPanel _replaceRow = new() { LastChildFill = true };

    public FindBar()
    {
        Classes.Add("findBar");
        Padding = new Thickness(4);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(0, 0, 4, 4);
        this.Res(BackgroundProperty, "PanelBrush").Res(BorderBrushProperty, "BorderSubtleBrush");
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 0, 24, 0);
        BoxShadow = BoxShadows.Parse("0 2 8 0 #40000000");

        ToolTip.SetTip(_showReplace, Loc.T("Find.ToggleReplace") + " (Ctrl+H)");
        _showReplace.IsCheckedChanged += (_, _) =>
        {
            _replaceRow.IsVisible = _showReplace.IsChecked == true;
            _showReplace.Content = _showReplace.IsChecked == true ? "⌄" : "›";
        };

        // Row 1: find text, options, n of m, previous / next, close.
        var row1 = new DockPanel();
        Dock(row1, Button("✕", "Button.Close", "Esc", () => CloseRequested?.Invoke()), Avalonia.Controls.Dock.Right);
        Dock(row1, Button("↓", "Find.FindNext", "Enter, F3", () => NextRequested?.Invoke(true)), Avalonia.Controls.Dock.Right);
        Dock(row1, Button("↑", "Menu.Search.FindPrevious", "Shift+Enter, Shift+F3", () => NextRequested?.Invoke(false)), Avalonia.Controls.Dock.Right);
        Dock(row1, _count, Avalonia.Controls.Dock.Right);
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(2, 0) };
        toggles.Children.AddRange([_matchCase, _wholeWord, _regex, _extended]);
        Dock(row1, toggles, Avalonia.Controls.Dock.Right);
        row1.Children.Add(_find);

        // Row 2: replace text and buttons.
        var replaceOne = new Button { Content = Loc.T("Find.Replace"), FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(replaceOne, Loc.T("Find.Replace") + " (Alt+R)");
        replaceOne.Click += (_, _) => ReplaceRequested?.Invoke();
        var replaceAll = new Button { Content = Loc.T("Find.ReplaceAll"), FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(replaceAll, Loc.T("Find.ReplaceAll") + " (Alt+A)");
        replaceAll.Click += (_, _) => ReplaceAllRequested?.Invoke();
        Dock(_replaceRow, replaceAll, Avalonia.Controls.Dock.Right);
        Dock(_replaceRow, replaceOne, Avalonia.Controls.Dock.Right);
        _replaceRow.Children.Add(_replace);
        _replaceRow.Margin = new Thickness(0, 4, 0, 0);
        _replaceRow.IsVisible = false;

        // Row 3: scope, find all, link to the files window.
        _scope.ItemsSource = new[] { Loc.T("Find.Scope.Document"), Loc.T("Find.Scope.Selection"), Loc.T("Find.Scope.Open") };
        _scope.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => OptionsChanged?.Invoke();
        var findAll = new Button { Content = Loc.T("Find.FindAll"), FontSize = 12, Padding = new Thickness(8, 2) };
        ToolTip.SetTip(findAll, Loc.T("Find.FindAll") + " (Alt+Enter)");
        findAll.Click += (_, _) => FindAllRequested?.Invoke();
        var inFiles = new Button { Content = Loc.T("Menu.Search.FindInFiles"), FontSize = 12, Padding = new Thickness(8, 2), Background = Brushes.Transparent };
        ToolTip.SetTip(inFiles, "Ctrl+Shift+F");
        inFiles.Click += (_, _) => InFilesRequested?.Invoke();
        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        row3.Children.AddRange([
            new TextBlock { Text = Loc.T("Find.LookIn"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
            _scope, findAll, inFiles,
        ]);

        var rows = new StackPanel();
        rows.Children.AddRange([row1, _replaceRow, row3]);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(_showReplace);
        Grid.SetColumn(rows, 1);
        grid.Children.Add(rows);
        Child = grid;

        _find.TextChanged += (_, _) => OptionsChanged?.Invoke();
        foreach (var t in new[] { _matchCase, _wholeWord, _regex, _extended })
            t.IsCheckedChanged += (_, _) => OptionsChanged?.Invoke();
        _regex.IsCheckedChanged += (_, _) => { if (_regex.IsChecked == true) _extended.IsChecked = false; };
        _extended.IsCheckedChanged += (_, _) => { if (_extended.IsChecked == true) _regex.IsChecked = false; };
        AddHandler(KeyDownEvent, (_, e) => { if (HandleKey(e)) e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public event Action? OptionsChanged;
    public event Action<bool>? NextRequested;
    public event Action? ReplaceRequested;
    public event Action? ReplaceAllRequested;
    public event Action? FindAllRequested;
    public event Action? InFilesRequested;
    public event Action? CloseRequested;

    public string FindText { get => _find.Text ?? ""; set => _find.Text = value; }

    public string ReplaceText { get => _replace.Text ?? ""; set => _replace.Text = value; }

    public bool ReplaceVisible
    {
        get => _showReplace.IsChecked == true;
        set => _showReplace.IsChecked = value;
    }

    public SearchScope Scope
    {
        get => (SearchScope)Math.Max(0, _scope.SelectedIndex);
        set => _scope.SelectedIndex = (int)value;
    }

    public SearchOptions Options => new(
        FindText,
        _matchCase.IsChecked == true,
        _wholeWord.IsChecked == true,
        _regex.IsChecked == true ? SearchMode.Regex : _extended.IsChecked == true ? SearchMode.Extended : SearchMode.Normal);

    /// <summary>"3 of 17", "No results", or an error (invalid regex).</summary>
    public void SetStatus(string text, bool error = false)
    {
        _count.Text = text;
        if (error) _count.Foreground = Brushes.IndianRed;
        else _count.ClearValue(TextBlock.ForegroundProperty);
        ToolTip.SetTip(_count, text.Length > 12 ? text : null);
    }

    public void FocusFind(bool replace = false)
    {
        var box = replace && ReplaceVisible ? _replace : _find;
        box.Focus();
        box.SelectAll();
    }

    public bool IsFocusWithin => IsKeyboardFocusWithin;

    /// <summary>Keys that belong to the bar; called before the window's own shortcuts.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            switch (e.Key)
            {
                case Key.C: Flip(_matchCase); return true;
                case Key.W: Flip(_wholeWord); return true;
                case Key.E: Flip(_regex); return true;
                case Key.X: Flip(_extended); return true;
                case Key.R: ReplaceRequested?.Invoke(); return true;
                case Key.A: ReplaceAllRequested?.Invoke(); return true;
                case Key.Enter: FindAllRequested?.Invoke(); return true;
            }
            return false;
        }
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                if (_replace.IsFocused && !shift) ReplaceRequested?.Invoke();
                else NextRequested?.Invoke(!shift);
                return true;
            case Key.Escape:
                CloseRequested?.Invoke();
                return true;
        }
        return false;
    }

    private static void Flip(ToggleButton t) => t.IsChecked = t.IsChecked != true;

    private static ToggleButton Toggle(string text, string tipKey, string gesture, bool underline = false)
    {
        var t = new ToggleButton
        {
            Content = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas, Menlo, monospace"), TextDecorations = underline ? TextDecorations.Underline : null },
            Classes = { "findToggle" },
            Padding = new Thickness(5, 1),
            MinWidth = 26,
            Focusable = false,
        };
        ToolTip.SetTip(t, $"{Loc.T(tipKey)} ({gesture})");
        return t;
    }

    private static Button Button(string text, string tipKey, string gesture, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(7, 1), Background = Brushes.Transparent, Focusable = false };
        ToolTip.SetTip(b, $"{Loc.T(tipKey)} ({gesture})");
        b.Click += (_, _) => action();
        return b;
    }

    private static void Dock(DockPanel panel, Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        panel.Children.Add(c);
    }
}
