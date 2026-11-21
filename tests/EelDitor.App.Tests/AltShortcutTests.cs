using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

public class AltShortcutTests
{
    [AvaloniaFact]
    public void ReleasingAltAfterAShortcut_DoesNotOpenTheMenu()
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.md");
        File.WriteAllText(path, "abc def ghi\n");
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [path]);
        w.Show();
        for (int i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs();
        var menu = w.FindControl<Menu>("MainMenu")!;
        w.FindControl<AvaloniaEdit.TextEditor>("Editor")!.TextArea.Focus();

        w.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        w.KeyPress(Key.Q, RawInputModifiers.Alt, PhysicalKey.Q, "q");
        w.KeyRelease(Key.Q, RawInputModifiers.Alt, PhysicalKey.Q, "q");
        w.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);

        // Alt alone still opens it.
        w.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        w.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);
        w.Close();
    }
}
