using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>Bugs found by <see cref="RandomEditingExplorer"/>, one test each.</summary>
public class MarkdownEditingRegressionTests
{
    private static (TextEditor Editor, MarkdownView View) Edit(string text, bool wrap = false)
    {
        var editor = new TextEditor { Document = new TextDocument(text), WordWrap = wrap };
        return (editor, new MarkdownView(editor, editor.Document, EditorPalette.Dark));
    }

    private static void Type(TextEditor editor, string text)
    {
        foreach (char c in text) editor.TextArea.PerformTextInput(c.ToString());
        Dispatcher.UIThread.RunJobs();
    }

    private static string Shown(string text)
    {
        var md = MarkdownDocument.Parse(text);
        var lines = text.Split('\n');
        return string.Join("\n", lines.Select((l, i) => InlineStyleEdit.Visible(l, md.GetLine(i)).Text));
    }

    [AvaloniaFact]
    public void CtrlB_WithNothingToStyle_InsertsNothing_AndBoldsWhatIsTypedNext()
    {
        var (editor, view) = Edit("abc ");
        editor.CaretOffset = 4;
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("abc ", editor.Document.Text);                 // no "****" on screen
        Type(editor, "de");
        Assert.Equal("abc **de**", editor.Document.Text);
        Assert.Equal(8, editor.CaretOffset);                         // typing goes on inside the bold
    }

