using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>Typing next to styled text extends the style; neighbouring spans of the same style are joined.</summary>
public class TypingStyleTests
{
    private static (TextEditor Editor, MarkdownView View) Edit(string text)
    {
        var editor = new TextEditor { Document = new TextDocument(text) };
        return (editor, new MarkdownView(editor, editor.Document, EditorPalette.Dark));
    }

    private static string Type(string text, int caret, string typed)
    {
        var (editor, view) = Edit(text);
        editor.CaretOffset = caret;
        foreach (char c in typed) editor.TextArea.PerformTextInput(c.ToString());
        view.Dispose();
        return editor.Document.Text;
    }

    [AvaloniaTheory]
    [InlineData("**Prost** edytor", 9, "y", "**Prosty** edytor")]   // after the closing marker: extends the bold
    [InlineData("**Prost** edytor", 7, "y", "**Prosty** edytor")]   // before it: the same
    [InlineData("*ab* c", 4, "cd", "*abcd* c")]                      // italic too
    [InlineData("~~ab~~ c", 6, "c", "~~abc~~ c")]                    // strikethrough
    [InlineData("**Prost**", 7, " ", "**Prost** ")]                  // a space at the end goes after the marker
    [InlineData("**Prost**", 9, " x", "**Prost** x")]                // and what follows the space is plain
    [InlineData("`code` x", 6, "y", "`code`y x")]                    // code is not extended
    public void TypingAfterStyledText(string text, int caret, string typed, string expected) =>
        Assert.Equal(expected, Type(text, caret, typed));

    [AvaloniaTheory]
    [InlineData("a **bold** b", 6, "a **bo**\n**ld** b")]        // the style is closed and opened again
    [InlineData("a **bo ld** b", 7, "a **bo**\n**ld** b")]       // the space at the break is dropped
    [InlineData("a ***bold*** b", 7, "a ***bo***\n***ld*** b")]  // bold italic
    [InlineData("x **a *bc* d**", 8, "x **a *b***\n***c* d**")] // nested
    [InlineData("a `co de` b", 5, "a `co`\n`de` b")]             // code
    [InlineData("a **bold** b", 4, "a \n**bold** b")]            // at the start of the word: before the marker
    [InlineData("a **bold** b", 8, "a **bold**\nb")]             // at its end: after the marker
    [InlineData("  **bold**", 6, "  **bo**\n  **ld**")]          // the indentation is kept
    public void EnterInsideAStyle(string text, int caret, string expected)
    {
        text = text.Replace("|", "");
        Assert.Equal(expected, Type(text, caret, "\n").Replace("\r\n", "\n"));
    }

    [AvaloniaFact]
    public void EnterInsideAStyle_IsOneUndoStep()
    {
        var (editor, view) = Edit("a **bold** b");
        editor.CaretOffset = 6;
        editor.TextArea.PerformTextInput("\n");
        editor.Undo();
        Assert.Equal("a **bold** b", editor.Document.Text);
        view.Dispose();
    }

    [AvaloniaFact]
    public void BoldingTheNextLetter_JoinsTheSpans()
    {
        var (editor, view) = Edit("**Prost**y edytor");
        editor.Select(9, 1);
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("**Prosty** edytor", editor.Document.Text);
    }

    [AvaloniaFact]
    public void SpansWithSomethingBetween_StaySeparate()
    {
        var (editor, view) = Edit("**a** b");
        editor.Select(6, 1);
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("**a** **b**", editor.Document.Text);
    }

    [AvaloniaFact]
    public void TypingOverASelection_KeepsTheMarkersOfAPartlySelectedSpan()
    {
        var (editor, view) = Edit("a **bold** b");
        editor.Select(0, 6);                       // "a **bo"
        editor.TextArea.PerformTextInput("X");
        Assert.Equal("X**ld** b", editor.Document.Text);
    }
}
