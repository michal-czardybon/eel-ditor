using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>The view never stays scrolled past the last line (it showed a whole empty screen after the text).</summary>
public class ScrollPastEndTests
{
    private static MainWindow Open(string text, double width)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "big.md");
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = true, WindowWidth = width, WindowHeight = 600 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Settle(w);
        return w;
    }

    private static void Settle(MainWindow w)
    {
        for (int i = 0; i < 200; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Thread.Sleep(3); }
    }

    private static string Tables()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 30; i++)
        {
            sb.Append($"## Sekcja {i}\n\nTekst {i}.\n\n");
            sb.Append("| Column 1 | Column 2 |\n|---|---|\n| To jest pierwszy napis | Litwo, ojczyzno moja, ty jesteś jak zdrowie, ile cię trzeba cenić, ten tylko się dowie, kto cię stracił |\n\n");
        }
        return sb.ToString();
    }

    [AvaloniaFact]
    public void WhenTheTextGetsShorter_AViewAtTheEndStaysOnTheText()
    {
        var w = Open(Tables(), 700);
        var tv = w.Editor.TextArea.TextView;
        w.Editor.SetVerticalOffset(tv.DocumentHeight - tv.Bounds.Height);
        Settle(w);
        double before = tv.DocumentHeight;

        // A wider window: the tables' rows wrap less, so the document gets shorter.
        w.Width = 1600;
        Settle(w);
        Assert.True(tv.DocumentHeight < before - 10, $"{tv.DocumentHeight} vs {before}");
        Assert.True(tv.VerticalOffset <= tv.DocumentHeight - tv.Bounds.Height + 1,
            $"offset {tv.VerticalOffset}, height {tv.DocumentHeight}, view {tv.Bounds.Height}");
        w.Close();
    }
}
