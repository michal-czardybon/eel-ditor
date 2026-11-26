using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>
/// Commands on selected table cells and on selected text inside a table, with the table as text (no wrap) and as a
/// grid (wrap): styles on and off, clear formatting, convert style, the current cell after clicks and the cell editor.
/// </summary>
public class TableCellOperationsTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string source, bool wrap)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "t.md");
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static MarkdownView View(MainWindow w) => w.MarkdownViewForTests!;

    private static string Line(EditorDocument doc, int n) => doc.Document.GetText(doc.Document.GetLineByNumber(n));

    private static void CaretIn(MainWindow w, EditorDocument doc, int line, string text)
    {
        var l = doc.Document.GetLineByNumber(line);
        w.Editor.TextArea.ClearSelection();
        w.Editor.CaretOffset = l.Offset + doc.Document.GetText(l).IndexOf(text, StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Run(MainWindow w, string command)
    {
        w.RunCommand(command);
        Dispatcher.UIThread.RunJobs();
    }

    public static TheoryData<bool, string, string> StyleCases()
    {
        var data = new TheoryData<bool, string, string>();
        foreach (bool wrap in new[] { false, true })
            foreach (var (cmd, marker) in new[] { ("md.bold", "**"), ("md.italic", "*"), ("md.strike", "~~"), ("md.code", "`"), ("md.highlight", "=="), ("md.sub", "~"), ("md.sup", "^") })
                data.Add(wrap, cmd, marker);
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(StyleCases))]
    public void ASelectedRow_GetsEachStyle_AndLosesItAgain(bool wrap, string command, string marker)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| raz | dwa |\n| x | y |\n", wrap);
        CaretIn(w, doc, 3, "raz");
        Run(w, "md.table.selRow");
        Run(w, command);
        Assert.Equal($"| {marker}raz{marker} | {marker}dwa{marker} |", Line(doc, 3));
        Assert.Equal("| x | y |", Line(doc, 4));
        Run(w, command);                                   // the same cells, still selected: off again
        Assert.Equal("| raz | dwa |", Line(doc, 3));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearFormatting_OfASelectedRow(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **raz** *i* dwa | ==x== `y` [l](http://x) |\n| **z** | q |\n", wrap);
        CaretIn(w, doc, 3, "raz");
        Run(w, "md.table.selRow");
        Run(w, "md.clearFormat");
        Assert.Equal("| raz i dwa | x y l |", Line(doc, 3));
        Assert.Equal("| **z** | q |", Line(doc, 4));       // other rows untouched
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASelectedColumn_IncludingTheHeader(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| raz | **dwa** |\n| x | y |\n", wrap);
        CaretIn(w, doc, 3, "dwa");
        Run(w, "md.table.selCol");
        Run(w, "md.italic");
        Assert.Equal("| a | *b* |\n|---|---|\n| raz | ***dwa*** |\n| x | *y* |\n", doc.Document.Text);
        Run(w, "md.clearFormat");
        Assert.Equal("| a | b |\n|---|---|\n| raz | dwa |\n| x | y |\n", doc.Document.Text);
        Run(w, "md.highlight");
        Run(w, "edit.convert.HighlightBold");
        Assert.Equal("| a | **b** |\n|---|---|\n| raz | **dwa** |\n| x | **y** |\n", doc.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHeaderRow(bool wrap)
    {
        var (w, doc) = Open("| a | **b** |\n|---|---|\n| 1 | 2 |\n", wrap);
        CaretIn(w, doc, 1, "a");
        Run(w, "md.table.selRow");
        Run(w, "md.highlight");
        Assert.Equal("| ==a== | ==**b**== |", Line(doc, 1));
        Run(w, "md.clearFormat");
        Assert.Equal("| a | b |", Line(doc, 1));
    }

    [AvaloniaFact]
    public void InAGrid_PartOfACell_StyleAndClear()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **abcdef** | x |\n", wrap: true);
        View(w).GridSelection.Select(new(3, 0, 2), new(3, 0, 4));     // "cd"
        Run(w, "md.clearFormat");
        Assert.Equal("| **ab**cd**ef** | x |", Line(doc, 3));
        View(w).GridSelection.Select(new(3, 0, 2), new(3, 0, 4));
        Run(w, "md.bold");
        Assert.Equal("| **abcdef** | x |", Line(doc, 3));           // joined again
        View(w).GridSelection.Select(new(3, 0, 0), new(3, 0, 6));
        Run(w, "md.italic");
        Assert.Equal("| ***abcdef*** | x |", Line(doc, 3));
    }

    [AvaloniaFact]
    public void InAGrid_DraggedCells_Style()
    {
        var (w, doc) = Open("| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |\n", wrap: true);
        View(w).GridSelection.Select(new(3, 1, 0), new(4, 2, 1));     // 2 3 / 5 6
        Run(w, "md.bold");
        Assert.Equal("| 1 | **2** | **3** |\n| 4 | **5** | **6** |", Line(doc, 3) + "\n" + Line(doc, 4));
    }

    [AvaloniaFact]
    public void InText_ASelectionInsideACell_AndOverTwoCells()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| ala ma | kota psa |\n", wrap: false);
        var l = doc.Document.GetLineByNumber(3);
        int at = l.Offset + Line(doc, 3).IndexOf("ma", StringComparison.Ordinal);
        w.Editor.Select(at, 2);
        Run(w, "md.bold");
        Assert.Equal("| ala **ma** | kota psa |", Line(doc, 3));
        // Over two cells: each cell's selected part, the pipe stays outside.
        int from = l.Offset + Line(doc, 3).IndexOf("ala", StringComparison.Ordinal);
        int to = l.Offset + Line(doc, 3).IndexOf(" psa", StringComparison.Ordinal);
        w.Editor.Select(from, to - from);
        Run(w, "md.clearFormat");
        Assert.Equal("| ala ma | kota psa |", Line(doc, 3));
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers mods, PhysicalKey physical)
    {
        w.KeyPress(key, mods, physical, null);
        w.KeyRelease(key, mods, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shortcuts_ActOnTheSelectedCells(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| ==raz== | ==dwa== |\n", wrap);
        CaretIn(w, doc, 3, "raz");
        Run(w, "md.table.selRow");
        w.Editor.TextArea.Focus();
        Press(w, Key.B, RawInputModifiers.Control, PhysicalKey.B);
        Assert.Equal("| **==raz==** | **==dwa==** |", Line(doc, 3));
        Press(w, Key.Space, RawInputModifiers.Control, PhysicalKey.Space);
        Assert.Equal("| raz | dwa |", Line(doc, 3));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoSelection_TheCellAtTheCaret(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **raz** | ==dwa== |\n", wrap);
        CaretIn(w, doc, 3, "dwa");
        Run(w, "md.clearFormat");
        Assert.Equal("| **raz** | dwa |", Line(doc, 3));
    }

    private static Point CellPoint(MainWindow w, int line, int cell)
    {
        var tv = w.Editor.TextArea.TextView;
        var visual = tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(line));
        var row = Assert.IsType<TableRowElement>(Assert.Single(visual.Elements));
        double x0 = visual.GetVisualPosition(0, AvaloniaEdit.Rendering.VisualYPosition.LineTop).X - tv.ScrollOffset.X;
        double y = visual.VisualTop - tv.ScrollOffset.Y + tv.DefaultLineHeight / 2;
        double column = EelDitor.Markdown.TableWrap.BorderAt(row.Layout.Widths, cell) + 2.5;
        return tv.TranslatePoint(new Point(x0 + column * tv.WideSpaceWidth, y), w)!.Value;
    }

    [AvaloniaFact]
    public void ShiftArrows_ExtendAndShrinkAWholeCellRectangle()
    {
        var (w, _) = Open("| a | b | c |\n|---|---|---|\n| left |  | right |\n", wrap: true);
        var at = CellPoint(w, 3, 0);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);

        Press(w, Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight);
        var selection = View(w).GridSelection;
        var block = selection.CellBlock!.Value;
        Assert.Equal([3], block.Lines);
        Assert.Equal((0, 1), (block.FirstCell, block.LastCell));
        var anchor = new GridTextSelection.Point(3, 0, 0);
        Assert.Equal(anchor, selection.Anchor);
        Assert.Equal(0, w.Editor.SelectionLength);
        Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
        Assert.Equal("| left |  |", Await(w.Clipboard!.TryGetTextAsync())!.TrimEnd());

        Press(w, Key.Up, RawInputModifiers.Shift, PhysicalKey.ArrowUp);
        Assert.Equal([1, 3], selection.CellBlock!.Value.Lines);
        Assert.Equal(anchor, selection.Anchor);
        Assert.Equal((1, 1), View(w).CurrentCell);
        Press(w, Key.Up, RawInputModifiers.Shift, PhysicalKey.ArrowUp);
        Assert.Equal((1, 1), View(w).CurrentCell);
        Assert.Equal(anchor, selection.Anchor);
        Press(w, Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown);
        Assert.Equal(new GridTextSelection.Point(3, 1, 0), selection.Active);
        Assert.Equal(anchor, selection.Anchor);
        Assert.Equal((3, 1), View(w).CurrentCell);
        Press(w, Key.Left, RawInputModifiers.Shift, PhysicalKey.ArrowLeft);
        Assert.Equal(selection.Anchor, selection.Active);
        Assert.Equal((3, 0), View(w).CurrentCell);
        Assert.Equal(0, w.Editor.SelectionLength);
    }

    [AvaloniaFact]
    public void DeleteClearsSelectedGridCellsAndKeepsTheSelection()
    {
        const string source = "| a | b | c |\n|:---|---|---:|\n| **left** | x<br>y | keep |\n| one |  | tail |\n";
        var (w, doc) = Open(source, wrap: true);
        var at = CellPoint(w, 3, 0);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Press(w, Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight);
        Press(w, Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown);
        Press(w, Key.Delete, RawInputModifiers.None, PhysicalKey.Delete);

        Assert.Equal("| a | b | c |\n|:---|---|---:|\n|  |  | keep |\n|  |  | tail |\n", doc.Document.Text);
        var block = View(w).GridSelection.CellBlock!.Value;
        Assert.Equal([3, 4], block.Lines);
        Assert.Equal((0, 1), (block.FirstCell, block.LastCell));
        Assert.Equal((4, 1), View(w).CurrentCell);
        Press(w, Key.Z, RawInputModifiers.Control, PhysicalKey.Z);
        Assert.Equal(source, doc.Document.Text);
        Assert.Equal((0, 1), (View(w).GridSelection.CellBlock!.Value.FirstCell, View(w).GridSelection.CellBlock!.Value.LastCell));
        Press(w, Key.Y, RawInputModifiers.Control, PhysicalKey.Y);
        Assert.Equal("| a | b | c |\n|:---|---|---:|\n|  |  | keep |\n|  |  | tail |\n", doc.Document.Text);
        Assert.Equal([3, 4], View(w).GridSelection.CellBlock!.Value.Lines);
        Press(w, Key.Z, RawInputModifiers.Control, PhysicalKey.Z);
        Run(w, "md.clearFormat");
        Assert.Contains("| left | x<br>y | keep |", doc.Document.Text);
        Run(w, "edit.undo");
        Assert.Equal(source, doc.Document.Text);
        Assert.Equal([3, 4], View(w).GridSelection.CellBlock!.Value.Lines);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteClearsASelectedColumn_WithImplicitOuterPipes(bool wrap)
    {
        var (w, doc) = Open("a | b\n:--- | ---:\n1 | 2\n", wrap);
        w.Editor.TextArea.Focus();
        CaretIn(w, doc, 3, "2");
        Run(w, "md.table.selCol");
        Press(w, Key.Delete, RawInputModifiers.None, PhysicalKey.Delete);
        Assert.Equal("a | |\n:--- | ---:\n1 | |\n", doc.Document.Text);
        Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
        Assert.Equal("|  |\n|---|\n|  |", Await(w.Clipboard!.TryGetTextAsync())!.Replace("\r\n", "\n").TrimEnd());
    }

    [AvaloniaFact]
    public void StructuralDeletionUsesTheGridBlock_AndUndoRestoresIt()
    {
        const string source = "a | b | c | d\n:--- | --- | ---: | ---\n1 | 2 | 3 | 4\n5 | 6 | 7 | 8\n9 | 10 | 11 | 12\n";
        var (w, doc) = Open(source, wrap: true);
        CaretIn(w, doc, 4, "7");
        var grid = View(w).GridSelection;
        grid.Select(new(3, 1, 0), new(4, 2, 0));
        Run(w, "md.table.delCol");
        Assert.Equal("a | d\n:--- | ---\n1 | 4\n5 | 8\n9 | 12\n", doc.Document.Text);
        Run(w, "edit.undo");
        Assert.Equal(source, doc.Document.Text);
        Assert.Equal([3, 4], grid.CellBlock!.Value.Lines);
        Assert.Equal((1, 2), (grid.CellBlock.Value.FirstCell, grid.CellBlock.Value.LastCell));

        grid.Select(new(3, 0, 0), new(4, 3, 0));
        Run(w, "md.table.delCol");
        Assert.Equal("", doc.Document.Text);
        Run(w, "edit.undo");
        Assert.Equal(source, doc.Document.Text);
        Assert.Equal(3, grid.CellBlock!.Value.LastCell);

        Run(w, "md.table.delRow");
        Assert.Equal("a | b | c | d\n:--- | --- | ---: | ---\n9 | 10 | 11 | 12\n", doc.Document.Text);
        Run(w, "edit.undo");
        Assert.Equal(source, doc.Document.Text);
        Assert.Equal([3, 4], grid.CellBlock!.Value.Lines);
    }

    [AvaloniaFact]
    public void ModifiedClicks_ExtendFromTheCaretOrStartAFreshCellSelection()
    {
        var (w, doc) = Open("| a | b | c |\n|---|---|---|\n| left | middle | right |\n| one | two | three |\n", wrap: true);
        CaretIn(w, doc, 3, "left");
        void Click(int line, int cell, RawInputModifiers modifiers)
        {
            var at = CellPoint(w, line, cell);
            w.MouseDown(at, MouseButton.Left, modifiers);
            w.MouseUp(at, MouseButton.Left, modifiers);
        }
        Click(4, 1, RawInputModifiers.Shift);
        var selection = View(w).GridSelection;
        var block = selection.CellBlock!.Value;
        Assert.Equal([3, 4], block.Lines);
        Assert.Equal((0, 1), (block.FirstCell, block.LastCell));
        Click(4, 1, RawInputModifiers.Shift); // Rapid repeat stays a selection gesture.
        Assert.Null(w.CellEditor);
        Assert.Equal(0, w.Editor.SelectionLength);
        Assert.Equal((4, 1), View(w).CurrentCell);

        Click(4, 2, RawInputModifiers.Control);
        Assert.False(selection.HasSelection);
        Assert.Equal((4, 2), View(w).CurrentCell);
        Assert.Equal(0, w.Editor.SelectionLength);
        Press(w, Key.Left, RawInputModifiers.Shift, PhysicalKey.ArrowLeft);
        Assert.Equal((1, 2), (selection.CellBlock!.Value.FirstCell, selection.CellBlock.Value.LastCell));
    }

    [AvaloniaFact]
    public void ADoubleClick_EditsTheCell_AndItStaysCurrent()
    {
        var (w, _) = Open("| a | b | c |\n|---|---|---|\n| jeden | dwa | trzy |\n", wrap: true);
        var at = CellPoint(w, 3, 1);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        w.MouseDown(at, MouseButton.Left);                  // the second press: a double click
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(w.CellEditor);
        Assert.Equal("dwa", w.CellEditor!.Text);
        Assert.Equal((3, 1), View(w).CurrentCell);
        CloseEditor(w);
    }

    /// <summary>Leaves no open cell editor (and so no open undo group) behind: the next test's window waited on it.</summary>
    private static void CloseEditor(MainWindow w)
    {
        if (w.CellEditor is not null) CellKey(w, Key.Escape);
    }

    [AvaloniaFact]
    public void TheContextMenu_OnASelection_ClearsItsFormatting()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| ==1== | **dwa** |\n| ==3== | *cztery* |\n", wrap: true);
        CaretIn(w, doc, 3, "dwa");
        Run(w, "md.table.selCol");
        var at = CellPoint(w, 4, 1);                          // inside the selection: it stays
        w.MouseDown(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(View(w).GridSelection.HasSelection);
        w.Editor.RaiseEvent(new Avalonia.Controls.ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        var menu = w.Editor.ContextMenu!;
        var clear = menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Md.ClearFormat"));
        clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("| a | b |\n|---|---|\n| ==1== | dwa |\n| ==3== | cztery |\n", doc.Document.Text);
        w.MouseUp(at, MouseButton.Right);
    }

    private static T Await<T>(Task<T> task)
    {
        while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        return task.GetAwaiter().GetResult();
    }

    private static void Settle()
    {
        for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    private static TableRowElement GridRow(MainWindow w, int line)
    {
        var tv = w.Editor.TextArea.TextView;
        return Assert.IsType<TableRowElement>(Assert.Single(tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(line)).Elements));
    }

    [AvaloniaFact]
    public void WrappedCell_LongWordsAndProseRaiseTheRowAndKeepAdjacentCells()
    {
        var (w, doc) = Open("| left | middle | right |\n|---|---|---|\n| keep | x | tail |\n\nAfter", wrap: true);
        double original = w.Editor.TextArea.TextView.GetOrConstructVisualLine(doc.Document.GetLineByNumber(3)).Height;
        w.EditTableCell(3, 1);
        var cell = w.CellEditor!;
        string word = new('x', 250);
        string prose = " " + string.Join(" ", Enumerable.Repeat("more text", 45));
        cell.TextArea.PerformTextInput(word);
        Settle();
        Assert.True(GridRow(w, 3).Cells[1].Ranges.Count > 1);
        cell.TextArea.PerformTextInput(prose);
        Settle();
        w.UpdateLayout();
        var row = GridRow(w, 3);
        Assert.Equal(["keep", word + prose, "tail"], row.Cells.Select(c => c.Text));
        Assert.True(w.Editor.TextArea.TextView.GetOrConstructVisualLine(doc.Document.GetLineByNumber(3)).Height > original);
        Assert.Equal("After", Line(doc, 5));
        Run(w, "md.table.editCell");
        Assert.Equal(word + prose, w.CellEditor!.Text);
        CellKey(w, Key.Enter, KeyModifiers.Control);
        doc.Document.UndoStack.MarkAsOriginalFile();
        w.Close();
    }

    [AvaloniaFact]
    public void MultilineCell_VerticalMovementUsesTheNewColumnAfterTyping()
    {
        var (w, _) = Open("| a | b |\n|---|---|\n| 123456789<br>abcdefghijklmno | keep |\n", wrap: true);
        w.EditTableCell(3, 0);
        Settle();
        var cell = w.CellEditor!;
        cell.TextArea.ClearSelection();
        cell.CaretOffset = 2;
        CellKey(w, Key.Down);
        Assert.Equal(3, cell.TextArea.Caret.Column);
        CellKey(w, Key.Up);
        cell.TextArea.PerformTextInput("ABCD");
        Assert.Equal(7, cell.TextArea.Caret.Column);
        CellKey(w, Key.Down);
        Assert.Equal(2, cell.TextArea.Caret.Line);
        Assert.Equal(7, cell.TextArea.Caret.Column);
        CloseEditor(w);
    }

    [AvaloniaFact]
    public void WrappedCell_ColumnGrowsInTheInputTurn_AndOnlyExplicitNewlinesBreakShortText()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| x | keep |\n", wrap: true);
        w.EditTableCell(3, 0);
        Settle();
        var cell = w.CellEditor!;
        var tv = cell.TextArea.TextView;
        double width = cell.Width;
        cell.TextArea.PerformTextInput("abcdefghijk");
        Assert.True(cell.Width > width);
        Assert.Equal("abcdefghijk", GridRow(w, 3).Cells[0].Text);
        w.UpdateLayout();
        tv.EnsureVisualLines();
        Assert.Single(tv.GetVisualLine(1).TextLines);

        cell.TextArea.PerformTextInput("     ");
        w.UpdateLayout();
        tv.EnsureVisualLines();
        Assert.Single(tv.GetVisualLine(1).TextLines);
        Assert.Equal("abcdefghijk     ", cell.Text);
        cell.TextArea.PerformTextInput("z");
        Assert.Equal("abcdefghijk     z", GridRow(w, 3).Cells[0].Text);
        CellKey(w, Key.Enter);
        cell.TextArea.PerformTextInput("next");
        Settle();
        Assert.Equal("abcdefghijk     z\nnext", GridRow(w, 3).Cells[0].Text);
        Assert.Equal("keep", GridRow(w, 3).Cells[1].Text);
        CellKey(w, Key.Enter, KeyModifiers.Control);
        doc.Document.UndoStack.MarkAsOriginalFile();
        w.Close();
    }

    [AvaloniaFact]
    public void WrappedCell_StyledParagraphsCopyAndPasteIntoTheSameAndADifferentCell()
    {
        var (w, doc) = Open("| A | B |\n|---|---|\n| one<br><br>two |  |\n", wrap: true);
        w.EditTableCell(3, 0, 0);
        var cell = w.CellEditor!;
        cell.Select(0, 3);
        Run(w, "md.bold");
        cell.Select(cell.Text!.IndexOf("two", StringComparison.Ordinal), 3);
        Run(w, "md.italic");
        cell.Select(2, "one**\n\n*two".Length);
        Run(w, "edit.copy");
        Assert.Equal("**one**\n\n*two*", Await(w.Clipboard!.TryGetTextAsync())!.Replace("\r\n", "\n"));
        Assert.Same(cell, w.CellEditor);
        Run(w, "edit.selectAll");
        Assert.Equal(cell.Document.TextLength, cell.SelectionLength);
        Run(w, "edit.paste");
        Assert.Equal("**one**\n\n*two*", cell.Text);

        w.EditTableCell(3, 1);
        Assert.Equal("", w.CellEditor!.Text);
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("**one**\n\n*two*", w.CellEditor!.Text);
        Assert.Equal("| **one**<br><br>*two* | **one**<br><br>*two* |", Line(doc, 3));
        var row = GridRow(w, 3);
        Assert.Equal("one\n\ntwo", row.Cells[1].Text);
        Assert.True(row.Cells[1].Styles[0].HasFlag(MdStyle.Bold));
        Assert.True(row.Cells[1].Styles[^1].HasFlag(MdStyle.Italic));
        CellKey(w, Key.Enter, KeyModifiers.Control);
        doc.Document.UndoStack.MarkAsOriginalFile();
        w.Close();
    }

    [AvaloniaFact]
    public void WideGrid_EmptyColumnAndRowAreDisplayedNavigableAndCopyable()
    {
        const int columns = 18;
        static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells) + " |";
        string header = Row(Enumerable.Range(0, columns).Select(i => i == 5 ? "" : $"H{i}"));
        string separator = Row(Enumerable.Repeat("---", columns));
        string empty = Row(Enumerable.Repeat("", columns));
        string source = $"{header}\n{separator}\n{empty}\n{empty}\n\nAfter";
        var (w, doc) = Open(source, wrap: true);
        var view = View(w);
        Assert.Equal(columns, GridRow(w, 1).Cells.Count);
        Assert.All(GridRow(w, 3).Cells, c => Assert.Equal("", c.Text));
        w.EditTableCell(3, 5, 0);
        CellKey(w, Key.Right);
        Assert.Equal((3, 6), view.CurrentCell);
        CellKey(w, Key.Down);
        Assert.Equal((4, 6), view.CurrentCell);
        CellKey(w, Key.Down);
        Assert.Null(w.CellEditor);
        Assert.Equal(5, w.Editor.TextArea.Caret.Line);
        w.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
        Settle();
        Assert.Equal(4, view.CurrentCell?.Line);
        Run(w, "md.bold");
        Assert.Equal(source, doc.Document.Text);
        Run(w, "edit.copy");
        Assert.Equal("|  |", Await(w.Clipboard!.TryGetTextAsync())!.TrimEnd());

        w.EditTableCell(4, columns - 1);
        CellKey(w, Key.B, KeyModifiers.Control);
        Assert.Equal(source, doc.Document.Text);
        w.CellEditor!.TextArea.PerformTextInput("bold");
        Settle();
        Assert.Equal("bold", GridRow(w, 4).Cells[^1].Text);
        Assert.All(GridRow(w, 4).Cells[^1].Styles, style => Assert.True(style.HasFlag(MdStyle.Bold)));
        Assert.Equal("", GridRow(w, 4).Cells[5].Text);
        Await(w.Clipboard!.SetTextAsync("fresh\r\ntext").ContinueWith(_ => 0));
        w.EditTableCell(4, 5);
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("fresh\ntext", GridRow(w, 4).Cells[5].Text);
        Assert.All(GridRow(w, 3).Cells, c => Assert.Equal("", c.Text));
        CellKey(w, Key.Enter, KeyModifiers.Control);
        doc.Document.UndoStack.MarkAsOriginalFile();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyARow_AndOverwriteAnotherRow(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **raz** | dwa |\n| trzy | cztery |\n| pięć | sześć |\n", wrap);
        CaretIn(w, doc, 3, "raz");
        Run(w, "md.table.selRow");
        Run(w, "edit.copy");
        Settle();
        var clipboard = w.Clipboard!;
        Assert.Equal("| **raz** | dwa |" + Environment.NewLine.Replace("\r\n", "\n"), Await(clipboard.TryGetTextAsync())!.Replace("\r\n", "\n"));

        CaretIn(w, doc, 5, "pięć");
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("| a | b |\n|---|---|\n| **raz** | dwa |\n| trzy | cztery |\n| **raz** | dwa |\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();                    // one step
        Assert.Equal("| a | b |\n|---|---|\n| **raz** | dwa |\n| trzy | cztery |\n| pięć | sześć |\n", doc.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyShortcut_CopiesTheCurrentCellOrSelectedColumn(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| left | right |\n", wrap);
        if (wrap)
        {
            var point = CellPoint(w, 3, 1);
            w.MouseDown(point, MouseButton.Left);
            w.MouseUp(point, MouseButton.Left);
        }
        else
            CaretIn(w, doc, 3, "right");
        Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
        Assert.Equal("| right |", Await(w.Clipboard!.TryGetTextAsync())!.TrimEnd());

        Press(w, Key.Up, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.ArrowUp);
        Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
        Assert.Equal("| b |\n|---|\n| right |", Await(w.Clipboard!.TryGetTextAsync())!.Replace("\r\n", "\n").TrimEnd());

        Await(w.Clipboard!.SetTextAsync("previous").ContinueWith(_ => 0));
        Run(w, "md.table.copyCol");
        Assert.Equal("| b |\n|---|\n| right |", Await(w.Clipboard!.TryGetTextAsync())!.Replace("\r\n", "\n").TrimEnd());

        if (wrap)
        {
            var point = CellPoint(w, 3, 0);
            w.Editor.TextArea.TextView.Redraw();
            w.MouseDown(point, MouseButton.Left);
            w.MouseUp(point, MouseButton.Left);
            Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
            Assert.Equal("| left |", Await(w.Clipboard!.TryGetTextAsync())!.TrimEnd());
        }
    }

    [AvaloniaTheory]
    [InlineData(false, "| **left** | right |")]
    [InlineData(true, "| **left** | right |")]
    [InlineData(true, "**left** | right")]
    public void CopyAndPasteShortcuts_PasteABodyRowAsItsOwnTable(bool wrap, string row)
    {
        var (w, doc) = Open($"| a | b |\n|---|---|\n{row}\n\nBefore after\n", wrap);
        CaretIn(w, doc, 3, "left");
        Run(w, "md.table.selRow");
        Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
        CaretIn(w, doc, 5, "after");
        Press(w, Key.V, RawInputModifiers.Control, PhysicalKey.V);
        Settle();

        Assert.Equal(2, View(w).Model.Tables.Count);
        Assert.Equal($"| a | b |\n|---|---|\n{row}\n\nBefore \n\n|  |  |\n|---|---|\n| **left** | right |\n\nafter\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void CutRows_ThenPasteThem_AtTheHeader_OverwritesTheHeader()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", wrap: true);
        View(w).GridSelection.Select(new(4, 0, 0), new(4, 1, 1));   // the whole last row
        Run(w, "edit.cut");
        Settle();
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |\n", doc.Document.Text);
        CaretIn(w, doc, 1, "a");
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("| 3 | 4 |\n|---|---|\n| 1 | 2 |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void PastingTabSeparatedRows_IntoATable()
    {
        var (w, doc) = Open("| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n", wrap: false);
        Await(w.Clipboard!.SetTextAsync("x\ty\r\nz\tq|r\r\n").ContinueWith(_ => 0));
        CaretIn(w, doc, 3, "2");
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("| a | b | c |\n|---|---|---|\n| 1 | x | y |\n|  | z | q\\|r |\n", doc.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData(6, 2)]
    [InlineData(5, 3)]
    public void PasteACopiedBlock_UsesTheTopLeftOfTheSelection(int lastLine, int lastColumn)
    {
        const string source = "| A | B | C | D |\n|:---|---:|---|---|\n| **x** |  | c | d |\n| y<br>z | w | e | f |\n| 1 | 2 | 3 | 4 |\n| 5 | 6 | 7 | 8 |\n";
        var (w, doc) = Open(source, wrap: true);
        CaretIn(w, doc, 3, "**x**");
        View(w).GridSelection.Select(new(3, 0, 0), new(4, 1, 0));
        Press(w, Key.C, RawInputModifiers.Control, PhysicalKey.C);
        CaretIn(w, doc, 6, "7");
        View(w).GridSelection.Select(new(lastLine, lastColumn, 0), new(5, 1, 0));
        Press(w, Key.V, RawInputModifiers.Control, PhysicalKey.V);
        Settle();
        Assert.Equal(source.Replace("| 1 | 2 | 3 | 4 |\n| 5 | 6 | 7 | 8 |", "| 1 | **x** |  | 4 |\n| 5 | y<br>z | w | 8 |"), doc.Document.Text);
        Run(w, "edit.undo");
        Assert.Equal(source, doc.Document.Text);
        Assert.Equal(lastColumn, View(w).GridSelection.CellBlock!.Value.LastCell);
        Assert.Equal(lastLine, View(w).GridSelection.CellBlock!.Value.Lines.Last());
    }

    [AvaloniaFact]
    public void PasteBlockPastBothEdges_ExtendsOnlyAsFarAsNeeded()
    {
        const string source = "a | b\r\n:--- | ---:\r\n1 | 2\r\n\r\nAfter";
        var (w, doc) = Open(source, wrap: true);
        CaretIn(w, doc, 3, "2");
        Await(w.Clipboard!.SetTextAsync("| x | y | z |\n|  | q | r |\n| u | v | w |").ContinueWith(_ => 0));
        Press(w, Key.V, RawInputModifiers.Control, PhysicalKey.V);
        Settle();
        Assert.Equal("a | b|  |  |\r\n:--- | ---:| --- | --- |\r\n1 | x | y | z |\r\n|  |  | q | r |\r\n|  | u | v | w |\r\n\r\nAfter", doc.Document.Text);
        Assert.Equal(4, View(w).Model.Tables[0].ColumnCount);
        Assert.Equal(5, View(w).Model.Tables[0].Rows.Count);
        doc.Document.UndoStack.Undo();
        Assert.Equal(source, doc.Document.Text);
    }

    [AvaloniaFact]
    public void CopyingAColumn_GivesAnHtmlTableForWord()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| 1 | **dwa** |\n", wrap: true);
        CaretIn(w, doc, 3, "dwa");
        Run(w, "md.table.selCol");
        Run(w, "edit.copy");
        Settle();
        var clipboard = w.Clipboard!;
        Assert.Equal("| b |\n|---|\n| **dwa** |\n", Await(clipboard.TryGetTextAsync())!.Replace("\r\n", "\n"));
        if (OperatingSystem.IsWindows())
        {
            var bytes = Await(clipboard.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat("HTML Format")));
            if (bytes is not null) // the headless clipboard may keep text only
            {
                string html = EelDitor.Markdown.TableClipboard.HtmlFromClipboard(bytes);
                Assert.Contains("<th style=\"padding:2px 6px\">b</th>", html);
                Assert.Contains("<strong>dwa</strong>", html);
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitTable_AboveTheCurrentRow(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|:--|--:|\n| 1 | 2 |\n| 3 | 4 |\n| 5 | 6 |\n", wrap);
        CaretIn(w, doc, 4, "3");
        Run(w, "md.table.split");
        Assert.Equal("| a | b |\n|:--|--:|\n| 1 | 2 |\n\n| a | b |\n|:--|--:|\n| 3 | 4 |\n| 5 | 6 |\n", doc.Document.Text);
        Assert.Equal(2, View(w).Model.Tables.Count);
        Assert.Equal(7, w.Editor.TextArea.Caret.Line);       // still on "3 | 4"
        doc.Document.UndoStack.Undo();
        Assert.Equal("| a | b |\n|:--|--:|\n| 1 | 2 |\n| 3 | 4 |\n| 5 | 6 |\n", doc.Document.Text);
        CaretIn(w, doc, 1, "a");                              // on the header: nothing to split
        Assert.False(w.CommandCanExecute("md.table.split"));
    }

    private static void CellKey(MainWindow w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.CellEditor!.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        Settle();
    }

    [AvaloniaFact]
    public void InAGrid_TheRowFollowsTheTyping_AndTheEditIsOneUndoStep()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| jeden | dwa |\n", wrap: true);
        w.EditTableCell(3, 1);
        var editor = w.CellEditor!;
        editor.TextArea.ClearSelection();
        editor.CaretOffset = 3;
        editor.Document.Insert(3, " i pół");
        Settle();
        Assert.Equal("| jeden | dwa i pół |", Line(doc, 3));          // written through while typing
        editor.Document.Insert(editor.Document.TextLength, " razy");
        Settle();
        Assert.Equal("| jeden | dwa i pół razy |", Line(doc, 3));
        CellKey(w, Key.Enter, KeyModifiers.Control);
        Assert.Null(w.CellEditor);
        doc.Document.UndoStack.Undo();                                  // the whole edit at once
        Assert.Equal("| jeden | dwa |", Line(doc, 3));
    }

    [AvaloniaFact]
    public void InAGrid_TypingBackToTheOriginal_KeepsTheEditedCellCurrent()
    {
        var (w, doc) = Open("| a | b | c |\n|---|---|---|\n| komnata (7; room) | dwa | trzy |\n", wrap: true);
        w.EditTableCell(3, 0);
        var editor = w.CellEditor!;
        editor.Document.Insert(5, "x");                       // type a character …
        Settle();
        Assert.Equal((3, 0), View(w).CurrentCell);
        editor.Document.Remove(5, 1);                         // … and Backspace it: the text is the original again
        Settle();
        Assert.Equal("| komnata (7; room) | dwa | trzy |", Line(doc, 3));
        Assert.Equal((3, 0), View(w).CurrentCell);            // not the last cell of the row
        editor.Document.Insert(0, "a");
        Settle();
        Assert.Equal((3, 0), View(w).CurrentCell);
        CloseEditor(w);
    }

    [AvaloniaFact]
    public void InAGrid_EscKeepsTheEdit_AndCtrlZPutsTheRowBack()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| jeden | dwa<br/>trzy |\n", wrap: true);
        w.EditTableCell(3, 1);
        var editor = w.CellEditor!;
        editor.Document.Insert(0, "zero ");
        Settle();
        Assert.Equal("| jeden | zero dwa<br>trzy |", Line(doc, 3));
        CellKey(w, Key.Escape);
        Assert.Null(w.CellEditor);
        Assert.Equal("| jeden | zero dwa<br>trzy |", Line(doc, 3));
        doc.Document.UndoStack.Undo();                                  // the whole edit at once
        Assert.Equal("| jeden | dwa<br/>trzy |", Line(doc, 3));       // exactly as it was, <br/> too
        Assert.False(doc.Document.UndoStack.CanUndo);
    }

    [AvaloniaFact]
    public void InAGrid_AChangeThatWouldBreakTheRow_IsNotWritten()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| jeden | `dwa` |\n", wrap: true);
        w.EditTableCell(3, 1);
        var editor = w.CellEditor!;
        editor.Document.Text = "a | b";           // a bare pipe is escaped, so this one is fine
        Settle();
        Assert.Equal("| jeden | a \\| b |", Line(doc, 3));
        CloseEditor(w);
    }

    [AvaloniaFact]
    public void TabInTheCellEditor_MovesTheCurrentCell()
    {
        var (w, _) = Open("| a | b | c |\n|---|---|---|\n| jeden | dwa | trzy |\n", wrap: true);
        w.EditTableCell(3, 0);
        Assert.Equal((3, 0), View(w).CurrentCell);
        w.CellEditor!.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
        var until = DateTime.UtcNow.AddSeconds(5);
        while (w.CellEditor?.Text != "dwa" && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.Equal("dwa", w.CellEditor!.Text);
        Assert.Equal((3, 1), View(w).CurrentCell);
        CloseEditor(w);
    }
}
