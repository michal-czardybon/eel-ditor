using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>Deleting styled text character by character never shows a marker.</summary>
public class DeleteStyleTests
{
    [AvaloniaTheory]
    [InlineData("Tekst **pogrubiony** i dalej.")]
    [InlineData("- **Prosty widok** tekst")]
    [InlineData("a *kursywa* b ***oba*** c")]
    [InlineData("x **a *b c* d** y")]
    [InlineData("# Nagłówek **gruby tekst** x")]
    [InlineData("z ~~skreślone dwa~~ i `kod dwa` k")]
    public void DeletingOneByOne_ShowsNoMarkers(string text)
    {
        // (Delete from the start of a heading also removes the space hidden after "#": not checked here.)
        foreach (bool backwards in text.StartsWith('#') ? new[] { true } : new[] { true, false })
        {
            var editor = new TextEditor { Document = new TextDocument(text) };
            var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
            string Shown() => InlineStyleEdit.Visible(editor.Document.Text, view.RenderFor(editor.Document.GetLineByNumber(1))).Text;
            string original = Shown();
            editor.CaretOffset = backwards ? text.Length : 0;
            for (int k = 0; k < 100 && Shown().Length > 0; k++)
            {
                string old = Shown();
                if (!MarkdownCommands.HandleDelete(editor, view, backwards))
                {
                    int c = editor.CaretOffset;
                    if (backwards) { if (c == 0) break; editor.Document.Remove(c - 1, 1); }
                    else { if (c >= editor.Document.TextLength) break; editor.Document.Remove(c, 1); }
                }
                string now = Shown();
                Assert.True(now.Length == old.Length - 1, $"{(backwards ? "Backspace" : "Delete")}: '{old}' → '{now}' ({editor.Document.Text})");
                if (backwards) Assert.StartsWith(now.TrimEnd(), original);
                else Assert.EndsWith(now.TrimStart(), original);
            }
            Assert.Equal("", Shown());
            view.Dispose();
        }
    }
}
