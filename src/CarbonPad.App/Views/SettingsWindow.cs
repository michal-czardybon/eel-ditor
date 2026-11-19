using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using CarbonPad.App.Services;

namespace CarbonPad.App.Views;

/// <summary>An installed font for the font picker.</summary>
public sealed record FontChoice(string Name, bool IsMonospace, bool HasBold)
{
    public override string ToString() => Name;
}

/// <summary>Settings dialog for options that are not simple toggles in the menus.</summary>
public sealed class SettingsWindow : Window
{
    private readonly ComboBox _font = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360 };
    private readonly CheckBox _allFonts = new() { Content = Loc.T("Settings.AllFonts") };
    private readonly NumericUpDown _fontSize;
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _fontNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private List<FontChoice> _installed = [];

    public SettingsWindow(AppSettings s)
    {
        Title = Loc.T("Menu.Settings.Open");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var theme = new ComboBox { ItemsSource = new[] { Loc.T("Menu.View.ThemeDark"), Loc.T("Menu.View.ThemeSystem"), Loc.T("Menu.View.ThemeLight") }, SelectedIndex = (int)s.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
        var language = new ComboBox { ItemsSource = Loc.Languages.Select(l => l.Name).ToList(), SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == s.Language)), HorizontalAlignment = HorizontalAlignment.Stretch };
        _fontSize = new NumericUpDown { Value = (decimal)s.FontSize, Minimum = 6, Maximum = 48, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Left, Width = 110 };
        var tabSize = new NumericUpDown { Value = s.TabSize, Minimum = 1, Maximum = 16, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Left, Width = 110 };
        var spaces = new CheckBox { Content = Loc.T("Indent.UseSpaces"), IsChecked = s.UseSpaces };
        var autoReload = new CheckBox { Content = Loc.T("Menu.Settings.AutoReload"), IsChecked = s.AutoReloadUnmodified };
        var threshold = new NumericUpDown { Value = s.LargeFileThresholdMb, Minimum = 1, Maximum = 2048, Increment = 5, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Left, Width = 110 };

        // Font: installed fonts, monospaced only unless asked; each shown in its own face, with a live preview.
        _font.ItemTemplate = new FuncDataTemplate<FontChoice?>((f, _) => f is null ? new TextBlock() : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = f.Name, FontFamily = new FontFamily(f.Name), FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = f.HasBold ? "" : Loc.T("Settings.NoBold"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                    .Res(TextBlock.ForegroundProperty, "DimTextBrush"),
            },
        });
        _font.SelectionChanged += (_, _) => UpdatePreview();
        _fontSize.ValueChanged += (_, _) => UpdatePreview();
        _allFonts.IsCheckedChanged += (_, _) => FillFonts(Selected?.Name ?? s.FontFamily);
        var previewBox = new Border { Child = _preview, Padding = new Thickness(8, 6), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0) }
            .Res(Border.BorderBrushProperty, "BorderSubtleBrush").Res(Border.BackgroundProperty, "EditorBrush");
        _fontNote.Res(TextBlock.ForegroundProperty, "DimTextBrush");
        var fontPanel = new StackPanel { Children = { _font, _allFonts, previewBox, _fontNote } };

        var largeNote = new TextBlock { Text = Loc.T("Settings.LargeFileHelp"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }
            .Res(TextBlock.ForegroundProperty, "DimTextBrush");
        var largePanel = new StackPanel { Children = { threshold, largeNote } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(20) };
        int row = 0;
        void Row(string label, Control c)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 16, 6) };
            Grid.SetRow(t, row);
            grid.Children.Add(t);
            Grid.SetRow(c, row);
            Grid.SetColumn(c, 1);
            c.Margin = new Thickness(0, 4);
            grid.Children.Add(c);
            row++;
        }
        Row(Loc.T("Settings.Theme"), theme);
        Row(Loc.T("Settings.Language"), language);
        Row(Loc.T("Settings.Font"), fontPanel);
        Row(Loc.T("Settings.FontSize"), _fontSize);
        Row(Loc.T("Settings.TabSize"), tabSize);
        Row("", spaces);
        Row(Loc.T("Settings.LargeFile"), largePanel);
        Row("", autoReload);

        var ok = new Button { Content = Loc.T("Button.Ok"), MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = Loc.T("Button.Cancel"), MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            s.Theme = (AppTheme)Math.Max(0, theme.SelectedIndex);
            string newLang = Loc.Languages[Math.Max(0, language.SelectedIndex)].Code;
            LanguageChanged = newLang != s.Language;
            s.Language = newLang;
            if (Selected is { } f) s.FontFamily = f.Name;
            s.FontSize = (double)(_fontSize.Value ?? 13);
            s.TabSize = (int)(tabSize.Value ?? 4);
            s.UseSpaces = spaces.IsChecked == true;
            s.LargeFileThresholdMb = (int)(threshold.Value ?? 10);
            s.AutoReloadUnmodified = autoReload.IsChecked == true;
            Saved = true;
            Close();
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Children =
            {
                grid,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20), Children = { ok, cancel } },
            },
        };

        _installed = InstalledFonts();
        // A saved font that is not monospaced (or a fallback list) still shows up selected.
        string current = FirstInstalled(s.FontFamily) ?? s.FontFamily;
        if (_installed.FirstOrDefault(f => f.Name.Equals(current, StringComparison.OrdinalIgnoreCase)) is { IsMonospace: false }) _allFonts.IsChecked = true;
        FillFonts(current);
    }

    public bool Saved { get; private set; }

    public bool LanguageChanged { get; private set; }

    private FontChoice? Selected => _font.SelectedItem as FontChoice;

    /// <summary>Installed font families, with monospace and bold detection (a variable font often has no usable bold face).</summary>
    internal static List<FontChoice> InstalledFonts()
    {
        var list = new List<FontChoice>();
        var boldFaces = WindowsBoldFaces();
        foreach (var family in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var regular)) continue;
                // Text fonts only: symbol fonts (Wingdings…) and fonts without Polish letters are left out.
                if (regular.GetGlyph('a') == 0 || regular.GetGlyph('ą') == 0 || regular.GetGlyph('Ż') == 0) continue;
                bool mono = regular.Metrics.IsFixedPitch || SameAdvance(regular, 'i', 'W', 'm', '.');
                // Avalonia reports a bold face even when it would only be faked (or, for variable fonts, not drawn at all),
                // so on Windows the installed font files decide.
                bool bold = boldFaces is not null
                    ? boldFaces.Contains(family)
                    : FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, FontWeight.Bold), out var b)
                      && b.Weight >= FontWeight.SemiBold && b.FontSimulations == FontSimulations.None;
                list.Add(new FontChoice(family, mono, bold));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                // Broken font file: skip it.
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>
    /// Windows: families that have a real bold font file, from the font registry ("Consolas Bold (TrueType)").
    /// Null on other systems.
    /// </summary>
    private static HashSet<string>? WindowsBoldFaces()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var fonts = hive.OpenSubKey(key);
            if (fonts is null) continue;
            foreach (string name in fonts.GetValueNames())
            {
                // "Consolas Bold (TrueType)", "Consolas Bold Italic (TrueType)", "Source Code Pro Bold (TrueType)"
                string face = name.Split('(')[0].Trim();
                foreach (string part in face.Split('&', StringSplitOptions.TrimEntries))
                {
                    int i = part.IndexOf(" Bold", StringComparison.OrdinalIgnoreCase);
                    if (i > 0) result.Add(part[..i].Trim());
                }
            }
        }
        return result;
    }

    private static bool SameAdvance(IGlyphTypeface face, params char[] chars)
    {
        int? width = null;
        foreach (char c in chars)
        {
            ushort glyph = face.GetGlyph(c);
            if (glyph == 0) return false;
            int w = face.GetGlyphAdvance(glyph);
            if (width is null) width = w;
            else if (width != w) return false;
        }
        return true;
    }

    /// <summary>The first family of a fallback list ("Consolas, Menlo, monospace") that is installed.</summary>
    private string? FirstInstalled(string families) =>
        families.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(n => _installed.Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));

    private void FillFonts(string select)
    {
        var items = _installed.Where(f => f.IsMonospace || _allFonts.IsChecked == true).ToList();
        _font.ItemsSource = items;
        _font.SelectedItem = items.FirstOrDefault(f => f.Name.Equals(select, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(f => f.Name == "Consolas") ?? items.FirstOrDefault();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (Selected is not { } f) return;
        var family = new FontFamily(f.Name);
        double size = (double)(_fontSize.Value ?? 13);
        _preview.Inlines!.Clear();
        _preview.FontFamily = family;
        _preview.FontSize = size;
        _preview.Inlines.Add(new Avalonia.Controls.Documents.Run("AaĄąĆćŻż 0O 1lI| {}[]() => != == ; "));
        _preview.Inlines.Add(new Avalonia.Controls.Documents.Run(Loc.T("Settings.PreviewBold")) { FontWeight = FontWeight.Bold });
        _fontNote.Text = !f.HasBold ? Loc.T("Settings.NoBoldHelp") : !f.IsMonospace ? Loc.T("Settings.NotMonospace") : "";
        _fontNote.IsVisible = _fontNote.Text.Length > 0;
    }
}
