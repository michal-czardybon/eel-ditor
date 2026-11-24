using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Tables drawn as grids: long rows, &lt;br&gt;, the hidden caret, selecting shown text, the cell editor's size.</summary>
public class TableGridExtrasTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string source, bool wrap = true)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "g.md");
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static AvaloniaEdit.Rendering.VisualLine Line(MainWindow w, int n) =>
        w.Editor.TextArea.TextView.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(n));

    private static MarkdownView View(MainWindow w) => w.MarkdownViewForTests!;

    private static void CellKey(MainWindow w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.CellEditor!.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        Dispatcher.UIThread.RunJobs();
    }

    private static string LongCell(int length) => string.Join(" ", Enumerable.Range(0, length / 6).Select(i => "**s" + (i % 10) + "**"));

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARowLongerThan3000Characters_IsDrawnLikeTheOthers(bool wrap)
    {
        var (w, doc) = Open("x\n\n| a | b |\n|---|---|\n| 1 | " + LongCell(4000) + " |\n| 2 | k |\n", wrap);
        Assert.True(doc.Document.GetLineByNumber(5).Length > 3000);
        var elements = Line(w, 5).Elements;
        if (wrap) Assert.IsType<TableRowElement>(Assert.Single(elements));   // the grid row
        else Assert.Contains(elements, e => e is HiddenElement);              // the ** are hidden
        w.Close();
    }

    [AvaloniaFact]
    public void OnAGridRow_TheCaretIsHidden_AndTheRowIsOutlined()
    {
        var (w, doc) = Open("tekst\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        var caret = w.Editor.TextArea.Caret;
        IBrush? normal = caret.CaretBrush;
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(5).Offset;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(Brushes.Transparent, caret.CaretBrush);
        Assert.NotNull(View(w).CaretGridRow);
        w.Editor.CaretOffset = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(normal, caret.CaretBrush);
        Assert.Null(View(w).CaretGridRow);

        // Without word wrap the table is text: the caret is shown.
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(5).Offset + 2;
        w.Editor.WordWrap = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(normal, caret.CaretBrush);
        w.Close();
    }

    [AvaloniaFact]
    public void TheCurrentCell_FollowsTheCaret_AndTheLineBandIsOffOnGridRows()
    {
        var (w, doc) = Open("tekst\n\n| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n");
        Assert.True(w.Editor.Options.HighlightCurrentLine);
        var line = doc.Document.GetLineByNumber(5);
        w.Editor.CaretOffset = line.Offset + 2;                 // in "1"
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((5, 0), View(w).CurrentCell);
        Assert.Equal(0, Assert.IsType<TableRowElement>(Assert.Single(Line(w, 5).Elements)).CurrentCell);
        Assert.False(w.Editor.Options.HighlightCurrentLine);     // the band would cover the whole row

        w.Editor.TextArea.Focus();
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(w, Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((5, 1), View(w).CurrentCell);              // Tab: the next cell
        Assert.Equal(1, Assert.IsType<TableRowElement>(Assert.Single(Line(w, 5).Elements)).CurrentCell);

        w.Editor.CaretOffset = 1;                               // out of the table
        Dispatcher.UIThread.RunJobs();
        Assert.Null(View(w).CurrentCell);
        Assert.True(w.Editor.Options.HighlightCurrentLine);
        Assert.Null(Assert.IsType<TableRowElement>(Assert.Single(Line(w, 5).Elements)).CurrentCell);
        w.Close();
    }

    [AvaloniaFact]
    public void SelectColumnAndRow_InAGrid()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| 1 | dwa |\n| 3 | cztery |\n");
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).Offset + 6;   // in "dwa"
        Dispatcher.UIThread.RunJobs();
        w.RunCommand("md.table.selCol");
        string nl = Environment.NewLine;
        Assert.Equal("b" + nl + "dwa" + nl + "cztery", View(w).GridSelection.Text);
        w.RunCommand("md.table.selRow");
        Assert.Equal("1\tdwa", View(w).GridSelection.Text);
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Styles_ApplyToASelectedColumn(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| 1 | dwa |\n| 3 | cztery |\n", wrap);
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).Offset + 6;   // in "dwa"
        Dispatcher.UIThread.RunJobs();
        w.RunCommand("md.table.selCol");
        w.RunCommand("md.highlight");
        Assert.Equal("| a | ==b== |\n|---|---|\n| 1 | ==dwa== |\n| 3 | ==cztery== |\n", doc.Document.Text);
        if (wrap) Assert.True(View(w).GridSelection.HasSelection);   // still selected: Ctrl+B next applies to it too
        w.RunCommand("md.clearFormat");
        Assert.Equal("| a | b |\n|---|---|\n| 1 | dwa |\n| 3 | cztery |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void InAGrid_AStyleWithoutASelection_AppliesToTheCurrentCell()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| 1 | dwa |\n");
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).Offset + 6;
        Dispatcher.UIThread.RunJobs();
        w.RunCommand("md.bold");
        Assert.Equal("| a | b |\n|---|---|\n| 1 | **dwa** |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void InAGrid_PartOfACell_GetsTheStyle()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **ala** ma kota | x |\n");
        View(w).GridSelection.Select(new(3, 0, 4), new(3, 0, 6));   // "ma" in the shown "ala ma kota"
        w.RunCommand("md.italic");
        Assert.Equal("| a | b |\n|---|---|\n| **ala** *ma* kota | x |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void BrInACell_IsALineBreakInTheGrid_AndInTheCellEditor()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| jeden<br>dwa <br/> trzy | x |\n");
        var row = Assert.IsType<TableRowElement>(Assert.Single(Line(w, 3).Elements));
        Assert.Equal(["jeden", "dwa", "trzy"], row.Cells[0].Lines.Select(l => l.Text));

        w.EditTableCell(3, 0);
        Assert.Equal("jeden\ndwa\ntrzy", w.CellEditor!.Text);
        // Unchanged: the tags stay as written.
        CellKey(w, Key.Enter, KeyModifiers.Control);
        Assert.Contains("| jeden<br>dwa <br/> trzy | x |", doc.Document.Text);

        w.EditTableCell(3, 0);
        w.CellEditor!.TextArea.ClearSelection();
        w.CellEditor.CaretOffset = w.CellEditor.Text.Length;
        CellKey(w, Key.Enter);                             // a new line, not the end of the edit
        Assert.NotNull(w.CellEditor);
        w.CellEditor.Document.Insert(w.CellEditor.CaretOffset, "cztery");
        CellKey(w, Key.Enter, KeyModifiers.Shift);         // Shift+Enter too
        Assert.NotNull(w.CellEditor);
        w.CellEditor.Document.Insert(w.CellEditor.CaretOffset, "pięć");
        CellKey(w, Key.Enter, KeyModifiers.Control);
        Assert.Null(w.CellEditor);
        Assert.Contains("| jeden<br>dwa<br>trzy<br>cztery<br>pięć | x |", doc.Document.Text);
    }

    [AvaloniaFact]
    public void TextInAGrid_CanBeSelectedAndCopied()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **ala** ma | kota |\n| pies | kot |\n");
        var sel = View(w).GridSelection;
        sel.Select(new(3, 0, 0), new(3, 0, 3));
        Assert.Equal("ala", sel.Text);                       // the shown text, without **
        sel.Select(new(3, 1, 0), new(4, 0, 2));
        Assert.Equal("ala ma\tkota" + Environment.NewLine + "pies\tkot", sel.Text);

        // The row element maps a point to the text: the first text line, three characters into the first cell.
        var row = Assert.IsType<TableRowElement>(Assert.Single(Line(w, 3).Elements));
        Assert.Equal((0, 3), row.Hit(2 + 3, 0));
        Assert.Equal((1, 0), row.Hit(row.Layout.Widths[0] + 3 + 2, 0));

        // Moving the caret drops the selection; the source never changes.
        string before = doc.Document.Text;
        w.Editor.CaretOffset = 5;
        Dispatcher.UIThread.RunJobs();
        Assert.False(sel.HasSelection);
        Assert.Equal(before, doc.Document.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void DraggingInACell_SelectsItsShownText()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **ala** ma | kota |\n| pies | kot |\n");
        var tv = w.Editor.TextArea.TextView;
        var visual = Line(w, 3);
        double cw = tv.WideSpaceWidth, lh = tv.DefaultLineHeight;
        double x0 = visual.GetVisualPosition(0, AvaloniaEdit.Rendering.VisualYPosition.LineTop).X - tv.ScrollOffset.X;
        double y = visual.VisualTop - tv.ScrollOffset.Y + lh / 2;
        Avalonia.Point At(double column) => tv.TranslatePoint(new Avalonia.Point(x0 + column * cw, y), w)!.Value;

        w.MouseDown(At(2 + 0.1), MouseButton.Left);           // before "ala"
        w.MouseMove(At(2 + 3.1), RawInputModifiers.LeftMouseButton);
        w.MouseUp(At(2 + 3.1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ala", View(w).GridSelection.Text);
        Assert.Equal(doc.Document.GetLineByNumber(3).Offset + 2, w.Editor.CaretOffset);   // the caret went into the cell
        Assert.Equal((3, 0), View(w).CurrentCell);
        Assert.Equal(0, w.Editor.SelectionLength);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ARightClick_MakesTheClickedCellCurrent(int cell)
    {
        var (w, _) = Open("| a | b | c |\n|---|---|---|\n| jeden | dwa | trzy |\n");
        var tv = w.Editor.TextArea.TextView;
        var visual = Line(w, 3);
        var row = Assert.IsType<TableRowElement>(Assert.Single(visual.Elements));
        double cw = tv.WideSpaceWidth, lh = tv.DefaultLineHeight;
        double x0 = visual.GetVisualPosition(0, AvaloniaEdit.Rendering.VisualYPosition.LineTop).X - tv.ScrollOffset.X;
        double y = visual.VisualTop - tv.ScrollOffset.Y + lh / 2;
        double column = EelDitor.Markdown.TableWrap.BorderAt(row.Layout.Widths, cell) + 2.5;   // inside the cell's text
        var at = tv.TranslatePoint(new Avalonia.Point(x0 + column * cw, y), w)!.Value;

        w.MouseDown(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((3, cell), View(w).CurrentCell);
        w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        w.Close();
    }

    [AvaloniaFact]
    public void CellEditor_IsTallEnoughForALongCell()
    {
        string text = string.Join(" ", Enumerable.Range(0, 120).Select(i => "słowo" + i));
        var (w, _) = Open("| a | b |\n|---|---|\n| " + text + " | x |\n");
        w.EditTableCell(3, 0);
        Dispatcher.UIThread.RunJobs();
        var editor = w.CellEditor!;
        var tv = editor.TextArea.TextView;
        tv.EnsureVisualLines();
        double content = tv.DocumentHeight;
        Assert.True(content <= editor.Bounds.Height - tv.DefaultLineHeight * 0.5,
            $"content {content} does not fit {editor.Bounds.Height}");
        w.Close();
    }

    [Theory]
    [InlineData("abc def", 3, 2)]
    [InlineData("abc def", 7, 1)]
    [InlineData("a\nb", 10, 2)]
    [InlineData("abcdefgh", 3, 3)]
    public void WrappedLineCount_CountsWordWrappedLines(string text, int columns, int lines) =>
        Assert.Equal(lines, MainWindow.WrappedLineCount(text, columns));
}
