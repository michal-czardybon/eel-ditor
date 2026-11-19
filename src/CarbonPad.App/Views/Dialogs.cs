using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CarbonPad.App.Services;

namespace CarbonPad.App.Views;

/// <summary>Small modal dialogs built in code (message box, text input).</summary>
public static class Dialogs
{
    /// <summary>Shows a message with buttons; returns the index of the clicked button, or -1 when closed.</summary>
    public static async Task<int> Ask(Window owner, string title, string message, params string[] buttons)
    {
        var dialog = CreateWindow(title);
        int result = -1;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => { result = index; dialog.Close(); };
            panel.Children.Add(b);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                panel,
            },
        };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        await dialog.ShowDialog(owner);
        return result;
    }

    public static Task Info(Window owner, string title, string message) => Ask(owner, title, message, Loc.T("Button.Ok"));

    /// <summary>Asks for a line of text; returns null when cancelled.</summary>
    public static async Task<string?> Input(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = CreateWindow(title);
        string? result = null;
        var box = new TextBox { Text = initial, MinWidth = 300 };
        var ok = new Button { Content = Loc.T("Button.Ok"), MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = Loc.T("Button.Cancel"), MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } },
            },
        };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return result;
    }

    private static Window CreateWindow(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        MinWidth = 320,
    };
}
