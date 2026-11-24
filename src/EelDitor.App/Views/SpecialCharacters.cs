using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EelDitor.App.Services;

namespace EelDitor.App.Views;

/// <summary>A group of characters in the special-character window: a name (a Loc key) and Unicode ranges.</summary>
public sealed record CharCategory(string Key, (int From, int To)[] Ranges)
{
    /// <summary>The characters of the category that Unicode assigns (no controls, surrogates or private use).</summary>
    public IEnumerable<string> Characters() =>
        Ranges.SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1))
            .Where(c => Rune.IsValid(c) && SpecialCharacters.IsShown(new Rune(c)))
            .Select(c => char.ConvertFromUtf32(c));
}

/// <summary>
/// Inserting any Unicode character, category by category (arrows, mathematical symbols, Cyrillic, …), never all mixed
/// together: categories on the left, their characters on the right, recently inserted ones first. A click inserts the
/// character at the caret and the window stays open; the code (U+2713) or the character itself can be searched.
/// </summary>
public sealed class SpecialCharacters : Window
{
    /// <summary>The categories, in the order shown (Recent first, filled from the settings).</summary>
    public static readonly CharCategory[] Categories =
    [
        new("CharMap.Punctuation", [(0x00A1, 0x00BF), (0x2010, 0x2027), (0x2030, 0x205E), (0x2E00, 0x2E4F)]),
        new("CharMap.Spaces", [(0x00A0, 0x00A0), (0x00AD, 0x00AD), (0x2000, 0x200F), (0x202F, 0x202F), (0x205F, 0x2060), (0x3000, 0x3000), (0xFEFF, 0xFEFF)]),
        new("CharMap.Arrows", [(0x2190, 0x21FF), (0x27F0, 0x27FF), (0x2900, 0x297F), (0x2B00, 0x2B2F)]),
        new("CharMap.Math", [(0x00B1, 0x00B1), (0x00D7, 0x00D7), (0x00F7, 0x00F7), (0x2200, 0x22FF), (0x27C0, 0x27EF), (0x2980, 0x29FF)]),
        new("CharMap.Scripts", [(0x00B2, 0x00B3), (0x00B9, 0x00B9), (0x2070, 0x209F)]),
        new("CharMap.Numbers", [(0x00BC, 0x00BE), (0x2150, 0x218F), (0x2460, 0x24FF), (0x2776, 0x2793)]),
        new("CharMap.Currency", [(0x0024, 0x0024), (0x00A2, 0x00A5), (0x20A0, 0x20C0)]),
        new("CharMap.Letterlike", [(0x2100, 0x214F)]),
        new("CharMap.Latin", [(0x00C0, 0x00FF), (0x0100, 0x017F), (0x0180, 0x024F), (0x1E00, 0x1EFF)]),
        new("CharMap.Greek", [(0x0370, 0x03FF), (0x1F00, 0x1FFF)]),
        new("CharMap.Cyrillic", [(0x0400, 0x04FF), (0x0500, 0x052F)]),
        new("CharMap.Box", [(0x2500, 0x259F)]),
        new("CharMap.Shapes", [(0x25A0, 0x25FF), (0x2B1B, 0x2B59)]),
        new("CharMap.Symbols", [(0x2600, 0x26FF), (0x2700, 0x27BF)]),
        new("CharMap.Technical", [(0x2300, 0x23FF)]),
        new("CharMap.Emoji", [(0x1F300, 0x1F5FF), (0x1F600, 0x1F64F), (0x1F680, 0x1F6FF), (0x1F900, 0x1F9FF)]),
    ];

    private const string RecentKey = "CharMap.Recent";

    /// <summary>Names of the characters that show nothing, written on their buttons.</summary>
    private static readonly Dictionary<int, string> Invisible = new()
    {
        [0x00A0] = "NBSP", [0x00AD] = "SHY", [0x2000] = "NQSP", [0x2001] = "MQSP", [0x2002] = "ENSP", [0x2003] = "EMSP",
        [0x2004] = "3/MSP", [0x2005] = "4/MSP", [0x2006] = "6/MSP", [0x2007] = "FSP", [0x2008] = "PSP", [0x2009] = "THSP",
        [0x200A] = "HSP", [0x200B] = "ZWSP", [0x200C] = "ZWNJ", [0x200D] = "ZWJ", [0x200E] = "LRM", [0x200F] = "RLM",
        [0x202F] = "NNBSP", [0x205F] = "MMSP", [0x2060] = "WJ", [0x3000] = "IDSP", [0xFEFF] = "BOM",
    };

    /// <summary>A character worth offering: assigned, not a control, surrogate or private-use one.</summary>
    internal static bool IsShown(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.OtherNotAssigned or UnicodeCategory.Control
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse)
        || Invisible.ContainsKey(rune.Value);