    [AvaloniaFact]
    public void CtrlB_Twice_WithNothingToStyle_IsOffAgain()
    {
        var (editor, view) = Edit("abc ");
        editor.CaretOffset = 4;
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Type(editor, "d");
        Assert.Equal("abc d", editor.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData("*ab*\ncd", 3, false, "*ab*cd")]   // Delete before the closing "*" at the end of the line: joins lines
    [InlineData("a\n*b*", 3, true, "a*b*")]        // Backspace after the opening "*" at the start of a line: the same
    [InlineData("x\n## abc", 5, true, "x\nabc")]   // Backspace at the start of a heading's text: no longer a heading
    [InlineData("x\n## abc", 1, false, "xabc")]    // Delete before a heading: its text joins as ordinary text
    public void DeleteAtTheEdgeOfALine(string text, int caret, bool backwards, string expected)
    {
        var (editor, view) = Edit(text);
        editor.CaretOffset = caret;
        if (!MarkdownCommands.HandleDelete(editor, view, backwards))
        {
            int c = editor.CaretOffset;
            if (backwards) editor.Document.Remove(c - 1, 1); else editor.Document.Remove(c, editor.Document.GetLineByOffset(c).DelimiterLength);
        }
        Assert.Equal(expected, editor.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData("## abc", 0, "## xabc")]   // the caret before the hidden "## "
    [InlineData("## abc", 3, "## xabc")]
    public void TypingAtTheStartOfAHeading_StaysInTheHeading(string text, int caret, string expected)
    {
        var (editor, _) = Edit(text);
        editor.CaretOffset = caret;
        Type(editor, "x");
        Assert.Equal(expected, editor.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData("## abc", 3, "\n## abc")]                 // at the start of a heading: the heading moves down
    [InlineData("a *b***c** d", 6, "a *b*\n**c** d")]     // between two styles: between their markers
    [InlineData("# Tytuł **xgruby** t", 11, "# Tytuł **x**\n**gruby** t")]
    public void EnterNextToMarkers(string text, int caret, string expected)
    {
        var (editor, _) = Edit(text);
        editor.CaretOffset = caret;
        Type(editor, "\n");
        Assert.Equal(expected, editor.Document.Text.Replace("\r\n", "\n"));
        Assert.Equal(Shown(text).Replace(" ", ""), Shown(expected).Replace("\n", "").Replace(" ", ""));
    }

    [AvaloniaFact]
    public void TheCaret_NeverStaysInsideAMarker()
    {
        var (editor, _) = Edit("a **bold** b");
        editor.CaretOffset = 9; // between the two "*" of the closing marker
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(10, editor.CaretOffset);
    }

    [AvaloniaFact]
    public void ArrowRight_AtTheEndOfAStyledLine_GoesToTheNextLine()
    {
        var (editor, view) = Edit("a *b*\nc");
        editor.CaretOffset = 4; // after "b", before the closing "*"
        if (!MarkdownCommands.HandleHorizontalMove(editor, view, right: true, extend: false))
            editor.CaretOffset += 1; // the editor's own move: one character (here the line break)
        Assert.Equal(6, editor.CaretOffset);
    }

    [AvaloniaFact]
    public void TableAsGrid_DeleteInARow_DoesNotWipeTheRow()
    {
        string text = "| a | b |\n|---|---|\n| c | d |\n";
        var (editor, view) = Edit(text, wrap: true);
        editor.CaretOffset = text.IndexOf("| c", StringComparison.Ordinal);
        Assert.True(MarkdownCommands.HandleDelete(editor, view, backwards: false));
        Assert.Equal(text, editor.Document.Text);
    }

    [AvaloniaFact]
    public void ItalicOverPartOfABoldSpan_DoesNotRestyleTheTextAround()
    {
        var (editor, view) = Edit("ab **cd** ef");
        editor.Select(1, 5); // "b **c"
        MarkdownCommands.ToggleStyle(editor, view, "*", MdStyle.Italic);
        string text = editor.Document.Text;
        var (shown, styles) = InlineStyleEdit.Visible(text, MarkdownDocument.Parse(text).GetLine(0));
        Assert.Equal("ab cd ef", shown);
        Assert.Equal(MdStyle.Italic, styles[1]);                    // b
        Assert.Equal(MdStyle.Bold | MdStyle.Italic, styles[3]);     // c
        Assert.Equal(MdStyle.Bold, styles[4]);                      // d keeps only its bold
        Assert.Equal(MdStyle.None, styles[6]);                      // e
    }

    [AvaloniaTheory]
    [InlineData("z **pogrubieniem**.", 5, 3, "z **p**ogr**ubieniem**.")]
    [InlineData("z **pogrubieniem**.", 7, 9, "z **pog**rubieniem.")]
    [InlineData("a *bc de* f", 5, 4, "a *bc* de f")]
    public void UnstylingPartOfASpan_UnstylesOnlyThatPart(string text, int start, int length, string expected)
    {
        var (editor, view) = Edit(text);
        editor.Select(start, length);
        MarkdownCommands.ToggleStyle(editor, view, text.Contains("**") ? "**" : "*", text.Contains("**") ? MdStyle.Bold : MdStyle.Italic);
        Assert.Equal(expected, editor.Document.Text);
    }

    [AvaloniaTheory]
    [InlineData("x *abQ* y", 5, 1, " ", "x *ab*  y")]      // a space typed over the last letter goes after the marker
    [InlineData("**a****b**", 5, 0, " ", "**a** **b**")]   // a space between two bold spans
    public void TypingNextToMarkers_ShowsAsTyped(string text, int start, int length, string typed, string expected)
    {
        var (editor, _) = Edit(text);
        editor.Select(start, length);
        if (length == 0) editor.CaretOffset = start;
        Type(editor, typed);
        Assert.Equal(expected, editor.Document.Text);
    }
    [AvaloniaFact]
    public void CtrlB_OnASelectionOfMarkersOnly_DoesNothing()
    {
        var (editor, view) = Edit("eji **yQ**");
        editor.Select(8, 2); // the closing "**" alone
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        Assert.Equal("eji **yQ**", editor.Document.Text);
    }

    [AvaloniaFact]
    public void TypingAfterCtrlB_RemovedTheSelectedText_KeepsTheOrderOfLetters()
    {
        var (editor, view) = Edit("a **bc** d");
        editor.Select(2, 6); // "**bc**": bold off, the selection becomes empty
        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
        editor.CaretOffset = editor.Document.TextLength;
        editor.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(editor.TextArea, editor.CaretOffset, editor.CaretOffset);
        Type(editor, "xy");
        Assert.EndsWith("dxy", editor.Document.Text);
    }
}
