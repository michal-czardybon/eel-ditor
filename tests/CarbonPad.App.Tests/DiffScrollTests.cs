using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CarbonPad.App.Editor;
using CarbonPad.App.Views;

namespace CarbonPad.App.Tests;

public class DiffScrollTests
{
    private static string Lines(int n, Func<int, string> f) => string.Join((char)10, Enumerable.Range(1, n).Select(f));

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScrollingOneSide_MovesTheOtherWhenSyncIsOn(bool sync)
    {
        string right = Lines(600, i => "line " + i);
        string left = Lines(600, i => i % 10 == 5 ? "changed " + i : "line " + i);
        var view = new DiffView("l", DiffView.ReadOnlyDocument(left), false, "r", new TextDocument(right), true, EditorPalette.Dark,
            new FontFamily("Consolas"), 13, ".txt", TextMateSharp.Grammars.ThemeName.DarkPlus) { SyncScroll = sync };
        var w = new Window { Content = view, Width = 900, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        view.RightEditor.SetVerticalOffset(3000);
        for (int i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(60); }
        Assert.InRange(view.RightEditor.VerticalOffset, 2900, 3100);
        if (sync) Assert.InRange(view.LeftEditor.VerticalOffset, 2800, 3200);
        else Assert.True(view.LeftEditor.VerticalOffset < 1000);
        w.Close();
    }
}

