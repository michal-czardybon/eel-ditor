using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>AltGr (= Ctrl+Alt on Windows) types letters such as ł ę ś; it must never trigger a Ctrl+Alt shortcut.</summary>
public class AltGrTests
{
    private static MainWindow Open()
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, "x");
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        w.Editor.Focus();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static bool SidebarVisible(MainWindow w) => w.MainGrid.ColumnDefinitions[0].Width.Value > 0;

    [AvaloniaTheory]
    [InlineData(Key.L, PhysicalKey.L, "ł")]
    [InlineData(Key.E, PhysicalKey.E, "ę")]
    [InlineData(Key.S, PhysicalKey.S, "ś")]
    public void AltGrLetters_AreText_NotShortcuts(Key key, PhysicalKey physical, string symbol)
    {
        var w = Open();
        bool sidebar = SidebarVisible(w);
        w.KeyPress(key, RawInputModifiers.Control | RawInputModifiers.Alt, physical, symbol);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(sidebar, SidebarVisible(w));
        Assert.Null(w.OwnedWindows.FirstOrDefault()); // no "Save as" dialog either
    }

    [AvaloniaFact]
    public void CtrlAltShortcuts_StillWork_WhenTheKeyTypesNothing()
    {
        var w = Open();
        bool sidebar = SidebarVisible(w);
        w.KeyPress(Key.L, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.L, "\u000C"); // a layout where it types nothing
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(sidebar, SidebarVisible(w));
        w.KeyPress(Key.B, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.B, null);   // the new shortcut
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(sidebar, SidebarVisible(w));
    }

    [Fact]
    public void IsAltGrText()
    {
        Assert.True(KeyMap.IsAltGrText(new KeyEventArgs { Key = Key.L, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt, KeySymbol = "ł" }));
        Assert.False(KeyMap.IsAltGrText(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt, KeySymbol = null }));
        Assert.False(KeyMap.IsAltGrText(new KeyEventArgs { Key = Key.L, KeyModifiers = KeyModifiers.Control, KeySymbol = "\f" }));
    }
}
