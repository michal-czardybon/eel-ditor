using Avalonia.Input;

namespace EelDitor.App.Services;

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
        if (IsAltGrText(e)) return false;
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

    /// <summary>
    /// AltGr is Ctrl+Alt on Windows: when Ctrl+Alt+key types a character in the current keyboard layout
    /// (Polish ą ć ę ł ń ó ś ź ż, German @ € …), it is text, never a shortcut.
    /// </summary>
    public static bool IsAltGrText(KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        if (e.KeySymbol is { Length: > 0 } symbol) return !char.IsControl(symbol[0]);
        // No symbol reported: ask the keyboard layout directly (letters and digits).
        return OperatingSystem.IsWindows() && WindowsKeyboard.TypesCharacterWithAltGr(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private static bool Same(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;
}

/// <summary>Asks the current Windows keyboard layout what Ctrl+Alt (AltGr) + a key types.</summary>
internal static unsafe partial class WindowsKeyboard
{
    internal static string? Probe(Key key, bool shift, bool altGr) // diagnostics: what a key types with or without AltGr
    {
        uint vk = (uint)('A' + (key - Key.A));
        var state = new byte[256];
        if (altGr) { state[0x11] = 0x80; state[0x12] = 0x80; }
        if (shift) state[0x10] = 0x80;
        IntPtr layout = GetKeyboardLayout(0);
        uint scan = MapVirtualKeyExW(vk, 0, layout);
        char* buffer = stackalloc char[8];
        int n;
        fixed (byte* keys = state) n = ToUnicodeEx(vk, scan, keys, buffer, 8, 4, layout);
        return n > 0 ? new string(buffer, 0, n) : null;
    }

    public static bool TypesCharacterWithAltGr(Key key, bool shift)
    {
        uint vk = key switch
        {
            >= Key.A and <= Key.Z => (uint)('A' + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
            _ => 0,
        };
        if (vk == 0) return false;
        try
        {
            var state = new byte[256];
            state[0x11] = 0x80; // VK_CONTROL
            state[0x12] = 0x80; // VK_MENU (Alt)
            if (shift) state[0x10] = 0x80;
            IntPtr layout = GetKeyboardLayout(0);
            uint scan = MapVirtualKeyExW(vk, 0, layout);
            char* buffer = stackalloc char[8];
            int n;
            fixed (byte* keys = state)
                n = ToUnicodeEx(vk, scan, keys, buffer, 8, 4, layout); // flag 4: keep the keyboard state (dead keys)
            return n > 0 && !char.IsControl(buffer[0]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr GetKeyboardLayout(uint thread);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, IntPtr layout);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* buffer, int size, uint flags, IntPtr layout);
}
