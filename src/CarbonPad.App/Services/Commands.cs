using Avalonia.Input;

namespace CarbonPad.App.Services;

/// <summary>A user command reachable from menus, the toolbar and keyboard shortcuts.</summary>
public sealed class AppCommand
{
    public required string Id { get; init; }

    /// <summary>Localization key of the menu label.</summary>
    public required string LabelKey { get; init; }

    public required Action Execute { get; init; }

    /// <summary>
    /// When false the shortcut falls through to the next command bound to the same keys
    /// (e.g. Ctrl+B = bold in Markdown, matching brace elsewhere).
    /// </summary>
    public Func<bool>? CanExecute { get; init; }

    public Func<bool>? IsChecked { get; init; }

    /// <summary>Optional explanation shown as a tooltip in menus and on the toolbar.</summary>
    public string? TooltipKey { get; init; }

    /// <summary>Shortcuts; a chord is written "Ctrl+K, Ctrl+C". The first one is shown in menus.</summary>
    public string[] Gestures { get; init; } = [];

    public string Label => Loc.T(LabelKey);

    public string? GestureText => Gestures.Length > 0 ? Gestures[0] : null;
}

/// <summary>Dispatches key presses to commands, including two-step chords (Visual Studio style).</summary>
public sealed class KeyMap
{
    private readonly List<(KeyGesture[] Steps, AppCommand Command)> _bindings = [];
    private KeyGesture? _pendingChord;

    public event Action<string?>? ChordStateChanged;

    public void Register(AppCommand command)
    {
        foreach (var g in command.Gestures)
        {
            var steps = g.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(KeyGesture.Parse).ToArray();
            _bindings.Add((steps, command));
        }
    }

    /// <summary>
    /// Returns true if the key was handled (executed or started a chord).
    /// <paramref name="allow"/> limits which commands may run (e.g. only file and diff commands inside the comparison view).
    /// </summary>
    public bool Handle(KeyEventArgs e, Func<AppCommand, bool>? allow = null)
    {
        bool Can(AppCommand c) => (allow?.Invoke(c) ?? true) && (c.CanExecute?.Invoke() ?? true);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin) return false;

        if (_pendingChord is { } first)
        {
            _pendingChord = null;
            ChordStateChanged?.Invoke(null);
            foreach (var (steps, cmd) in _bindings)
            {
                if (steps.Length == 2 && Same(steps[0], first) && steps[1].Matches(e) && Can(cmd))
                {
                    cmd.Execute();
                    return true;
                }
            }
            return true; // swallow unknown chord second key
        }

        foreach (var (steps, cmd) in _bindings)
        {
            if (steps.Length == 1 && steps[0].Matches(e) && Can(cmd))
            {
                cmd.Execute();
                return true;
            }
        }

        foreach (var (steps, cmd) in _bindings)
        {
            if (steps.Length == 2 && steps[0].Matches(e) && (allow?.Invoke(cmd) ?? true))
            {
                _pendingChord = steps[0];
                ChordStateChanged?.Invoke(steps[0].ToString());
                return true;
            }
        }
        return false;
    }

    private static bool Same(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;
}
