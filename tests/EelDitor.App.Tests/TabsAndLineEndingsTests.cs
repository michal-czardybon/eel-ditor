using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EelDitor.App.Documents;
using EelDitor.App.Services;
using EelDitor.App.Views;

namespace EelDitor.App.Tests;

/// <summary>Tabs stay tabs (typed and pasted); "Show line endings" does not change the layout.</summary>
public class TabsAndLineEndingsTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string name, string text, AppSettings? settings = null)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(settings ?? new AppSettings(), [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    [Fact]
    public void Defaults_TabIsATab_FourWide()
    {
        var s = new AppSettings();
        Assert.False(s.UseSpaces);
        Assert.Equal(4, s.TabSize);
    }

    [AvaloniaFact]
    public void TabKey_TypesATab_ByDefault()
    {
        var (w, doc) = Open("a.txt", "x");
        w.Editor.CaretOffset = 1;
        w.Editor.TextArea.Focus();
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("x\t", doc.Document.Text);
    }

    /// <summary>
    /// Regression: AvaloniaEdit's paste turned every tab into spaces when "Insert spaces" was on. Pasted text keeps
    /// its tabs whatever that setting is; only the line endings follow the file's.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    public async Task Paste_KeepsTabs_EvenWithInsertSpacesOn(string name)
    {
        var (w, doc) = Open(name, "x\r\n", new AppSettings { UseSpaces = true });
        Assert.True(w.Editor.Options.ConvertTabsToSpaces);
        await w.Clipboard!.SetTextAsync("a\tb\n\tc");
        w.Editor.CaretOffset = doc.Document.TextLength;
        w.RunCommand("edit.paste");
        for (int i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs();
        Assert.Equal("x\r\na\tb\r\n\tc", doc.Document.Text);
        Assert.Equal(doc.Document.TextLength, w.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task Paste_ReplacesTheSelection_AsOneUndoStep()
    {
        var (w, doc) = Open("a.txt", "one two three");
        await w.Clipboard!.SetTextAsync("2\t2");
        w.Editor.Select(4, 3);
        w.RunCommand("edit.paste");
        for (int i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs();
        Assert.Equal("one 2\t2 three", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("one two three", doc.Document.Text);
    }

    /// <summary>The marks are drawn (spaces, tabs, line ends, also in Markdown with hidden markup and wrapping).</summary>
    [AvaloniaFact]
    public void WhitespaceMarks_Draw()
    {
        var editor = new AvaloniaEdit.TextEditor
        {
            Document = new AvaloniaEdit.Document.TextDocument("# Tytuł z\tsłowami\r\n**gruby tekst** i\tdalej " + string.Join(" ", Enumerable.Repeat("słowo", 80)) + "\r\nkoniec\n"),
            WordWrap = true,
        };
        var window = new Avalonia.Controls.Window { Width = 500, Height = 400, Content = editor };
        window.Show();
        var view = new EelDitor.App.Editor.MarkdownView(editor, editor.Document, EelDitor.App.Editor.EditorPalette.Dark);
        var marks = new EelDitor.App.Editor.WhitespaceMarks(editor) { Spaces = true, LineEnds = true };
        Dispatcher.UIThread.RunJobs();
        var tv = editor.TextArea.TextView;
        tv.EnsureVisualLines();
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(500, 400));
        using (var context = bitmap.CreateDrawingContext()) marks.Draw(tv, context);
        view.Dispose();
        window.Close();
    }

    /// <summary>
    /// Regression: AvaloniaEdit's marks are elements in the line. ␍ ␊ come from a fallback font with a taller line
    /// (lines changed height), and a space mark is as wide as a space in the normal size (headings, drawn larger, got
    /// narrower). The marks are now drawn outside the layout: showing them changes no line's height or width.
    /// </summary>
    [AvaloniaFact]
    public void ShowAllCharacters_ChangesNoLineHeightOrWidth()
    {
        string paragraph = string.Join(" ", Enumerable.Repeat("słowo", 120));
        var (w, _) = Open("a.md", "# Tytuł z kilkoma słowami\r\n\r\n## Drugi\tnagłówek\r\n\r\n" + paragraph + "\r\n\r\nkrótka\tlinia\r\n",
            new AppSettings { WordWrap = true, WindowWidth = 900 });
        var tv = w.Editor.TextArea.TextView;
        List<(double Height, double Width)> Lines()
        {
            tv.Redraw();
            w.UpdateLayout();
            return tv.VisualLines.SelectMany(vl => vl.TextLines.Select(t => (t.Height, t.WidthIncludingTrailingWhitespace))).ToList();
        }
        var before = Lines();
        w.RunCommand("view.allChars");
        Assert.True(w.ShowsEndOfLine && w.ShowsSpaces);
        var o = w.Editor.Options;
        Assert.False(o.ShowEndOfLine || o.ShowSpaces || o.ShowTabs); // AvaloniaEdit's own marks stay off
        Assert.Equal(before, Lines());
        w.RunCommand("view.allChars");
        Assert.False(w.ShowsEndOfLine || w.ShowsSpaces);
    }
}
