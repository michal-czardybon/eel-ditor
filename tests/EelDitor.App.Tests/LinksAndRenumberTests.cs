using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Web addresses (underlined, Ctrl+click) and Renumber items.</summary>
public class LinksAndRenumberTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string name, string source)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static bool Underlined(VisualLine line, int column) =>
        line.Elements.First(e => e.VisualColumn <= column && column < e.VisualColumn + e.VisualLength)
            .TextRunProperties.TextDecorations is { Count: > 0 };

    [AvaloniaTheory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    public void BareAddress_IsUnderlined_InPlainTextAndMarkdown(string name)
    {
        var (w, _) = Open(name, "see https://example.org/x here\n");
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var line = tv.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(1));
        Assert.True(Underlined(line, 8));
        Assert.False(Underlined(line, 1));
    }

    [AvaloniaFact]
    public void PlainLinks_CtrlClick_ActivatesTheAddress_APlainClickDoesNot()
    {
        var editor = new TextEditor { FontFamily = new FontFamily("Consolas"), FontSize = 14 };
        editor.Document = new AvaloniaEdit.Document.TextDocument("go to www.example.org now");
        var window = new Window { Width = 600, Height = 200, Content = editor };
        window.Show();
        var links = new PlainLinks(editor, () => true, () => Brushes.Blue);
        var opened = new List<string>();
        links.LinkActivated += opened.Add;
        Dispatcher.UIThread.RunJobs();
        var tv = editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var p = tv.GetVisualPosition(new TextViewPosition(1, 10), VisualYPosition.LineMiddle) - tv.ScrollOffset;
        var inWindow = tv.TranslatePoint(p, window)!.Value;

        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Assert.Empty(opened);

        Thread.Sleep(600); // not a double click (under load the two presses were taken for one)
        window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(inWindow, MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(["https://www.example.org"], opened);
        window.Close();
    }

    [AvaloniaFact]
    public void Renumber_NumbersTheList_AsOneUndoStep()
    {
        var (w, doc) = Open("a.md", "1. a\n1. b\n5. c\n");
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(2).Offset + 3;
        w.RunCommand("md.list.renumber");
        Assert.Equal("1. a\n2. b\n3. c\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("1. a\n1. b\n5. c\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void Renumber_IsInTheContextMenu_OnlyInANumberedList()
    {
        var (w, doc) = Open("a.md", "1. a\n3. b\n\n- x\n");
        var menu = w.Editor.ContextMenu!;
        bool Offered(int line)
        {
            w.Editor.CaretOffset = doc.Document.GetLineByNumber(line).Offset + 3;
            Dispatcher.UIThread.RunJobs();
            w.Editor.RaiseEvent(new ContextRequestedEventArgs());
            Dispatcher.UIThread.RunJobs();
            bool found = menu.Items.OfType<MenuItem>().Any(i => (string?)i.Header == Loc.T("Menu.Md.List.Renumber"));
            menu.Close();
            return found;
        }
        Assert.True(Offered(2));
        Assert.False(Offered(4));
    }
}
