using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyARow_AndPasteItAboveAnotherRow(bool wrap)
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| **raz** | dwa |\n| trzy | cztery |\n| pięć | sześć |\n", wrap);
        CaretIn(w, doc, 3, "raz");
        Run(w, "md.table.selRow");
        Run(w, "edit.copy");
        Settle();
        var clipboard = w.Clipboard!;
        Assert.Equal("| **raz** | dwa |" + Environment.NewLine.Replace("\r\n", "\n"), Await(clipboard.TryGetTextAsync())!.Replace("\r\n", "\n"));

        CaretIn(w, doc, 5, "pięć");                       // the current row: pasted above it
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("| a | b |\n|---|---|\n| **raz** | dwa |\n| trzy | cztery |\n| **raz** | dwa |\n| pięć | sześć |\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();                    // one step
        Assert.Equal("| a | b |\n|---|---|\n| **raz** | dwa |\n| trzy | cztery |\n| pięć | sześć |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void CutRows_ThenPasteThem_AtTheHeader_GoesBelowTheSeparator()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", wrap: true);
        View(w).GridSelection.Select(new(4, 0, 0), new(4, 1, 1));   // the whole last row
        Run(w, "edit.cut");
        Settle();
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |\n", doc.Document.Text);
        CaretIn(w, doc, 1, "a");
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("| a | b |\n|---|---|\n| 3 | 4 |\n| 1 | 2 |\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void PastingTabSeparatedRows_IntoATable()
    {
        var (w, doc) = Open("| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n", wrap: false);
        Await(w.Clipboard!.SetTextAsync("x\ty\r\nz\tq|r\r\n").ContinueWith(_ => 0));
        CaretIn(w, doc, 3, "2");
        Run(w, "edit.paste");
        Settle();
        Assert.Equal("| a | b | c |\n|---|---|---|\n| x | y |  |\n| z | q\\|r |  |\n| 1 | 2 | 3 |\n", doc.Document.Text);
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
        CellKey(w, Key.Enter);
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
    public void InAGrid_EscPutsTheRowBack()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| jeden | dwa<br/>trzy |\n", wrap: true);
        w.EditTableCell(3, 1);
        var editor = w.CellEditor!;
        editor.Document.Insert(0, "zero ");
        Settle();
        Assert.Equal("| jeden | zero dwa<br>trzy |", Line(doc, 3));
        CellKey(w, Key.Escape);
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
