using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>The document's height is known before scrolling, so the scrollbar thumb follows the mouse.</summary>
public class ScrollHeightTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHeightDoesNotGrowWhileScrolling(bool wrap)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            sb.Append($"## Sekcja {i}\n\nTo jest **pogrubiony** tekst z `kodem` i [linkiem](http://x.pl) numer {i}.\n\n");
            sb.Append("| Column 1 | Column 2 | Column 3 |\n|---|---|---|\n| To jest pierwszy napis | Litwo, ojczyzno moja, ty jesteś jak zdrowie, ile cię trzeba cenić | ten tylko się dowie, kto cię stracił |\n| a | b | c |\n\n");
        }
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "big.md");
        File.WriteAllText(path, sb.ToString());
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 900, WindowHeight = 600 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        // Scrolled to the middle before the background measuring: the text in view stays in place.
        var tv0 = w.Editor.TextArea.TextView;
        w.UpdateLayout();
        tv0.EnsureVisualLines();
        w.Editor.SetVerticalOffset(tv0.GetVisualTopByDocumentLine(400));
        w.UpdateLayout();
        for (int i = 0; i < 300; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Thread.Sleep(5); }

        var tv = w.Editor.TextArea.TextView;
        Assert.Equal(tv.GetVisualTopByDocumentLine(400), tv.VerticalOffset, 2.0); // line 400 is still at the top
        var scroll = (ILogicalScrollable)tv;
        double extent = scroll.Extent.Height;
        Assert.True(extent > w.Editor.Document.LineCount * tv.DefaultLineHeight * (wrap ? 1.3 : 1.0) - 1, $"{extent}");
        for (int k = 0; k <= 40; k++)
        {
            scroll.Offset = new Avalonia.Vector(0, (extent - tv.Bounds.Height) * k / 40.0);
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Assert.Equal(extent, scroll.Extent.Height, 1);
        }
        w.Close();
    }
}
