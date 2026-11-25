using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>In the word view a change can be reverted (it used to be disabled there), and from the context menu.</summary>
public class WordDiffRevertUiTests
{
    private static (DiffView View, Window Window) Show(string left, TextDocument right)
    {
        var view = new DiffView("HEAD", DiffView.ReadOnlyDocument(left), false, "file", right, true, EditorPalette.Dark,
            new FontFamily("Consolas"), 13, ".txt", TextMateSharp.Grammars.ThemeName.DarkPlus);
        var w = new Window { Content = view, Width = 900, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, w);
    }

    [AvaloniaFact]
    public void InTheWordView_TheChangeAtTheCaret_IsReverted_AndOnlyIt()
    {
        var right = new TextDocument("Ala ma psa.\r\nPies jest mały i bury.\r\n");
        var (view, w) = Show("Ala ma kota.\r\nPies jest duży i bury.\r\n", right);
        view.WordMode = true;
        Dispatcher.UIThread.RunJobs();
        // Switching to the word view lands on the first change; F7 goes to the second one: duży → mały.
        view.GoToChange(1);
        Assert.True(view.RevertCurrentChange());
        Assert.Equal("Ala ma psa.\r\nPies jest duży i bury.\r\n", right.Text);   // line ends kept
        Assert.Equal(1, view.WordDiffResult!.Changes.Count);
        right.UndoStack.Undo();                               // one undo step
        Assert.Equal("Ala ma psa.\r\nPies jest mały i bury.\r\n", right.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void InTheWordView_ARemovedWordComesBackWithItsSpace()
    {
        var right = new TextDocument("a c");
        var (view, w) = Show("a b c", right);
        view.WordMode = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.RevertCurrentChange());
        Assert.Equal("a b c", right.Text);
        w.Close();
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(60); }
    }

    private static void Type(DiffView view, int wordsOffset, string text)
    {
        var editor = view.WordsEditor;
        editor.TextArea.ClearSelection();
        editor.CaretOffset = wordsOffset;
        editor.TextArea.PerformTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheWordView_CanBeEdited_AndTheEditGoesIntoTheFile()
    {
        var right = new TextDocument("Ala ma psa.\r\nPies jest mały.\r\n");
        var (view, w) = Show("Ala ma kota.\r\nPies jest duży.\r\n", right);
        view.WordMode = true;
        Settle();
        // The word view's current text (the diff may be computed again between edits and give a new document).
        string Words() => view.WordsEditor.Document.Text;
        Assert.False(view.WordsEditor.IsReadOnly);

        // In unchanged text: "Pies" → "Pies nie".
        Type(view, Words().IndexOf("Pies", StringComparison.Ordinal) + 4, " nie");
        Assert.Equal("Ala ma psa.\r\nPies nie jest mały.\r\n", right.Text);
        // In an added word: "psa" → "psaa".
        Type(view, Words().IndexOf("psa", StringComparison.Ordinal) + 3, "a");
        Assert.Equal("Ala ma psaa.\r\nPies nie jest mały.\r\n", right.Text);
        // A line break: in the file's line endings.
        Type(view, Words().IndexOf("jest", StringComparison.Ordinal), "\n");
        Assert.Equal("Ala ma psaa.\r\nPies nie \r\njest mały.\r\n", right.Text);
        // A removed word is not in the file: nothing can be typed into it.
        int kota = Words().IndexOf("kota", StringComparison.Ordinal);
        Type(view, kota + 2, "X");
        Assert.Equal("Ala ma psaa.\r\nPies nie \r\njest mały.\r\n", right.Text);
        Assert.DoesNotContain("koXta", Words());

        // After the diff is computed again the word view still shows both versions.
        Settle();
        Assert.Contains("kota", view.WordsEditor.Document.Text);
        Assert.Contains("psaa", view.WordsEditor.Document.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void DeletingAcrossARemovedWord_DeletesOnlyTheFilesText()
    {
        var right = new TextDocument("a X c");
        var (view, w) = Show("a b c", right);
        view.WordMode = true;
        Settle();
        var editor = view.WordsEditor;
        // The word view shows "a bX c" (b removed, X added): select all of it and delete.
        editor.SelectAll();
        editor.TextArea.PerformTextInput("z");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("z", right.Text.Trim());
        w.Close();
    }

    [AvaloniaFact]
    public void TheContextMenu_OfferRevertThisChange()
    {
        var right = new TextDocument("one\nTWO\nthree");
        var (view, w) = Show("one\ntwo\nthree", right);
        view.WordMode = true;
        Dispatcher.UIThread.RunJobs();
        var menu = view.WordsEditor.ContextMenu!;
        menu.Open(view.WordsEditor);
        Dispatcher.UIThread.RunJobs();
        var revert = menu.Items.OfType<MenuItem>().First();
        Assert.True(revert.IsEnabled);
        revert.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("one\ntwo\nthree", right.Text);
        menu.Close();
        w.Close();
    }
}
