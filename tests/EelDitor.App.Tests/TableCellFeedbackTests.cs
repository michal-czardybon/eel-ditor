using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>
/// Reported problems with tables and around them: links in cells, Ctrl+click, the cell editor's keys, styles on part
/// of a cell, highlight over a line break, bold symbols, the document map, the open-files list, special characters
/// and text pasted from Word.
/// </summary>
public class TableCellFeedbackTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string source, bool wrap = true, string name = "t.md")
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static void Settle()
    {
        for (int i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    private static string Line(EditorDocument doc, int n) => doc.Document.GetText(doc.Document.GetLineByNumber(n));

    private static MarkdownView View(MainWindow w) => w.MarkdownViewForTests!;

    private static void CellKey(MainWindow w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.CellEditor!.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        Settle();
    }

    /// <summary>The middle of character <paramref name="index"/> of a grid cell's shown text, in window coordinates.</summary>
    private static Point GridPoint(MainWindow w, int line, int cell, int index)
    {
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var visual = tv.GetVisualLine(line)!;
        var el = Assert.IsType<TableRowElement>(Assert.Single(visual.Elements));
        var (column, drawn, _) = el.Spans(cell, index, index + 1).First();
        double x0 = visual.GetVisualPosition(0, VisualYPosition.LineTop).X;
        var p = new Point(x0 + (column + 0.5) * tv.WideSpaceWidth, visual.VisualTop + (drawn + 0.5) * tv.DefaultLineHeight) - tv.ScrollOffset;
        return tv.TranslatePoint(p, w)!.Value;
    }

    [AvaloniaFact]
    public void EditCellCommand_OpensTheSelectedGridColumn()
    {
        var (w, _) = Open("| A | B |\n|---|---|\n| left | right |\n");
        var point = GridPoint(w, 3, 1, 0);
        w.MouseDown(point, MouseButton.Left);
        w.MouseUp(point, MouseButton.Left);
        w.RunCommand("md.table.editCell");
        Assert.Equal("right", w.CellEditor!.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void SingleClickSelectsTheGridCell_AndTypingRequiresAnOpenCellEditor()
    {
        var (w, doc) = Open("| A | B |\n|---|---|\n| left | right |\n");
        var point = GridPoint(w, 3, 1, 0);
        w.MouseDown(point, MouseButton.Left);
        w.MouseUp(point, MouseButton.Left);
        Assert.Equal((3, 1), View(w).CurrentCell);
        Assert.Null(w.CellEditor);
        w.Editor.TextArea.PerformTextInput("x");
        Assert.Equal("| left | right |", Line(doc, 3));
        w.Close();
    }

    [AvaloniaFact]
    public void TrailingSpaces_KeepTheCaretInsideTheGrid_AndAreKeptWhenTypingContinues()
    {
        var (w, doc) = Open("| A | B |\n|---|---|\n| left | hello |\n");
        w.EditTableCell(3, 1, 5);
        Settle();
        var editor = w.CellEditor!;
        var grid = w.Editor.TextArea.TextView;
        var tv = editor.TextArea.TextView;
        double CaretX()
        {
            tv.EnsureVisualLines();
            var caret = tv.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineTop);
            return caret.X - tv.HorizontalOffset + 2 * grid.WideSpaceWidth;
        }
        double before = CaretX();
        editor.TextArea.PerformTextInput(" ");
        Settle();
        w.UpdateLayout();
        double afterOneSpace = CaretX();
        Assert.True(afterOneSpace > before + grid.WideSpaceWidth / 2,
            $"Caret moved from {before} to {afterOneSpace}, character width {grid.WideSpaceWidth}, offset {editor.CaretOffset}.");

        string spaces = new(' ', 20);
        editor.TextArea.PerformTextInput(spaces[1..]);
        Settle();
        w.UpdateLayout();

        Assert.Equal("hello" + spaces, editor.Text);
        Assert.Equal("| left | hello |", Line(doc, 3));
        var cell = View(w).CellRect(3, 1)!.Value;
        Assert.InRange(CaretX(), before + grid.WideSpaceWidth / 2, cell.Width);

        CellKey(w, Key.Left);
        Assert.Equal(24, editor.CaretOffset);
        CellKey(w, Key.Right);
        Assert.Equal(25, editor.CaretOffset);
        editor.TextArea.PerformTextInput("world");
        Settle();
        Assert.Equal("| left | hello" + spaces + "world |", Line(doc, 3));
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(Key.Down, false, false)]
    [InlineData(Key.Right, false, false)]
    [InlineData(Key.Down, true, false)]
    [InlineData(Key.Right, true, false)]
    [InlineData(Key.Right, false, true)]
    [InlineData(Key.Down, true, true)]
    public void ArrowsAtEndOfGrid_MoveToAnEditableLineBelowTheTable(Key key, bool editing, bool hasLineBelow)
    {
        const string table = "| A | B |\r\n|---|---|\r\n| left | right |";
        string source = hasLineBelow ? table + "\r\n" : table;
        var (w, doc) = Open(source);
        if (editing)
        {
            w.EditTableCell(3, 1, 5);
            CellKey(w, key);
        }
        else
        {
            w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).EndOffset;
            w.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            Settle();
        }
        Assert.Equal(table + "\r\n", doc.Document.Text);
        Assert.Equal(4, w.Editor.TextArea.Caret.Line);
        Assert.Null(w.CellEditor);
        w.Editor.TextArea.PerformTextInput("Tekst za tabelą");
        Assert.Equal(table + "\r\nTekst za tabelą", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        if (!hasLineBelow) doc.Document.UndoStack.Undo();
        Assert.Equal(source, doc.Document.Text);
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData("md.table.delTable", "| A | B |\n|---|---|\n| left | right |")]
    [InlineData("md.table.delCol", "| A |\n|---|\n| left |")]
    public void DeletingATable_PreservesSurroundingTextAndCanBeUndone(string command, string table)
    {
        string source = "Przed\n\n" + table + "\n\nZa";
        var (w, doc) = Open(source);
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(5).Offset + 2;
        w.RunCommand(command);
        Assert.Equal("Przed\n\n\nZa", doc.Document.Text);
        Assert.Equal(3, w.Editor.TextArea.Caret.Line);
        doc.Document.UndoStack.Undo();
        Assert.Equal(source, doc.Document.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void EnterAtEndOfLastCell_ExtendsTheGridRowBeforeMoreTextIsTyped()
    {
        var (w, doc) = Open("| A | B |\n|---|---|\n| x | hello |\n\nBelow the table\n");
        w.EditTableCell(3, 1, 5);
        Settle();
        var grid = w.Editor.TextArea.TextView;
        grid.EnsureVisualLines();
        double initialHeight = grid.GetVisualLine(3)!.Height;

        for (int i = 0; i < 3; i++) CellKey(w, Key.Enter);
        w.UpdateLayout();
        grid.EnsureVisualLines();

        Assert.Equal("| x | hello<br><br><br> |", Line(doc, 3));
        Assert.Equal(4, w.CellEditor!.TextArea.Caret.Line);
        Assert.Equal(initialHeight + 3 * grid.DefaultLineHeight, grid.GetVisualLine(3)!.Height, 2);

        CellKey(w, Key.Enter, KeyModifiers.Control);
        w.EditTableCell(3, 1);
        Assert.Equal(4, w.CellEditor!.Document.LineCount);
    }

    [AvaloniaFact]
    public void HighlightOverABr_StaysHiddenInTheCellEditor_AndIsWrittenBackAsOneSpan()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| x | ==jeden<br>dwa== |\n");
        w.EditTableCell(3, 1);
        Settle();
        var editor = w.CellEditor!;
        // Closed and opened again around the line break, so no marker shows on either line.
        Assert.Equal("==jeden==\n==dwa==", editor.Text);
        var view = w.CellViewForTests!;
        Assert.True(view.RenderFor(editor.Document.GetLineByNumber(1)).IsHidden(0));
        Assert.True(view.RenderFor(editor.Document.GetLineByNumber(2)).IsHidden(0));
        editor.TextArea.ClearSelection();
        editor.Document.Insert(editor.Document.TextLength - 2, " trzy");
        Settle();
        CellKey(w, Key.Enter, KeyModifiers.Control);
        Assert.Equal("| x | ==jeden<br>dwa trzy== |", Line(doc, 3));
    }

    [AvaloniaFact]
    public void CtrlClickOnALinkInAGridCell_OpensIt_AndCtrlClickElsewhereSelectsNoRows()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| raz | (zob. [notatka](brak.md)) |\n| dwa | trzy |\n");
        var opened = new List<string>();
        View(w).LinkActivated += opened.Add;
        var onLink = GridPoint(w, 3, 1, 7);                        // the "o" of "notatka" in "(zob. notatka)"
        w.MouseDown(onLink, MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(onLink, MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(["brak.md"], opened);
        var plain = GridPoint(w, 3, 1, 8);
        Thread.Sleep(600);                                         // not a double click
        w.MouseDown(plain, MouseButton.Left);                      // a plain click only places the caret
        w.MouseUp(plain, MouseButton.Left);
        Assert.Single(opened);

        // Ctrl+click off a link: the clicked cell becomes the current one, nothing else (it used to select whole rows).
        var elsewhere = GridPoint(w, 4, 1, 1);
        Thread.Sleep(600);
        w.MouseDown(elsewhere, MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(elsewhere, MouseButton.Left, RawInputModifiers.Control);
        Settle();
        Assert.Equal(0, w.Editor.SelectionLength);
        Assert.Equal((4, 1), View(w).CurrentCell);
        Assert.Single(opened);
    }

    [AvaloniaFact]
    public void HighlightFromTheMenu_InTheCellEditor_StylesOnlyTheSelectedText()
    {
        var (w, doc) = Open("| a | b |\n|---|---|\n| x | ala ma kota |\n");
        w.EditTableCell(3, 1);
        var editor = w.CellEditor!;
        editor.Select(4, 2);                                       // "ma"
        w.RunCommand("md.highlight");
        Settle();
        Assert.NotNull(w.CellEditor);                              // the edit goes on
        Assert.Equal("ala ==ma== kota", editor.Text);
        CellKey(w, Key.Enter, KeyModifiers.Control);
        Assert.Equal("| x | ala ==ma== kota |", Line(doc, 3));
    }

    [AvaloniaFact]
    public void ArrowsAtTheEdgeOfTheText_MoveTheEditToTheNeighbouringCell()
    {
        var (w, _) = Open("| a | b |\n|---|---|\n| jeden | dwa |\n| trzy | cztery |\n");
        w.EditTableCell(3, 0);
        var editor = w.CellEditor!;
        editor.TextArea.ClearSelection();
        editor.CaretOffset = 2;
        CellKey(w, Key.Right);                                     // inside the text: an ordinary caret move
        Assert.Same(editor, w.CellEditor);
        editor.CaretOffset = editor.Document.TextLength;
        CellKey(w, Key.Right);                                     // after the last character: the next cell
        Assert.Equal("dwa", w.CellEditor!.Text);
        Assert.Equal(0, w.CellEditor.CaretOffset);
        CellKey(w, Key.Left);                                      // before the first: back, at its end
        Assert.Equal("jeden", w.CellEditor!.Text);
        Assert.Equal(5, w.CellEditor.CaretOffset);
        CellKey(w, Key.Down);                                      // on the last line: the cell below
        Assert.Equal("trzy", w.CellEditor!.Text);
        CellKey(w, Key.Up);                                        // on the first line: the cell above
        Assert.Equal("jeden", w.CellEditor!.Text);
        CellKey(w, Key.Up);                                        // the header
        Assert.Equal("a", w.CellEditor!.Text);
        CellKey(w, Key.Up);                                        // nothing above: stays
        Assert.Equal("a", w.CellEditor!.Text);
        CellKey(w, Key.Escape);
        Assert.Null(w.CellEditor);
    }

    [AvaloniaFact]
    public void SymbolsInBoldText_StayAtNormalWeight()
    {
        var (w, _) = Open("**Legenda ✔ ok** zwykły ◍\n", wrap: false);
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var visual = tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(1));
        FontWeight WeightAt(int column) =>
            visual.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength).TextRunProperties.Typeface.Weight;
        string text = w.Editor.Document.Text;
        Assert.Equal(FontWeight.Bold, WeightAt(text.IndexOf("Legenda", StringComparison.Ordinal)));
        Assert.Equal(FontWeight.Normal, WeightAt(text.IndexOf('✔')));     // drawn bold, it turned white for good
        Assert.Equal(FontWeight.Bold, WeightAt(text.IndexOf("ok", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public void DocumentMap_DraggingToTheBottom_ReachesTheEnd_AndTheFrameFollows()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++)
            sb.Append($"Akapit {i}.\n\n| a | b |\n|---|---|\n| {new string('x', 30)} | {string.Join(" ", Enumerable.Repeat("długi tekst komórki", 12))} |\n\n");
        var (w, _) = Open(sb.ToString());
        for (int i = 0; i < 100; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Thread.Sleep(3); }
        w.SideTabs.SelectedItem = w.MapTab;
        Settle();
        w.UpdateLayout();
        var map = w.GetVisualDescendants().OfType<DocumentMap>().Single();
        var tv = w.Editor.TextArea.TextView;
        var top = map.TranslatePoint(new Point(20, 2), w)!.Value;
        var bottom = map.TranslatePoint(new Point(20, map.Bounds.Height - 1), w)!.Value;
        w.MouseDown(top, MouseButton.Left);
        for (int k = 1; k <= 10; k++) { w.MouseMove(top + (bottom - top) * k / 10.0); Settle(); }
        w.MouseMove(bottom + new Point(0, 50));                    // past the map's end
        Settle();
        w.MouseUp(bottom, MouseButton.Left);
        Settle();
        Assert.Equal(tv.DocumentHeight - tv.Bounds.Height, tv.VerticalOffset, 2.0);
        // The frame (the part in view) is at the end of the map's document.
        var mtv = map.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single().TextArea.TextView;
        var (frameTop, frameHeight) = map.Frame();
        Assert.Equal(mtv.DocumentHeight, frameTop + frameHeight, 2.0);
    }

    [AvaloniaFact]
    public void OpenFiles_FlatListByName_OrByModificationTime()
    {
        static EditorDocument Doc(string path, int day) =>
            new(new AvaloniaEdit.Document.TextDocument(""), path) { LastWriteTimeUtc = new DateTime(2026, 1, day) };
        string root = Path.Combine(Path.GetTempPath(), "flat-test");
        var b = Doc(Path.Combine(root, "x", "b.md"), 3);
        var a = Doc(Path.Combine(root, "y", "a.md"), 1);
        var c = Doc(Path.Combine(root, "c.md"), 2);
        var model = new OpenFilesModel();
        foreach (var d in new[] { b, a, c }) model.Add(d);
        Assert.All(model.TopItems, i => Assert.IsType<FileGroup>(i));          // by folder, as before

        model.Mode = FileListMode.Name;
        Assert.Equal(new object[] { a, b, c }, model.TopItems);
        Assert.Empty(model.Groups);
        Assert.Equal([a, b, c], model.VisualOrder());

        model.Mode = FileListMode.Modified;
        Assert.Equal(new object[] { b, c, a }, model.TopItems);                // newest first
        model.Filter = "b.md";
        Assert.Equal(new object[] { b }, model.TopItems);
    }

    [AvaloniaFact]
    public void OpenFiles_ModeButtons_AreRemembered()
    {
        var (w, _) = Open("x\n", name: "a.txt");
        w.SetFileListMode(FileListMode.Modified);
        var buttons = w.FileListModes.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>().ToList();
        Assert.Equal(3, buttons.Count);
        Assert.Equal([false, false, true], buttons.Select(x => x.IsChecked == true));
        w.Close();
        Assert.Equal("Modified", SettingsStore.Load().FileListMode);
    }

    [AvaloniaFact]
    public void SpecialCharacters_ByCategory_SearchAndInsert()
    {
        var inserted = new List<string>();
        var recent = new List<string>();
        var window = new SpecialCharacters(inserted.Add, recent, new FontFamily("Consolas"));
        window.Show();
        int arrows = Array.FindIndex(SpecialCharacters.Categories, c => c.Key == "CharMap.Arrows") + 1;
        window.ShowCategory(arrows);
        Assert.Contains("→", window.Shown);
        Assert.DoesNotContain("а", window.Shown);                                // no Cyrillic among the arrows
        int cyrillic = Array.FindIndex(SpecialCharacters.Categories, c => c.Key == "CharMap.Cyrillic") + 1;
        window.ShowCategory(cyrillic);
        Assert.Contains("Ж", window.Shown);
        window.ShowSearch("U+2713");
        Assert.Equal(["✓"], window.Shown);
        window.Insert("✓");
        window.Insert("→");
        Assert.Equal(["✓", "→"], inserted);
        Assert.Equal(["→", "✓"], recent);                                        // newest first
        window.ShowCategory(0);
        Assert.Equal(["→", "✓"], window.Shown);
        Assert.All(SpecialCharacters.Categories, c => Assert.NotEmpty(c.Characters()));
        window.Close();
    }

    [AvaloniaFact]
    public void SpecialCharacters_InsertAtTheCaret()
    {
        var (w, doc) = Open("ab\n", name: "a.txt");
        w.Editor.CaretOffset = 1;
        w.InsertCharacter("→");
        Assert.Equal("a→b\n", doc.Document.Text);
        Assert.True(w.CommandCanExecute("edit.specialChar"));
    }

    [AvaloniaFact]
    public async Task PastingFromWord_AtTheStartOfALine_PutsTheBlocksOnLinesOfTheirOwn()
    {
        // A table pasted before "# Test" joined that heading into its last row ("| a | b |# Test").
        var (w, doc) = Open("# Test\n", wrap: false);
        w.Editor.CaretOffset = 0;
        const string html = "<html xmlns:w=\"urn:schemas-microsoft-com:office:word\"><body><p class=MsoNormal>Tekst</p>"
            + "<table><tr><td>a</td><td>b</td></tr></table></body></html>";
        await w.PasteContentAsync(html, "Tekst\r\na\tb");
        Settle();
        Assert.Equal("Tekst\n\n|  |  |\n|---|---|\n| a | b |\n\n# Test\n", doc.Document.Text);
        // A single line goes into the line, as typed.
        w.Editor.CaretOffset = doc.Document.Text.IndexOf("Test", StringComparison.Ordinal);
        await w.PasteContentAsync("<html xmlns:w=\"urn:schemas-microsoft-com:office:word\"><body><p class=MsoNormal><b>Duży</b> </p></body></html>", "Duży");
        Settle();
        Assert.EndsWith("# **Duży**Test\n", doc.Document.Text);
    }

    /// <summary>Makes the whole line bold, as a TextMate theme does with **…** in raw mode.</summary>
    private sealed class AllBold : AvaloniaEdit.Rendering.DocumentColorizingTransformer
    {
        protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line) =>
            ChangeLinePart(line.Offset, line.EndOffset, el =>
            {
                var tf = el.TextRunProperties.Typeface;
                el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeight.Bold, tf.Stretch));
            });
    }

    [AvaloniaFact]
    public void SymbolsMadeBoldByAnyColorizer_StayAtNormalWeight()
    {
        // Raw mode: the TextMate theme makes **…** bold; a bold emoji was drawn white, and so were all later ones.
        var (w, _) = Open("Legenda ✔ ok\n", wrap: false, name: "a.txt");
        var tv = w.Editor.TextArea.TextView;
        tv.LineTransformers.Insert(0, new AllBold());   // before the editor's own SymbolWeight, as TextMate is
        tv.Redraw();
        tv.EnsureVisualLines();
        var visual = tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(1));
        FontWeight WeightAt(int at) =>
            visual.Elements.First(e => e.RelativeTextOffset <= at && at < e.RelativeTextOffset + e.DocumentLength).TextRunProperties.Typeface.Weight;
        string text = w.Editor.Document.Text;
        Assert.Equal(FontWeight.Bold, WeightAt(0));
        Assert.Equal(FontWeight.Normal, WeightAt(text.IndexOf('✔')));
    }

    [AvaloniaFact]
    public void AClosedDocument_IsNotKeptInMemory_ByTheFocusedRowOfTheFileList()
    {
        // The sidebar remembered its last focused element, the closed file's row, and through it the whole document.
        var (w, text) = OpenFocusAndClose();
        for (int i = 0; i < 5; i++) { Settle(); GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(text.IsAlive);
        GC.KeepAlive(w);
    }

    /// <summary>Opens a file, focuses its row in the file list, closes it; only a weak reference to its text is kept.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (MainWindow W, WeakReference Text) OpenFocusAndClose()
    {
        var (w, doc) = Open("a\n", wrap: false, name: "a.txt");
        w.NewDocument();
        Settle();
        var text = new WeakReference(doc.Document);
        FocusRow(w, doc);
        w.RunCommand("file.new");
        w.Editor.TextArea.Focus();                        // the focus goes to the editor, as in the app
        Settle();
        CloseDocument(w, doc);
        return (w, text);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FocusRow(MainWindow w, EditorDocument doc)
    {
        w.FilesTree.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, doc)).Focus();
        Settle();
    }

    private static void CloseDocument(MainWindow w, EditorDocument doc)
    {
        var close = w.FilesTree.GetVisualDescendants().OfType<Button>()
            .Single(x => x.Classes.Contains("rowClose") && ReferenceEquals(x.DataContext, doc));
        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();
    }

    [AvaloniaFact]
    public void WhileComparing_TheMapFollowsTheComparison_AndMarksTheChangedLines()
    {
        var (w, doc) = Open("jeden\ndwa\ntrzy\ncztery\n", wrap: false, name: "a.txt");
        w.SideTabs.SelectedItem = w.MapTab;
        Settle();
        var map = w.GetVisualDescendants().OfType<DocumentMap>().Single();
        w.OpenDiff("HEAD", "jeden\nDWA\ntrzy\n", "working", doc.Document, true, null);
        Settle();
        var view = w.CurrentDiffView!;
        Assert.Equal([(1, 1, Core.Diff.DiffKind.Modified), (3, 1, Core.Diff.DiffKind.Inserted)], map.ChangedLines!().ToList());

        w.RunCommand("diff.words");                                    // the word view: its own text and lines
        Settle();
        var mapEditor = map.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();
        Assert.Same(view.WordsEditor.Document, mapEditor.Document);
        Assert.Contains(map.ChangedLines!(), c => c.Line == 1);

        w.CloseDiff();
        Settle();
        Assert.Null(map.ChangedLines);
        Assert.Same(doc.Document, mapEditor.Document);
    }

    [AvaloniaFact]
    public void NewDocument_WithTextSelected_SwitchesCleanly()
    {
        // The status bar read the old selection's offsets while AvaloniaEdit was switching to the new, empty document;
        // the exception left the switch half done and the window stood still.
        var (w, doc) = Open(string.Concat(Enumerable.Repeat("Akapit z tekstem do zaznaczenia, dość długi.\r\n\r\n", 40)), wrap: true);
        w.Editor.Select(200, 300);
        Settle();
        w.RunCommand("file.new");
        Settle();
        w.UpdateLayout();
        var model = (OpenFilesModel)w.DataContext!;
        Assert.Equal(2, model.Documents.Count);
        Assert.NotSame(doc.Document, w.Editor.Document);
        Assert.Equal(0, w.Editor.Document.TextLength);
        Assert.True(w.Editor.TextArea.Selection.IsEmpty);
        Assert.False(File.Exists(ErrorLog.FilePath) && File.ReadAllText(ErrorLog.FilePath).Contains("ArgumentOutOfRange"),
            File.Exists(ErrorLog.FilePath) ? File.ReadAllText(ErrorLog.FilePath) : "");
    }

    [AvaloniaFact]
    public async Task PastingFromWord_KeepsListsAndStyles_AsMarkdown()
    {
        var (w, doc) = Open("Start\n", wrap: false);
        w.Editor.CaretOffset = doc.Document.TextLength;
        const string html = """
            <html xmlns:w="urn:schemas-microsoft-com:office:word"><body>
            <p class=MsoNormal>Tekst z <b>pogrubieniem</b></p>
            <p class=MsoListParagraph style='mso-list:l0 level1 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>1.<span>&nbsp; </span></span><![endif]>Raz</p>
            <p class=MsoListParagraph style='mso-list:l0 level1 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>2.<span>&nbsp; </span></span><![endif]>Dwa</p>
            </body></html>
            """;
        await w.PasteContentAsync(html, "Tekst z pogrubieniem\r\nRaz\r\nDwa");
        Settle();
        Assert.Equal("Start\nTekst z **pogrubieniem**\n\n1. Raz\n2. Dwa", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("Start\n", doc.Document.Text);
    }
}
