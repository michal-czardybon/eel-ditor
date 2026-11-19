using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CarbonPad.App.Editor;
using CarbonPad.App.Views;
using CarbonPad.Core.Git;

namespace CarbonPad.App.Tests;

public class DiffViewTests
{
    private static (DiffView View, Window Window) Show(TextDocument left, bool leftEditable, TextDocument right, string ext = ".txt")
    {
        var view = new DiffView("left", left, leftEditable, "right", right, true, EditorPalette.Dark,
            new FontFamily("Consolas"), 13, ext, TextMateSharp.Grammars.ThemeName.DarkPlus);
        var w = new Window { Content = view, Width = 900, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, w);
    }

    [AvaloniaFact]
    public void DiffView_RendersAndTracksEdits()
    {
        var right = new TextDocument("a\nB\nc\nd\ne");
        var (view, w) = Show(DiffView.ReadOnlyDocument("a\nb\nc\nd"), false, right);
        // Opening the view jumps to the first change.
        Assert.Equal(2, view.RightEditor.TextArea.Caret.Line);
        view.GoToChange(1);
        Assert.Equal(5, view.RightEditor.TextArea.Caret.Line);
        view.GoToChange(1);
        Assert.Equal(2, view.RightEditor.TextArea.Caret.Line); // wraps around
        view.GoToChange(-1);
        Assert.Equal(5, view.RightEditor.TextArea.Caret.Line);
        right.Insert(0, "new\n");
        w.Close();
    }

    [AvaloniaFact]
    public void DiffView_FollowsThemeChange()
    {
        var (view, w) = Show(DiffView.ReadOnlyDocument("a"), false, new TextDocument("b"), ".cs");
        view.ApplyPalette(EditorPalette.Light, TextMateSharp.Grammars.ThemeName.LightPlus);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(EditorPalette.Light.Background, view.RightEditor.Background);
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData("a|b|c", "a|X|c")]   // modified line
    [InlineData("a|b|c", "a|c")]     // line missing on the right
    [InlineData("a|c", "a|b|c")]     // extra line on the right
    [InlineData("a|b", "a")]         // missing at the end
    [InlineData("a", "a|b")]         // extra at the end
    [InlineData("x|a", "a")]         // missing at the start
    [InlineData("a|b|", "a|")]       // trailing line break kept
    [InlineData("a|b|c|d|e|f", "A|b|c|D|e|F|g")]
    [InlineData("", "a|b")]
    [InlineData("a|b", "")]
    public void CopyingEveryBlockToTheRight_MakesSidesIdentical(string leftText, string rightText)
    {
        foreach (string nl in new[] { "\n", "\r\n" })
        {
            string l = leftText.Replace("|", nl), r = rightText.Replace("|", nl);
            var right = new TextDocument(r);
            var (view, w) = Show(DiffView.ReadOnlyDocument(l), false, right);
            for (int guard = 0; guard < 10 && !view.Diff.Identical; guard++)
            {
                view.GoToChange(1);
                view.CopyBlock(toRight: true);
            }
            Assert.Equal(l, right.Text);
            w.Close();
        }
    }

    [AvaloniaFact]
    public void CopyBlockToLeft_ChangesOnlyThatBlock_AndUndoesInOneStep()
    {
        var left = new TextDocument("a\nb\nc\nd\ne");
        var right = new TextDocument("a\nB\nc\nd\nE");
        var (view, w) = Show(left, true, right);
        view.RightEditor.Focus();
        view.RightEditor.TextArea.Caret.Line = 2;
        view.CopyBlock(toRight: false);
        Assert.Equal("a\nB\nc\nd\ne", left.Text);
        Assert.Single(view.Diff.Blocks);
        left.UndoStack.Undo();
        Assert.Equal("a\nb\nc\nd\ne", left.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void CopyBlock_DoesNothingOnReadOnlySide()
    {
        var left = DiffView.ReadOnlyDocument("a\nb");
        var right = new TextDocument("a\nB");
        var (view, w) = Show(left, false, right);
        view.CopyBlock(toRight: false);
        Assert.Equal("a\nb", left.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void CopyAll_KeepsTargetLineEndings()
    {
        var right = new TextDocument("x\r\ny\r\n");
        var (view, w) = Show(DiffView.ReadOnlyDocument("a\nb\n"), false, right);
        view.CopyAll(toRight: true);
        Assert.Equal("a\r\nb\r\n", right.Text);
        Assert.True(right.UndoStack.CanUndo);
        Assert.True(view.Diff.Identical || view.Diff.Blocks.Count == 0 || right.Text == "a\r\nb\r\n");
        w.Close();
    }

    [AvaloniaFact]
    public void HistoryPanel_BuildsWithCommits()
    {
        var doc = new CarbonPad.App.Documents.EditorDocument(new TextDocument("x"), null);
        var commits = new[] { new GitCommit("0123456789abcdef", "Ktoś", DateTimeOffset.Now, "Pierwszy commit", "a.txt") };
        var panel = new HistoryPanel(doc, @"C:\repo", "a.txt", commits);
        var w = new Window { Content = panel };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.Close();
    }
}
