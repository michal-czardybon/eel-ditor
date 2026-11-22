using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>
/// Moving between grid cells with the arrows and Enter to edit; spaces typed in a cell (as text and in the cell
/// editor); images in tables shown the same way in every view; word wrap on by default.
/// </summary>
public class GridNavigationTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static (MainWindow W, EditorDocument Doc) Open(string source, bool wrap, Action<string>? prepare = null)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        prepare?.Invoke(dir);
        string path = Path.Combine(dir, "n.md");
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static void Key(MainWindow w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        Dispatcher.UIThread.RunJobs();
    }

    private static void CloseEditor(MainWindow w)
    {
        if (w.CellEditor is { } editor)
            editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
        Dispatcher.UIThread.RunJobs();
    }

    private static void CaretAt(MainWindow w, int line, string text)
    {
        var l = w.Editor.Document.GetLineByNumber(line);
        w.Editor.TextArea.ClearSelection();
        w.Editor.CaretOffset = l.Offset + w.Editor.Document.GetText(l).IndexOf(text, StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();
    }

    private const string Table = "before\n\n| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n| 4 | 5 |\n\nafter\n";

    [AvaloniaFact]
    public void InAGrid_TheArrowsGoFromCellToCell()
    {
        var (w, _) = Open(Table, wrap: true);
        var view = w.MarkdownViewForTests!;
        CaretAt(w, 5, "1");
        Assert.Equal((5, 0), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Right);
        Assert.Equal((5, 1), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Right);
        Assert.Equal((5, 2), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Right);                     // the row's edge: the next row's first cell
        Assert.Equal((6, 0), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Left);                      // and back: the previous row's last cell
        Assert.Equal((5, 2), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Down);                      // the row below has only two cells
        Assert.Equal((6, 1), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Left);
        Assert.Equal((6, 0), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Up);
        Key(w, Avalonia.Input.Key.Up);                        // over the separator to the header
        Assert.Equal((3, 0), view.CurrentCell);
        Key(w, Avalonia.Input.Key.Up);                        // out of the table: the line before it
        Assert.Null(view.CurrentCell);
        Assert.Equal(2, w.Editor.TextArea.Caret.Line);
        CaretAt(w, 6, "5");
        Key(w, Avalonia.Input.Key.Down);
        Assert.Equal(7, w.Editor.TextArea.Caret.Line);
    }

    [AvaloniaFact]
    public void InAGrid_EnterEditsTheCurrentCell()
    {
        var (w, doc) = Open(Table, wrap: true);
        CaretAt(w, 5, "1");
        Key(w, Avalonia.Input.Key.Right);
        Key(w, Avalonia.Input.Key.Enter);
        Assert.NotNull(w.CellEditor);
        Assert.Equal("2", w.CellEditor!.Text);
        Assert.Equal("| 1 | 2 | 3 |", doc.Document.GetText(doc.Document.GetLineByNumber(5)));  // no line break typed
        CloseEditor(w);
    }

    [AvaloniaFact]
    public void AsText_ASpaceTypedAfterACellsText_ShowsRightAfterIt()
    {
        // The column is wider than the cell, so the cell is padded; the space must not land after the padding.
        var (w, doc) = Open("| Column 1 | b |\n| -------- | - |\n|  | x |\n", wrap: false);
        var l = doc.Document.GetLineByNumber(3);
        w.Editor.CaretOffset = l.Offset + 2;
        w.Editor.TextArea.PerformTextInput("A");
        w.Editor.TextArea.PerformTextInput(" ");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("| A  | x |", doc.Document.GetText(l));
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        double X(int column) => tv.GetVisualPosition(new TextViewPosition(3, column + 1), VisualYPosition.LineTop).X;
        Assert.Equal(X(3) + tv.WideSpaceWidth, X(4), 1);    // one space after "A", not past the padding
    }

    [AvaloniaFact]
    public void InTheCellEditor_ASpaceAfterAFullCell_StaysOnTheLine()
    {
        var (w, _) = Open("| Column 1 | b |\n|---|---|\n|  | x |\n", wrap: true);
        w.EditTableCell(3, 0);
        Dispatcher.UIThread.RunJobs();
        var editor = w.CellEditor!;
        foreach (char c in "abcdefghij ")
        {
            editor.TextArea.PerformTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
        var tv = editor.TextArea.TextView;
        tv.EnsureVisualLines();
        Assert.Equal(1, tv.VisualLines.Sum(v => v.TextLines.Count));       // the space did not wrap
        CloseEditor(w);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnImageInATable_IsShownAsItsText_InEveryView(bool wrap)
    {
        var (w, _) = Open("![dot](k.png)\n\n| a | b |\n|---|---|\n| ![dot](k.png) | x |\n", wrap,
            dir => File.WriteAllBytes(Path.Combine(dir, "k.png"), Convert.FromBase64String(OnePixelPng)));
        var tv = w.Editor.TextArea.TextView;
        Assert.Single(tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(1)).Elements.OfType<InlineObjectElement>()); // outside: a preview
        Assert.Empty(tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(5)).Elements.OfType<InlineObjectElement>());
        if (!wrap) return;
        w.EditTableCell(5, 0);
        Dispatcher.UIThread.RunJobs();
        var editorView = w.CellEditor!.TextArea.TextView;
        editorView.EnsureVisualLines();
        Assert.Empty(editorView.VisualLines.SelectMany(v => v.Elements).OfType<InlineObjectElement>());
        CloseEditor(w);
    }

    [AvaloniaFact]
    public void WordWrap_IsOnByDefault()
    {
        SettingsStore.UseDirectory(TestDirs.New());
        Assert.True(SettingsStore.Load().WordWrap);
        Assert.True(new AppSettings().WordWrap);
    }

    [AvaloniaFact]
    public void DoubleClickEditing_PutsTheCaretWhereClicked_WithoutSelecting()
    {
        var (w, _) = Open("| a | b |\n|---|---|\n| **ab**cd | x |\n", wrap: true);
        w.EditTableCell(3, 0, shownOffset: 3);                  // shown "abcd": before the "d"
        Dispatcher.UIThread.RunJobs();
        var editor = w.CellEditor!;
        Assert.Equal(0, editor.SelectionLength);
        Assert.Equal("**ab**cd".IndexOf('d'), editor.CaretOffset);
        CloseEditor(w);
    }
}
