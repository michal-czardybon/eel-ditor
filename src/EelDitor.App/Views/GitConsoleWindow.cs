using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EelDitor.App.Services;
using EelDitor.Core.Git;

namespace EelDitor.App.Views;

/// <summary>
/// The git commands started from the editor (stage, unstage, commit, push, pull…) and what git answered, like Git GUI's
/// output window: each command as typed in a shell, its output, and whether it succeeded. It opens by itself for push
/// and pull and when a command fails; the Git tab's "Console" button opens it any time. The text can be selected and
/// copied.
/// </summary>
public sealed class GitConsoleWindow : Window
{
    private readonly IReadOnlyList<GitService.Command> _commands;
    private readonly Func<GitService.Command, string?> _note;
    private readonly SelectableTextBlock _text = new()
    {
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(10, 8),
    };
    private readonly ScrollViewer _scroll;

    /// <param name="commands">The commands of this session, oldest first (the list grows; call <see cref="Refresh"/>).</param>
    /// <param name="note">An explanation to add under a command's output (e.g. why a pull did nothing), or null.</param>
    /// <param name="clear">Empties the list.</param>
    public GitConsoleWindow(IReadOnlyList<GitService.Command> commands, Func<GitService.Command, string?> note, Action clear)
    {
        _commands = commands;
        _note = note;
        Title = Loc.T("Git.ConsoleTitle");
        this.Res(BackgroundProperty, "ChromeBrush");
        Width = 760;
        Height = 420;
        MinWidth = 360;
        MinHeight = 200;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _scroll = new ScrollViewer { Content = _text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var textBorder = new Border { Child = _scroll, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(10, 10, 10, 6) }
            .Res(Border.BorderBrushProperty, "BorderSubtleBrush").Res(Border.BackgroundProperty, "EditorBrush");
        var clearButton = new Button { Content = Loc.T("Git.ConsoleClear"), FontSize = 12, Padding = new Thickness(14, 3) };
        clearButton.Click += (_, _) => { clear(); Refresh(); };
        var close = new Button { Content = Loc.T("Button.Close"), FontSize = 12, Padding = new Thickness(14, 3) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 10, 10), Children = { clearButton, close } };
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(textBorder);
        Content = dock;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Refresh();
    }

    /// <summary>The text shown (tests).</summary>
    internal string Text => _text.Inlines?.Text ?? "";

    /// <summary>Shows the commands again (one was added or has finished) and scrolls to the end.</summary>
    public void Refresh()
    {
        var inlines = new InlineCollection();
        if (_commands.Count == 0)
            inlines.Add(new Run(Loc.T("Git.ConsoleEmpty")).Res(TextElement.ForegroundProperty, "DimTextBrush"));
        foreach (var c in _commands)
        {
            if (inlines.Count > 0) inlines.Add(new LineBreak());
            inlines.Add(new Run($"{c.Started:HH:mm:ss}  ").Res(TextElement.ForegroundProperty, "DimTextBrush"));
            inlines.Add(new Run("$ " + c.CommandLine) { FontWeight = FontWeight.SemiBold }.Res(TextElement.ForegroundProperty, "AccentTextBrush"));
            inlines.Add(new LineBreak());
            if (c.Result is not { } r)
            {
                inlines.Add(new Run(Loc.T("Git.ConsoleRunning")).Res(TextElement.ForegroundProperty, "DimTextBrush"));
                inlines.Add(new LineBreak());
                continue;
            }
            string output = (Encoding.UTF8.GetString(r.Output) + r.Error).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
            if (output.Length > 0)
            {
                inlines.Add(new Run(output));
                inlines.Add(new LineBreak());
            }
            if (r.ExitCode == 0)
                inlines.Add(new Run("✔ " + Loc.T("Git.ConsoleDone")).Res(TextElement.ForegroundProperty, "GitAddedBrush"));
            else
                inlines.Add(new Run("✖ " + Loc.T("Git.ConsoleFailed", r.ExitCode)) { FontWeight = FontWeight.SemiBold }.Res(TextElement.ForegroundProperty, "GitDeletedBrush"));
            inlines.Add(new LineBreak());
            if (_note(c) is { Length: > 0 } note)
            {
                inlines.Add(new Run(note).Res(TextElement.ForegroundProperty, "WarningTextBrush"));
                inlines.Add(new LineBreak());
            }
        }
        _text.Inlines = inlines;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }
}
