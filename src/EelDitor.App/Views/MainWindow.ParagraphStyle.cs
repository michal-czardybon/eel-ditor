using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit.Document;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.Core.Text;

namespace EelDitor.App.Views;

/// <summary>
/// The paragraph line-break convention on the toolbar (prose files only): a combo box with the setting for new text
/// and, next to it, the convention the open file actually follows, with a warning sign when the two differ. A click on
/// the file's convention offers to adopt it for new text or to split the file's long lines.
/// </summary>
public partial class MainWindow
{
    private static readonly WrapMode[] ParagraphModes = [WrapMode.OneLine, WrapMode.Width80, WrapMode.Width100, WrapMode.Width120, WrapMode.Sentences];

    private ComboBox? _wrapCombo;
    private Button? _fileConvention;
    private TextBlock? _conventionWarning, _conventionText, _conventionLabel;
    private readonly List<Control> _paragraphTools = [];
    private bool _updatingWrapCombo;
    private CancellationTokenSource? _textSettled;

    /// <summary>Runs <see cref="OnTextSettled"/> 600 ms after the last change (each change restarts the wait).</summary>
    private void ScheduleTextSettled()
    {
        _textSettled?.Cancel();
        var cts = _textSettled = new CancellationTokenSource();
        _ = Task.Delay(600, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) OnTextSettled(); });
        }, TaskScheduler.Default);
    }

    /// <summary>The setting for new text (tests).</summary>
    internal ComboBox? ParagraphCombo => _wrapCombo;

    /// <summary>The file's convention next to it (tests).</summary>
    internal Button? FileConventionButton => _fileConvention;

    /// <summary>The text shown for the file's convention, and whether it is marked as differing (tests).</summary>
    internal (string Text, bool Warning) FileConventionShown => (_conventionText?.Text ?? "", _conventionWarning?.IsVisible == true);

    /// <summary>An option of the setting: "Disabled (one line per paragraph)", "80 columns", "Break after each sentence"…</summary>
    internal static string ParagraphOptionLabel(WrapMode mode) => mode switch
    {
        WrapMode.OneLine => Loc.T("Wrap.OptionOneLine"),
        WrapMode.Sentences => Loc.T("Wrap.OptionSentences"),
        _ => ParagraphModeLabel(mode),
    };

    /// <summary>An option as shown in the toolbar's combo box: "Disabled" (the full text is in the tooltip).</summary>
    internal static string ParagraphComboLabel(WrapMode mode) => mode switch
    {
        WrapMode.OneLine => Loc.T("Wrap.OptionOneLineShort"),
        WrapMode.Sentences => Loc.T("Wrap.OptionSentencesShort"),
        _ => ParagraphOptionLabel(mode),
    };

    /// <summary>A file's convention, short: "One line per paragraph", "80 columns", "Sentence per line"…</summary>
    internal static string ParagraphModeLabel(WrapMode mode) => Loc.T(mode switch
    {
        WrapMode.Width80 => "Wrap.Short80",
        WrapMode.Width100 => "Wrap.Short100",
        WrapMode.Width120 => "Wrap.Short120",
        WrapMode.Sentences => "Wrap.ShortSentences",
        _ => "Wrap.ShortOneLine",
    });

    private void BuildParagraphTools(Action separator)
    {
        int start = Toolbar.Children.Count;
        separator();
        Toolbar.Children.Add(new TextBlock { Text = Loc.T("Wrap.ComboLabel"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
        _wrapCombo = new ComboBox
        {
            ItemsSource = ParagraphModes.Select(ParagraphComboLabel).ToList(),
            FontSize = 12,
            MinWidth = 150,
            Height = 26,
            Padding = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0),
        };
        ToolTip.SetTip(_wrapCombo, Loc.T("Wrap.Tip"));
        _wrapCombo.SelectionChanged += (_, _) =>
        {
            if (_updatingWrapCombo || _wrapCombo.SelectedIndex < 0) return;
            SetParagraphWrap(ParagraphModes[_wrapCombo.SelectedIndex]);
            Editor.Focus();
        };
        Toolbar.Children.Add(_wrapCombo);

        // "Current file:" and a drop-down button with the file's convention: its menu holds the actions.
        _conventionLabel = new TextBlock { Text = Loc.T("Wrap.FileConventionLabel"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
        Toolbar.Children.Add(_conventionLabel);
        _conventionWarning = new TextBlock { Text = "⚠", FontSize = 13, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center }
            .Res(TextBlock.ForegroundProperty, "IconAmberBrush");
        _conventionText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { Text = "▾", FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            .Res(TextBlock.ForegroundProperty, "DimTextBrush");
        _fileConvention = new Button
        {
            FontSize = 12,
            Height = 26,
            MinWidth = 110,
            Padding = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0),
            Content = new DockPanel { Children = { _conventionWarning, chevron, _conventionText } },
        };
        DockPanel.SetDock(_conventionWarning, Dock.Left);
        DockPanel.SetDock(chevron, Dock.Right);
        _fileConvention.Click += (_, _) => ShowConventionMenu();
        Toolbar.Children.Add(_fileConvention);
        _paragraphTools.AddRange(Toolbar.Children.Skip(start).OfType<Control>());
    }

    private void ShowConventionMenu()
    {
        if (_fileConvention is null || _active is not { } d) return;
        var menu = new ContextMenu();
        if (DetectedParagraphMode(d) is { Mode: { } mode } && mode != _settings.ParagraphWrap)
        {
            var adopt = new MenuItem { Header = Loc.T("Wrap.Adopt", ParagraphOptionLabel(mode)) };
            adopt.Click += (_, _) => SetParagraphWrap(mode);
            menu.Items.Add(adopt);
            menu.Items.Add(new Separator());
        }
        // The actions say what they do: "Convert to 80 columns"… (not to the convention the file already has).
        var current = DetectedParagraphMode(d)?.Mode;
        foreach (var (id, target) in new[] { ("wrap.joinLines", WrapMode.OneLine), ("wrap.split80", WrapMode.Width80), ("wrap.split100", WrapMode.Width100), ("wrap.split120", WrapMode.Width120), ("wrap.splitSentences", WrapMode.Sentences) })
            if (target != current) menu.Items.Add(Item(id, track: false));
        menu.Open(_fileConvention);
    }

    private (TextDocument Document, ITextSourceVersion Version, (WrapMode? Mode, bool Mixed) Result, int Longest)? _paragraphCache;

    /// <summary>How the open prose file's paragraphs are broken (null: it does not tell), recomputed when the text changes.</summary>
    private (WrapMode? Mode, bool Mixed)? DetectedParagraphMode(EditorDocument d)
    {
        if (d.IsLarge || !IsProseDocument(d)) return null;
        return Analyze(d).Result;
    }

    private ((WrapMode? Mode, bool Mixed) Result, int Longest) Analyze(EditorDocument d)
    {
        var doc = d.Document;
        if (_paragraphCache is { } c && ReferenceEquals(c.Document, doc) && c.Version.BelongsToSameDocumentAs(doc.Version) && c.Version.CompareAge(doc.Version) == 0)
            return (c.Result, c.Longest);
        var lines = doc.Lines.Select(l => doc.GetText(l)).ToList();
        var result = ParagraphWrap.Detect(lines, d.IsMarkdown);
        int longest = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
        _paragraphCache = (doc, doc.Version, result, longest);
        return (result, longest);
    }

    /// <summary>
    /// Notices about a state of the text go when the text changes (also by Ctrl+Z): "Split 8 long lines…" is about
    /// the text right after the split.
    /// </summary>
    private void DropStaleNotices()
    {
        if (_active is not { ReflowNotice: not null } d) return;
        var now = d.Document.Version;
        if (d.ReflowVersion is { } v && v.BelongsToSameDocumentAs(now) && v.CompareAge(now) == 0) return;
        d.ReflowNotice = null;
        d.ReflowVersion = null;
        UpdateInfoBar();
    }

    /// <summary>A moment after typing stops: what is derived from the whole text.</summary>
    private void OnTextSettled()
    {
        RefreshEolState();
        UpdateParagraphTools();
    }

    /// <summary>
    /// The line endings as they are in the text now: after Ctrl+Z of "Convert to LF" the file is mixed again, and the
    /// status bar and the mixed-endings bar must say so.
    /// </summary>
    private void RefreshEolState()
    {
        if (_active is not { } d) return;
        var doc = d.Document;
        int crlf = 0, lf = 0, cr = 0;
        foreach (var line in doc.Lines)
        {
            if (line.DelimiterLength == 2) crlf++;
            else if (line.DelimiterLength == 1) { if (doc.GetCharAt(line.EndOffset) == '\n') lf++; else cr++; }
        }
        if (crlf + lf + cr == 0) return;
        bool mixed = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0) > 1;
        var dominant = crlf >= lf && crlf >= cr ? Core.Text.EolKind.CrLf : lf >= cr ? Core.Text.EolKind.Lf : Core.Text.EolKind.Cr;
        if (mixed == d.EolMixed && (mixed || dominant == d.Eol)) return;
        d.EolMixed = mixed;
        if (!mixed || !d.EolMixedAccepted) d.Eol = dominant;
        UpdateStatus();
        UpdateInfoBar();
    }

    private void UpdateParagraphTools()
    {
        var d = _active;
        bool prose = d is not null && IsProseDocument(d) && !d.IsLarge;
        foreach (var c in _paragraphTools) c.IsVisible = prose;
        if (_wrapCombo is not null)
        {
            _updatingWrapCombo = true;
            _wrapCombo.SelectedIndex = Array.IndexOf(ParagraphModes, _settings.ParagraphWrap);
            ToolTip.SetTip(_wrapCombo, ParagraphOptionLabel(_settings.ParagraphWrap) + Environment.NewLine + Loc.T("Wrap.Tip"));
            _updatingWrapCombo = false;
        }

        StatusLongest.IsVisible = StatusLongestSep.IsVisible = d is { IsMarkdown: true, IsLarge: false };
        if (!prose || d is null || _fileConvention is null || _conventionText is null || _conventionWarning is null) return;
        var ((mode, mixed), longest) = Analyze(d);
        StatusLongest.Text = Loc.T("Status.Longest", longest.ToString("N0"));
        _fileConvention.IsVisible = mode is not null || mixed;
        if (_conventionLabel is not null) _conventionLabel.IsVisible = _fileConvention.IsVisible;
        _conventionText.Text = mixed ? Loc.T("Wrap.Mixed") : mode is { } m ? ParagraphModeLabel(m) : "";
        bool differs = mixed || mode != _settings.ParagraphWrap;
        _conventionWarning.IsVisible = differs;
        ToolTip.SetTip(_fileConvention, Loc.T(differs ? "Wrap.ConventionDiffers" : "Wrap.ConventionTip"));
    }
}
