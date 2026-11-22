using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EelDitor.App.Services;
using EelDitor.Core.Search;

namespace EelDitor.App.Views;

public enum FilesScope { CurrentDocument, OpenDocuments, Folder }

public enum FindAction { FindAll, ReplaceAll, Cancel }

/// <summary>
/// "Find and replace in files" (Visual Studio style): one form, a "Look in" choice (current document, all open
/// documents or a folder), results in the bottom panel. Quick find in the current document is the <see cref="FindBar"/>.
/// </summary>
public sealed class FindWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AutoCompleteBox _find = new() { FilterMode = AutoCompleteFilterMode.StartsWith, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoCompleteBox _replace = new() { FilterMode = AutoCompleteFilterMode.StartsWith, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _scope = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _directory = new();
    private readonly Button _browse = new() { Content = Loc.T("Find.Browse"), Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _include = new();
    private readonly TextBox _exclude = new();
    private readonly ComboBox _mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _matchCase = new() { Content = Loc.T("Find.MatchCase") };
    private readonly CheckBox _wholeWord = new() { Content = Loc.T("Find.WholeWord") };
    private readonly CheckBox _recursive = new() { Content = Loc.T("Find.Subfolders"), IsChecked = true };
    private readonly CheckBox _hidden = new() { Content = Loc.T("Find.Hidden") };
    private readonly TextBlock _message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Control> _folderOnly = [];

    public FindWindow(AppSettings settings)
    {
        _settings = settings;
        Title = Loc.T("Find.InFilesTitle");
        this.Res(BackgroundProperty, "ChromeBrush"); // the window grey, not the theme's near-black
        MinWidth = 560;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _find.ItemsSource = settings.FindHistory;
        _replace.ItemsSource = settings.ReplaceHistory;
        _directory.Text = settings.FindInFilesDirectory;
        _include.Text = settings.FindInFilesFilter;
        _exclude.Text = settings.FindInFilesExclude;
        _scope.ItemsSource = new[] { Loc.T("Find.Scope.Document"), Loc.T("Find.Scope.Open"), Loc.T("Find.Scope.Folder") };
        _scope.SelectedIndex = 2;
        _scope.SelectionChanged += (_, _) => UpdateFolderControls();
        _mode.ItemsSource = new[] { Loc.T("Find.ModeNormal"), Loc.T("Find.ModeExtended"), Loc.T("Find.ModeRegex") };
        _mode.SelectedIndex = 0;
        _browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p) _directory.Text = p;
        };

        // Labels in one column, fields stretch; nothing has a fixed width that could clip translated text.
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        int row = 0;
        void Field(string label, Control control, bool folderOnly = false)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetRow(l, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 3);
            form.Children.Add(l);
            form.Children.Add(control);
            if (folderOnly) { _folderOnly.Add(l); _folderOnly.Add(control); }
            row++;
        }
        Field(Loc.T("Find.FindWhat"), _find);
        Field(Loc.T("Find.ReplaceWith"), _replace);
        Field(Loc.T("Find.LookIn"), _scope);
        var dirRow = new DockPanel();
        DockPanel.SetDock(_browse, Avalonia.Controls.Dock.Right);
        dirRow.Children.Add(_browse);
        dirRow.Children.Add(_directory);
        Field(Loc.T("Find.Directory"), dirRow, folderOnly: true);
        Field(Loc.T("Find.Filters"), _include, folderOnly: true);
        Field(Loc.T("Find.Exclude"), _exclude, folderOnly: true);
        Field(Loc.T("Find.Mode"), _mode);
        var checks = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new[] { _matchCase, _wholeWord, _recursive, _hidden })
        {
            c.Margin = new Thickness(0, 0, 16, 0);
            checks.Children.Add(c);
        }
        Field("", checks);
        _folderOnly.Add(_recursive);
        _folderOnly.Add(_hidden);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        void B(string key, FindAction action, bool accent = false)
        {
            var b = new Button { Content = Loc.T(key), Padding = new Thickness(12, 4) };
            if (accent) b.Classes.Add("accent");
            b.Click += (_, _) => Raise(action);
            buttons.Children.Add(b);
        }
        B("Find.FindAll", FindAction.FindAll, accent: true);
        B("Find.ReplaceAll", FindAction.ReplaceAll);
        B("Find.Cancel", FindAction.Cancel);
        var close = new Button { Content = Loc.T("Button.Close"), Padding = new Thickness(12, 4) };
        close.Click += (_, _) => Hide();
        buttons.Children.Add(close);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_message);

        Content = new StackPanel { Margin = new Thickness(14), Children = { form, bottom } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Raise(FindAction.FindAll); e.Handled = true; }
        };
        // The window's own ✕ only hides it (keeps the fields); closing the main window really closes it.
        Closing += (_, e) =>
        {
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            Hide();
        };
        UpdateFolderControls();
    }

    public event Action<FindAction>? ActionRequested;

    public string FindText { get => _find.Text ?? ""; set => _find.Text = value; }

    public string ReplaceText => _replace.Text ?? "";

    public FilesScope Scope
    {
        get => (FilesScope)Math.Max(0, _scope.SelectedIndex);
        set => _scope.SelectedIndex = (int)value;
    }

    public string Directory { get => _directory.Text ?? ""; set => _directory.Text = value; }

    public SearchOptions Options => new(
        FindText,
        _matchCase.IsChecked == true,
        _wholeWord.IsChecked == true,
        _mode.SelectedIndex switch { 1 => SearchMode.Extended, 2 => SearchMode.Regex, _ => SearchMode.Normal });

    public FileSearchOptions FileOptions => new(Directory, _include.Text ?? "*.*", _exclude.Text ?? "", _recursive.IsChecked == true, _hidden.IsChecked == true);

    public void ShowMessage(string text) => _message.Text = text;

    public void FocusFind()
    {
        _find.Focus();
        if (_find.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } tb) tb.SelectAll();
    }

    /// <summary>Remembers the current patterns in the history lists and settings.</summary>
    public void Remember()
    {
        Push(_settings.FindHistory, FindText);
        Push(_settings.ReplaceHistory, ReplaceText);
        _settings.FindInFilesDirectory = Directory;
        _settings.FindInFilesFilter = _include.Text ?? "*.*";
        _settings.FindInFilesExclude = _exclude.Text ?? "";
        _find.ItemsSource = null;
        _find.ItemsSource = _settings.FindHistory;
        _replace.ItemsSource = null;
        _replace.ItemsSource = _settings.ReplaceHistory;
    }

    internal static void Push(List<string> list, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        list.Remove(value);
        list.Insert(0, value);
        if (list.Count > 30) list.RemoveRange(30, list.Count - 30);
    }

    private void UpdateFolderControls()
    {
        bool folder = Scope == FilesScope.Folder;
        foreach (var c in _folderOnly) c.IsEnabled = folder;
    }

    private void Raise(FindAction action)
    {
        if (action is not FindAction.Cancel && string.IsNullOrEmpty(FindText)) { FocusFind(); return; }
        Remember();
        ActionRequested?.Invoke(action);
    }
}
