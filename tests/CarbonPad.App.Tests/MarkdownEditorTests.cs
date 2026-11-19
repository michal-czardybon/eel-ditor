using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CarbonPad.App.Editor;
using CarbonPad.Markdown;

namespace CarbonPad.App.Tests;

public class MarkdownEditorTests
{
    private static (TextEditor Editor, MarkdownView View, Window Window) Create(string text)
    {
        var editor = new TextEditor { Document = new TextDocument(text), FontFamily = "Consolas", FontSize = 13 };
        var window = new Window { Content = editor, Width = 800, Height = 400 };
        window.Show();
        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
        editor.TextArea.TextView.EnsureVisualLines();
        return (editor, view, window);
    }

    [AvaloniaFact]
    public void RenderingDoesNotChangeTheSource()
    {
        const string text = "| a | bbbbb |\r\n|---|---|\r\n| **c** | d |\r\n";
        var (editor, _, _) = Create(text);
        editor.TextArea.TextView.EnsureVisualLines();
        Assert.Equal(text, editor.Document.Text);
        Assert.True(editor.TextArea.TextView.VisualLinesValid);
    }

    [AvaloniaFact]
    public void TableRowsHaveEqualVisualWidth()
    {
        var (editor, _, _) = Create("| a | bbbbb |\n|---|---|\n| cccc | d |\n");
        var tv = editor.TextArea.TextView;
        tv.EnsureVisualLines();
        double W(int line) => tv.GetOrConstructVisualLine(editor.Document.GetLineByNumber(line)).TextLines[0].WidthIncludingTrailingWhitespace;
        Assert.Equal(W(1), W(2), 1);
        Assert.Equal(W(1), W(3), 1);
    }

    [AvaloniaFact]
    public void CtrlB_WrapsWordAndTogglesBack()
    {
        var (editor, view, _) = Create("hello world");
        editor.CaretOffset = 2;
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("**hello** world", editor.Document.Text);
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("hello world", editor.Document.Text);
    }

    [AvaloniaFact]
    public void CtrlB_OnSelection()
    {
        var (editor, view, _) = Create("hello world");
        editor.Select(6, 5);
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("hello **world**", editor.Document.Text);
    }

    [AvaloniaFact]
    public void RightArrow_SkipsHiddenMarkers()
    {
        var (editor, view, _) = Create("a **bc** d");
        editor.CaretOffset = 2; // before **
        Assert.True(MarkdownCommands.HandleHorizontalMove(editor, view, right: true, extend: false));
        Assert.Equal(5, editor.CaretOffset); // after 'b'
        MarkdownCommands.HandleHorizontalMove(editor, view, right: true, extend: false);
        Assert.Equal(6, editor.CaretOffset); // after 'c', still inside bold
        MarkdownCommands.HandleHorizontalMove(editor, view, right: true, extend: false);
        Assert.Equal(9, editor.CaretOffset); // after the space following the closing **
    }

    [AvaloniaFact]
    public void Backspace_AfterClosingMarker_DeletesVisibleCharacter()
    {
        var (editor, view, _) = Create("**abc** x");
        editor.CaretOffset = 7; // after closing **
        Assert.True(MarkdownCommands.HandleDelete(editor, view, backwards: true));
        Assert.Equal("**ab** x", editor.Document.Text);
    }

    [AvaloniaFact]
    public void Backspace_LastCharacterOfSpan_RemovesMarkers()
    {
        var (editor, view, _) = Create("x **a** y");
        editor.CaretOffset = 7;
        Assert.True(MarkdownCommands.HandleDelete(editor, view, backwards: true));
        Assert.Equal("x  y", editor.Document.Text);
    }

    [AvaloniaFact]
    public void TableCommands_AreSingleUndoStep()
    {
        const string text = "| a | b |\n|---|---|\n| 1 | 2 |";
        var (editor, view, _) = Create(text);
        editor.CaretOffset = editor.Document.GetLineByNumber(3).Offset + 2;
        Assert.True(MarkdownCommands.RunTableCommand(editor, view, TableCommand.InsertColumnRight));
        Assert.Equal("| a |   | b |\n|---| --- |---|\n| 1 |   | 2 |", editor.Document.Text);
        editor.Undo();
        Assert.Equal(text, editor.Document.Text);
    }

    [AvaloniaFact]
    public void TabInLastCell_AddsRow()
    {
        var (editor, view, _) = Create("| a | b |\n|---|---|\n| 1 | 2 |");
        editor.CaretOffset = editor.Document.GetLineByNumber(3).Offset + 7;
        Assert.True(MarkdownCommands.HandleTab(editor, view, backwards: false));
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |\n|   |   |", editor.Document.Text);
        Assert.Equal(4, editor.TextArea.Caret.Line);
    }

    [AvaloniaFact]
    public void EditingInsideTable_KeepsRenderingConsistent()
    {
        var (editor, view, _) = Create("| a | b |\n|---|---|\n| 1 | 2 |");
        var tv = editor.TextArea.TextView;
        for (int i = 0; i < 20; i++)
        {
            editor.Document.Insert(editor.Document.GetLineByNumber(3).Offset + 3, "x");
            tv.EnsureVisualLines();
        }
        double W(int line) => tv.GetOrConstructVisualLine(editor.Document.GetLineByNumber(line)).TextLines[0].WidthIncludingTrailingWhitespace;
        Assert.Equal(W(3), W(1), 1);
        Assert.Equal(23, view.Model.Tables[0].Rows[2].Cells[0].End - view.Model.Tables[0].Rows[2].Cells[0].Start);
    }
}

public class MarkdownLongLineTests
{
    /// <summary>Elements covering the given column after construction and colorizing.</summary>
    private static List<AvaloniaEdit.Rendering.VisualLineElement> ElementsAt(string text, int column)
    {
        var doc = new AvaloniaEdit.Document.TextDocument(text);
        var editor = new AvaloniaEdit.TextEditor { Document = doc };
        var w = new Avalonia.Controls.Window { Content = editor, Width = 1200, Height = 400 };
        w.Show();
        using var md = new CarbonPad.App.Editor.MarkdownView(editor, doc, CarbonPad.App.Editor.EditorPalette.Dark);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var vl = editor.TextArea.TextView.GetOrConstructVisualLine(doc.GetLineByNumber(1));
        var found = vl.Elements.Where(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength).ToList();
        w.Close();
        return found;
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(100)]
    [InlineData(3100)]   // above AvaloniaEdit's element-generator limit
    public void BoldMarkersAreHidden_EvenOnVeryLongLines(int fillerLength)
    {
        string text = new string('a', fillerLength) + " **bold** end";
        int marker = fillerLength + 1;
        var element = Assert.Single(ElementsAt(text, marker));
        bool zeroWidthElement = element.GetType().Name == "HiddenElement";
        bool collapsedText = element.TextRunProperties.FontRenderingEmSize < 0.1
            && element.TextRunProperties.ForegroundBrush is Avalonia.Media.ISolidColorBrush { Color.A: 0 };
        Assert.True(zeroWidthElement || collapsedText, $"{element.GetType().Name}, size {element.TextRunProperties.FontRenderingEmSize}");
        var bold = Assert.Single(ElementsAt(text, marker + 3));
        Assert.Equal(Avalonia.Media.FontWeight.Bold, bold.TextRunProperties.Typeface.Weight);
    }
}
