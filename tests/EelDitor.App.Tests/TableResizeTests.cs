using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Tables drawn as grids follow the window width at once, and every row the same way.</summary>
public class TableResizeTests
{
    private static string MediumTable(int rows = 60, int columns = 6)
    {
        var rnd = new Random(7);
        string[] words = ["alfa", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "jota", "kappa", "lambda", "mi"];
        string Cell() => string.Join(" ", Enumerable.Range(0, rnd.Next(1, 25)).Select(_ => words[rnd.Next(words.Length)]));
        var sb = new System.Text.StringBuilder("# Tabela\n\n");
        sb.Append("| " + string.Join(" | ", Enumerable.Range(1, columns).Select(c => "Kolumna " + c)) + " |\n");
        sb.Append("|" + string.Concat(Enumerable.Repeat("---|", columns)) + "\n");
        for (int r = 0; r < rows; r++) sb.Append("| " + string.Join(" | ", Enumerable.Range(0, columns).Select(_ => Cell())) + " |\n");
        return sb.ToString();
    }

    /// <summary>
    /// Regression: the grid took the view width from TextView.Bounds, which a resized view updates only after it has
    /// built its lines. Every resize showed the columns of the previous width, and rows built later (scrolled into
    /// view, measured in the background) had other widths: a jagged table.
    /// </summary>
    [AvaloniaFact]
    public void AfterAResize_EveryRowHasTheColumnsOfTheNewWidth_AtOnce()
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "t.md");
        File.WriteAllText(path, MediumTable());
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = true, WindowWidth = 1400, WindowHeight = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        var tv = w.Editor.TextArea.TextView;
        // Let the window settle at its first size (as a user's window has before it is resized).
        for (int i = 0; i < 100; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        var seen = new HashSet<int>();

        foreach (double width in new[] { 1200.0, 900, 1300, 1000 }) // narrower and wider
        {
            w.Width = width;
            w.UpdateLayout();
            for (int i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs(); // the layout finishes; Bounds has the new width
            int expected = (int)((tv.Bounds.Width - 12) / tv.WideSpaceWidth);
            Assert.True(seen.Add(expected), "the view did not change width"); // the test must really resize
            var visible = tv.VisualLines.SelectMany(vl => vl.Elements.OfType<TableRowElement>()).ToList();
            Assert.NotEmpty(visible);
            Assert.All(visible, row => Assert.Equal(expected, row.Layout.Available));

            // Rows out of view, built now (as the height primer and scrolling do), get the same columns.
            var doc = w.Editor.Document;
            var all = Enumerable.Range(1, doc.LineCount)
                .Select(n => tv.GetOrConstructVisualLine(doc.GetLineByNumber(n)))
                .SelectMany(vl => vl.Elements.OfType<TableRowElement>())
                .Select(e => string.Join(",", e.Layout.Widths)).Distinct().ToList();
            Assert.Single(all);
        }
    }
}
