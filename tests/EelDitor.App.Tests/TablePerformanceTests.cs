using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;
using Xunit.Abstractions;

namespace EelDitor.App.Tests;

/// <summary>
/// Commands over a whole column of a large table (800 rows, long cells, about 300 KB) must not freeze the editor:
/// the Markdown model is reparsed once per command (and once per undo), not once per changed row.
/// </summary>
public class TablePerformanceTests(ITestOutputHelper output)
{
    private static string BigTable()
    {
        var sb = new System.Text.StringBuilder("# Glossary\n\n| word | lemma | n | pos | isv | candidates | note | context |\n|---|---|---|---|---|---|---|---|\n");
        var random = new Random(7);
        for (int i = 0; i < 800; i++)
        {
            string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(k => "słowo" + random.Next(100)));
            sb.Append($"| W{i} | lemat{i} | {i % 9} | NOUN | izba | izba (5; chamber; pl sala, izba)<br>{Words(8)} | {Words(6)} | {Words(random.Next(10, 60))} |\n");
        }
        return sb.ToString();
    }

    private (MainWindow W, EditorDocument Doc) Open(bool wrap)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "big.md");
        File.WriteAllText(path, BigTable());
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 1200 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private double Time(string what, Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        Dispatcher.UIThread.RunJobs();
        output.WriteLine($"{what}: {sw.ElapsedMilliseconds} ms");
        return sw.Elapsed.TotalSeconds;
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ColumnCommands_OnALargeTable_AreQuick(bool wrap)
    {
        var (w, doc) = Open(wrap);
        var row = doc.Document.GetLineByNumber(10);
        w.Editor.CaretOffset = row.Offset + doc.Document.GetText(row).IndexOf("lemat", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();
        string original = doc.Document.Text;

        double total = 0;
        total += Time("select column + highlight", () => { w.RunCommand("md.table.selCol"); w.RunCommand("md.highlight"); });
        Assert.Contains("| ==lemat5== |", doc.Document.Text);
        total += Time("undo", () => doc.Document.UndoStack.Undo());
        Assert.Equal(original, doc.Document.Text);
        total += Time("move column right", () => w.RunCommand("md.table.colMoveRight"));
        total += Time("undo", () => doc.Document.UndoStack.Undo());
        total += Time("delete column", () => w.RunCommand("md.table.delCol"));
        total += Time("undo", () => doc.Document.UndoStack.Undo());
        Assert.Equal(original, doc.Document.Text);
        // Before the fix each of these took many seconds (15 s for one undo).
        Assert.True(total < 12, $"{total:F1} s in all");
        w.Close();
    }
}
