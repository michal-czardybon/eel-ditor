using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Word wrap on: tables drawn as grids with wrapping cells, edited in a cell editor.</summary>
public class TableGridTests
{
    private const string Table =
        "Tekst.\n\n| a | b |\n|---|---|\n| krótki | bardzo długi tekst komórki, który się nie zmieści w jednej linii przy wąskim oknie edytora |\n| x \\| y | z |\n";

    private static (MainWindow W, EditorDocument Doc) Open(bool wrap)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "t.md");
        File.WriteAllText(path, Table);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 700 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static AvaloniaEdit.Rendering.VisualLine Line(MainWindow w, int n) =>
        w.Editor.TextArea.TextView.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(n));

    private static void Key(MainWindow w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.CellEditor!.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow W, EditorDocument Doc) OpenText(string source)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "k.md");
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = true }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static void AssertTableIntact(string text, int rows, int cells)
    {
        var md = EelDitor.Markdown.MarkdownDocument.Parse(text);
        Assert.Equal(rows, md.Tables[0].Rows.Count);
        Assert.All(md.Tables[0].Rows, r => Assert.Equal(cells, r.Cells.Count));
    }

    [AvaloniaFact]
    public void WithAllCharactersShown_TheEndOfLineMarkStaysOnTheGridsLine()
    {
        var (w, _) = Open(wrap: true);
        Assert.False(w.Editor.Options.ShowEndOfLine);
        w.RunCommand("view.eol");
        Assert.True(w.Editor.Options.ShowEndOfLine);
        w.Editor.TextArea.TextView.Redraw();
        Dispatcher.UIThread.RunJobs();
        w.Editor.TextArea.TextView.EnsureVisualLines();
        // The mark follows the grid on the same text line instead of wrapping onto a line of its own.
        for (int n = 3; n <= 6; n++) Assert.Single(Line(w, n).TextLines);
        // With room to spare: the mark's glyph may come from a wider fallback font.
        var tv = w.Editor.TextArea.TextView;
        for (int n = 3; n <= 6; n++) Assert.True(Line(w, n).TextLines[0].WidthIncludingTrailingWhitespace <= tv.Bounds.Width - 2 * tv.WideSpaceWidth);
    }

    [AvaloniaFact]
    public void WithWordWrap_TableRowsAreAsciiGrids_WithWrappedCells()
    {
        var (w, _) = Open(wrap: true);
        var row = Line(w, 5);
        Assert.Single(row.Elements);
        Assert.Equal("TableRowElement", row.Elements[0].GetType().Name);
        Assert.True(row.Height > 2.5 * w.Editor.TextArea.TextView.DefaultLineHeight); // wrapped text + the rule below
        Assert.NotEqual("TableRowElement", Line(w, 1).Elements[0].GetType().Name);   // ordinary text stays text

        var (plain, _) = Open(wrap: false);
        Assert.DoesNotContain(Line(plain, 5).Elements, e => e.GetType().Name == "TableRowElement");
    }

    [AvaloniaFact]
    public void CellEditor_ChangesOnlyTheCellText()
    {
        var (w, doc) = Open(wrap: true);
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(5).Offset;
        w.RunCommand("md.table.editCell");
        Assert.Equal("krótki", w.CellEditor!.Text);
        w.CellEditor.Text = "nowy | tekst";
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Null(w.CellEditor);
        Assert.Contains("| nowy \\| tekst | bardzo długi", doc.Document.Text);
        Assert.EndsWith("| x \\| y | z |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void CellEditor_HidesTheMarkers_AndADeletedBacktickCannotBreakTheTable()
    {
        var (w, doc) = OpenText("| command | use |\n|---|---|\n| `lookup` | `find` |\n| `entry <id>` | the entry |\n");
        w.EditTableCell(3, 0);
        var editor = w.CellEditor!;
        Assert.Equal("`lookup`", editor.Text);
        var visual = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
        Assert.Contains(visual.Elements, e => e.GetType().Name == "HiddenElement");   // the backticks are not shown
        editor.Text = "lookup`";                          // the opening backtick deleted: it would pair with `find`
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Null(w.CellEditor);
        Assert.Contains("| lookup\\` | `find` |", doc.Document.Text);
        Assert.Contains("| `entry <id>` | the entry |", doc.Document.Text);
        AssertTableIntact(doc.Document.Text, 4, 2);
    }

    [AvaloniaFact]
    public void CellEditor_CtrlB_MakesTheSelectionBold()
    {
        var (w, doc) = OpenText("| a | b |\n|---|---|\n| find all | x |\n");
        w.EditTableCell(3, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.CellEditor!.Bounds.Width > 100 && w.CellEditor.Bounds.Height > 10, w.CellEditor.Bounds.ToString());
        w.CellEditor!.Select(5, 3);
        Key(w, Avalonia.Input.Key.B, KeyModifiers.Control);
        Assert.Equal("find **all**", w.CellEditor!.Text);
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Contains("| find **all** | x |", doc.Document.Text);
    }

    [AvaloniaFact]
    public void CtrlB_OnASelectedRow_BoldsEachCell_AndKeepsTheTable()
    {
        var (w, doc) = OpenText("| a | b |\n|---|---|\n| raz | dwa |\n| trzy | cztery |\n");
        var line = doc.Document.GetLineByNumber(3);
        w.Editor.Select(line.Offset, line.Length);
        w.RunCommand("md.bold");
        Assert.Contains("| **raz** | **dwa** |", doc.Document.Text);
        AssertTableIntact(doc.Document.Text, 4, 2);
        w.RunCommand("md.bold");                         // toggles back
        Assert.Contains("| raz | dwa |", doc.Document.Text);

        // Several rows, and a selection starting in the middle of a cell.
        var l4 = doc.Document.GetLineByNumber(4);
        w.Editor.Select(line.Offset + 3, l4.EndOffset - line.Offset - 3);
        w.RunCommand("md.bold");
        Assert.Contains("| r**az** | **dwa** |\n| **trzy** | **cztery** |", doc.Document.Text);
        AssertTableIntact(doc.Document.Text, 4, 2);
    }

    [AvaloniaFact]
    public void CtrlB_PressedOnARowSelectedWithTheMouse_InTheGrid()
    {
        var (w, doc) = OpenText("Tekst.\n\n| a | b |\n|---|---|\n| raz | dwa |\n| trzy | cztery |\n");
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var vl = tv.GetOrConstructVisualLine(doc.Document.GetLineByNumber(5));
        var from = tv.TranslatePoint(vl.GetVisualPosition(0, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - tv.ScrollOffset, w)!.Value;
        var to = tv.TranslatePoint(vl.GetVisualPosition(vl.VisualLength, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - tv.ScrollOffset, w)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(w, from + new Avalonia.Vector(1, 0), MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(w, to - new Avalonia.Vector(1, 0), RawInputModifiers.LeftMouseButton);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(w, to - new Avalonia.Vector(1, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(w, Avalonia.Input.Key.B, RawInputModifiers.Control, PhysicalKey.B, "b");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("| **raz** | **dwa** |", doc.Document.Text);
        AssertTableIntact(doc.Document.Text, 4, 2);
    }

    [AvaloniaFact]
    public void CtrlB_OnRowsWithStarsAndStyles_NeverShowsStrayStars()
    {
        // The table of levels from the user's file: cells with a lone "*", "&", "*slovo(\*)*", "**slovo**(\*)".
        string table = string.Join("\n",
            "| level | when | plain text and Markdown |",
            "|---|---|---|",
            "| 0 | dictionary word fitting by both glosses | no mark |",
            "| * | a word from supplement.md | *slovo(\\*)*; in Markdown **slovo**(\\*) |",
            "| & | a word made by **standard word formation** | *slovo(&)* |",
            "| 3 | any other word | *slovo(!)* |");
        var (w, doc) = OpenText("Tekst.\n\n" + table + "\n");
        string Shown(int lineNumber)
        {
            var md = EelDitor.Markdown.MarkdownDocument.Parse(doc.Document.Text);
            string text = doc.Document.GetText(doc.Document.GetLineByNumber(lineNumber));
            return EelDitor.Markdown.InlineStyleEdit.Visible(text, md.GetLine(lineNumber - 1)).Text;
        }
        for (int n = 5; n <= 8; n++)
        {
            string before = Shown(n);
            var line = doc.Document.GetLineByNumber(n);
            for (int times = 0; times < 2; times++) // on, then off
            {
                w.Editor.Select(line.Offset, doc.Document.GetLineByNumber(n).Length);
                w.RunCommand("md.bold");
                Assert.Equal(before, Shown(n));                // the same text shows: no stray stars
                AssertTableIntact(doc.Document.Text, 6, 3);
            }
        }
    }

    [AvaloniaFact]
    public void CtrlB_WithTheCaretOnARow_BoldsTheWholeRow()
    {
        foreach (bool wrap in new[] { false, true })
        {
            string dir = TestDirs.New();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "r.md");
            File.WriteAllText(path, "| a | b |\n|---|---|\n| raz | dwa |\n");
            SettingsStore.UseDirectory(TestDirs.New());
            var w = new MainWindow(new AppSettings { WordWrap = wrap }, [path]);
            w.Show();
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path);
            w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).Offset;    // before the first pipe
            w.RunCommand("md.bold");
            Assert.Contains("| **raz** | **dwa** |", doc.Document.Text);
            w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).EndOffset; // after the last pipe: off again
            w.RunCommand("md.bold");
            Assert.Contains("| raz | dwa |", doc.Document.Text);
            AssertTableIntact(doc.Document.Text, 3, 2);
        }
    }

    [AvaloniaFact]
    public void CellEditor_TabMovesToTheNextCell_EscCancels()
    {
        var (w, doc) = Open(wrap: true);
        w.EditTableCell(5, 1);
        Assert.StartsWith("bardzo długi", w.CellEditor!.Text);
        Key(w, Avalonia.Input.Key.Tab);              // no change: saves nothing, moves to the next row
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("x \\| y", w.CellEditor!.Text);  // the backslash is hidden in the view
        w.CellEditor.Text = "zmiana";
        Key(w, Avalonia.Input.Key.Escape);
        Assert.Null(w.CellEditor);
        Assert.Equal(Table, doc.Document.Text);

        w.EditTableCell(6, 0);
        Key(w, Avalonia.Input.Key.Tab, KeyModifiers.Shift);  // back to the previous row's last cell
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("bardzo długi", w.CellEditor!.Text);
        Key(w, Avalonia.Input.Key.Escape);
    }
}
