using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;

namespace EelDitor.App.Tests;

/// <summary>Deleting a selection never leaves a lone, visible style marker (the markers are hidden and have no width).</summary>
public class DeleteMarkersTests
{
    private static string Delete(string text, int start, int length)
    {
        var editor = new TextEditor { Document = new TextDocument(text) };
        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
        editor.Select(start, length);
        if (!MarkdownCommands.DeleteSelectionKeepingMarkers(editor, view))
            editor.Document.Remove(editor.SelectionStart, editor.SelectionLength);
        view.Dispose();
        return editor.Document.Text;
    }

    [AvaloniaTheory]
    // "a **bold** b": a = 0, ** = 2..4, bold = 4..8, ** = 8..10
    [InlineData(2, 6, "a  b")]       // from before the opening ** to the end of the word
    [InlineData(4, 4, "a  b")]       // the word itself (double click)
    [InlineData(3, 5, "a  b")]       // starting inside the marker
    [InlineData(4, 6, "a  b")]       // the word and the closing marker
    [InlineData(5, 3, "a **b** b")]  // part of the word: the markers stay
    [InlineData(5, 5, "a **b** b")]  // part of the word and the closing marker: it stays
    [InlineData(0, 6, "**ld** b")]   // text before the span and part of it: the opening marker stays
    [InlineData(6, 6, "a **bo**")]   // part of it and the text after
    [InlineData(0, 12, "")]          // everything
    public void Bold(int start, int length, string expected) => Assert.Equal(expected, Delete("a **bold** b", start, length));

    [AvaloniaFact]
    public void ALinkTarget_IsNeverCut()
    {
        // "see [doc](a.md) now": selecting from inside the hidden target to "now" keeps the link whole.
        string text = "see [doc](a.md) now";
        Assert.Equal("see [doc](a.md)", Delete(text, 12, 7).TrimEnd());
    }

    [AvaloniaFact]
    public void TypingOverASelection_KeepsTheMarkersOfAPartlySelectedSpan()
    {
        var editor = new TextEditor { Document = new TextDocument("a **bold** b") };
        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
        editor.Select(0, 6);                                  // "a **bo"
        if (MarkdownCommands.DeleteSelectionKeepingMarkers(editor, view)) editor.Document.Insert(editor.CaretOffset, "X");
        Assert.Equal("X**ld** b", editor.Document.Text);
        view.Dispose();
    }
}