    private readonly ListBox _categories = new() { MinWidth = 170 };
    private readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _search = new() { Watermark = Loc.T("CharMap.Search"), FontSize = 12 };
    private readonly TextBlock _preview = new() { FontSize = 30, MinWidth = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _code = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<string> _insert;
    private readonly List<string> _recent;
    private readonly FontFamily _font;

    /// <param name="insert">Puts a character at the caret of the editor.</param>
    /// <param name="recent">Recently inserted characters (kept in the settings), newest first; updated in place.</param>
    public SpecialCharacters(Action<string> insert, List<string> recent, FontFamily font)
    {
        _insert = insert;
        _recent = recent;
        _font = font;
        Title = Loc.T("CharMap.Title");
        this.Res(BackgroundProperty, "ChromeBrush");
        Width = 720;
        Height = 480;
        MinWidth = 420;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _categories.ItemsSource = new[] { RecentKey }.Concat(Categories.Select(c => c.Key)).Select(k => Loc.T(k)).ToList();
        _categories.SelectionChanged += (_, _) => { if (_categories.SelectedIndex >= 0) { _search.Text = ""; ShowCategory(_categories.SelectedIndex); } };
        _search.TextChanged += (_, _) => { if (!string.IsNullOrEmpty(_search.Text)) ShowSearch(_search.Text); };
        _scroll = new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var close = new Button { Content = Loc.T("Button.Close"), FontSize = 12, Padding = new Thickness(14, 3), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var info = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        info.Children.Add(close);
        info.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _preview, _code, new TextBlock { Text = Loc.T("CharMap.Hint"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }.Res(TextBlock.ForegroundProperty, "DimTextBrush") },
        });
        _code.Res(TextBlock.ForegroundProperty, "DimTextBrush");
        _preview.FontFamily = font;

        var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(_search, Dock.Top);
        _search.Margin = new Thickness(0, 0, 0, 6);
        left.Children.Add(_search);
        left.Children.Add(_categories);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(10) };
        body.Children.Add(left);
        var gridBorder = new Border { Child = _scroll, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) }
            .Res(Border.BorderBrushProperty, "BorderSubtleBrush").Res(Border.BackgroundProperty, "EditorBrush");
        Grid.SetColumn(gridBorder, 1);
        body.Children.Add(gridBorder);
        Grid.SetRow(info, 1);
        Grid.SetColumnSpan(info, 2);
        body.Children.Add(info);
        Content = body;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        _categories.SelectedIndex = _recent.Count > 0 ? 0 : 1;
    }

    /// <summary>The characters shown now (tests).</summary>
    internal IReadOnlyList<string> Shown => _grid.Children.OfType<Control>().Select(c => (string)c.Tag!).ToList();

    /// <summary>Shows a category: 0 is Recent, then <see cref="Categories"/>.</summary>
    internal void ShowCategory(int index) =>
        Fill(index == 0 ? _recent : Categories[index - 1].Characters());

    /// <summary>
    /// A search: a code (<c>2713</c>, <c>U+2713</c>) shows that character, typed characters show themselves.
    /// </summary>
    internal void ShowSearch(string query)
    {
        query = query.Trim();
        var found = new List<string>();
        string hex = query.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? query[2..] : query;
        if (hex.Length is >= 2 and <= 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
            && Rune.IsValid(code) && IsShown(new Rune(code)))
            found.Add(char.ConvertFromUtf32(code));
        else foreach (var rune in query.EnumerateRunes())
            if (!char.IsAsciiLetterOrDigit((char)Math.Min(rune.Value, 0x7F)) && rune.Value > ' ' && IsShown(rune) && !found.Contains(rune.ToString()))
                found.Add(rune.ToString());
        _categories.SelectedIndex = -1;
        Fill(found);
    }

    private void Fill(IEnumerable<string> characters)
    {
        _grid.Children.Clear();
        foreach (string c in characters) _grid.Children.Add(Cell(c));
        _scroll.Offset = default;
    }

    private Control Cell(string c)
    {
        int code = char.ConvertToUtf32(c, 0);
        bool invisible = Invisible.TryGetValue(code, out string? name);
        var text = new TextBlock
        {
            Text = invisible ? name : c,
            FontFamily = _font,
            FontSize = invisible ? 9 : 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cell = new Border
        {
            Child = text,
            Width = 40,
            Height = 40,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = c,
        };
        ToolTip.SetTip(cell, CodeText(c));
        cell.PointerEntered += (_, _) => { cell.Res(Border.BackgroundProperty, "ActiveItemBrush"); Describe(c); };
        cell.PointerExited += (_, _) => cell.Background = Brushes.Transparent;
        cell.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
            Insert(c);
            e.Handled = true;
        };
        return cell;
    }

    private static string CodeText(string c) => string.Join(" ", c.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));

    private void Describe(string c)
    {
        _preview.Text = Invisible.ContainsKey(char.ConvertToUtf32(c, 0)) ? "␣" : c;
        _code.Text = CodeText(c);
    }

    /// <summary>Inserts a character and puts it first among the recent ones.</summary>
    internal void Insert(string c)
    {
        _insert(c);
        _recent.Remove(c);
        _recent.Insert(0, c);
        if (_recent.Count > 48) _recent.RemoveRange(48, _recent.Count - 48);
        Describe(c);
    }
}
