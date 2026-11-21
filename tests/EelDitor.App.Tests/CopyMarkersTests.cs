using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Copying a part of styled text never puts a lone marker into the clipboard.</summary>
public class CopyMarkersTests
{
    private static string? Copy(string text, int start, int length)
    {
        var editor = new TextEditor { Document = new TextDocument(text) };
        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
        editor.Select(start, length);
        string? result = MarkdownCommands.BalancedSelectionText(editor, view);
        view.Dispose();
        return result;
    }

    [AvaloniaTheory]
    // "a **bold** b": ** = 2..4, bold = 4..8, ** = 8..10
    [InlineData(2, 6, "**bold**")]        // from before the opening ** to the end of the word
    [InlineData(4, 4, "**bold**")]        // the word (double click)
    [InlineData(5, 3, "**old**")]         // part of the word: copied bold
    [InlineData(0, 6, "a **bo**")]        // text before and part of the span
    [InlineData(4, 8, "**bold** b")]      // the word, its closing marker and the text after
    public void ClosesTheStyle(int start, int length, string expected) => Assert.Equal(expected, Copy("a **bold** b", start, length));

    [AvaloniaFact]
    public void AWholeSpanWithItsMarkers_IsCopiedAsItIs() => Assert.Null(Copy("a **bold** b", 0, 12));

    [AvaloniaFact]
    public void SeveralLines_KeepTheirLineBreaks() =>
        Assert.Equal("**old**\nx **y**", Copy("a **bold**\nx **y** z", 5, 11));

    [AvaloniaFact]
    public void WindowsClipboardKeys_AreBound()
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, "x");
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [path]);
        var gestures = w.CommandGestures();
        Assert.Contains("Shift+Insert", gestures["edit.paste"]);
        Assert.Contains("Ctrl+Insert", gestures["edit.copy"]);
        Assert.Contains("Shift+Delete", gestures["edit.cut"]);
    }

    // On the UI test thread like the other tests that set the settings directory (a static), never alongside them.
    [AvaloniaFact]
    public void CascadiaIsReplacedOnce_ByAFontWithBold()
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        SettingsStore.UseDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"FontFamily\": \"Cascadia Mono\"}");
        var s = SettingsStore.Load();
        Assert.Equal(AppSettings.DefaultFontFamily, s.FontFamily);
        Assert.True(s.BoldFontChecked);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"FontFamily\": \"Cascadia Mono\", \"BoldFontChecked\": true}");
        Assert.Equal("Cascadia Mono", SettingsStore.Load().FontFamily); // chosen again later: kept
    }
}
