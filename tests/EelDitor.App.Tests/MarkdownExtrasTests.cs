using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using EelDitor.App.Documents;
using EelDitor.App.Editor;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Core.Text;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>Heading sizes, sub/superscript, the long-line bar, the Cyrillic view with formatting, export.</summary>
public class MarkdownExtrasTests
{
    private static (MainWindow W, EditorDocument Doc) Open(string source, bool wrap = false, WrapMode paragraphs = WrapMode.OneLine, string name = "x.md")
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings { WordWrap = wrap, ParagraphWrap = paragraphs, WindowWidth = 900 }, [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        return (w, ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path));
    }

    private static VisualLine Line(MainWindow w, int n) =>
        w.Editor.TextArea.TextView.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(n));

    private static double TextSize(VisualLine line) =>
        line.Elements.First(e => e is VisualLineText && e.VisualLength > 0).TextRunProperties.FontRenderingEmSize;

    [AvaloniaFact]
    public void HeadingLevels_HaveDifferentSizes()
    {
        var (w, _) = Open("# Jeden\n\n## Dwa\n\n### Trzy\n\n#### Cztery\n\ntekst\n");
        double h1 = TextSize(Line(w, 1)), h2 = TextSize(Line(w, 3)), h3 = TextSize(Line(w, 5)), h4 = TextSize(Line(w, 7)), text = TextSize(Line(w, 9));
        Assert.True(h1 > h2 && h2 > h3 && h3 > text, $"{h1} {h2} {h3} {text}");
        Assert.Equal(text, h4);
        w.Close();
    }

    [AvaloniaFact]
    public void HeadingSubmenu_InTheContextMenu_ShowsTheLevel()
    {
        var (w, doc) = Open("tekst\n\n## Dwa\n");
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).Offset + 4;
        Dispatcher.UIThread.RunJobs();
        var menu = w.Editor.ContextMenu!;
        w.Editor.RaiseEvent(new ContextRequestedEventArgs());   // as a right click does: the menu is built on opening
        Dispatcher.UIThread.RunJobs();
        var heading = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Md.Heading"));
        var checkedItems = heading.Items.OfType<MenuItem>().Where(i => i.IsChecked).Select(i => (string?)i.Header).ToList();
        Assert.Equal([Loc.T("Menu.Md.H2")], checkedItems);
        menu.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Subscript_IsWrittenWithTildes_AndShownSmaller()
    {
        var (w, doc) = Open("H2O\n");
        w.Editor.Select(1, 1);
        w.RunCommand("md.sub");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("H~2~O\n", doc.Document.Text);
        var line = Line(w, 1);
        double sizes = line.Elements.Where(e => e is VisualLineText).Min(e => e.TextRunProperties.FontRenderingEmSize);
        Assert.True(sizes < TextSize(line));
    }

    [AvaloniaTheory]
    [InlineData("# T\n\nPierwsze zdanie jest tu. Drugie zdanie jest tam, a trzecie też jest gdzieś tutaj obok nich wszystkich razem i jeszcze dalej, aż do końca.\n", WrapMode.OneLine)]
    [InlineData("# T\n\nPierwsze zdanie.\nDrugie zdanie, dłuższe.\nTrzecie.\n\nKolejne.\nI jeszcze.\n", WrapMode.Sentences)]
    public void Toolbar_ShowsTheSetting_AndTheFilesConvention_WarningWhenTheyDiffer(string text, WrapMode convention)
    {
        var (w, _) = Open(text, paragraphs: WrapMode.Width80);
        Assert.False(w.InfoBar.IsVisible);
        Assert.Equal(1, w.ParagraphCombo!.SelectedIndex);   // 80 columns
        Assert.Equal(MainWindow.ParagraphModeLabel(convention), w.FileConventionShown.Text);
        Assert.True(w.FileConventionShown.Warning);

        // Choosing the file's convention in the combo box: no warning.
        w.ParagraphCombo.SelectedIndex = convention == WrapMode.OneLine ? 0 : 4;
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.FileConventionShown.Warning);
        Assert.True(w.StatusLongest.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void ConvertStyle_InTheWholeFile_OrInTheSelection_OneUndoStep()
    {
        var (w, doc) = Open("Tekst ==ważny== i ==drugi==.\n\n| a | b |\n|---|---|\n| ==x== | y |\n\n```\n==kod==\n```\n");
        var edit = w.MainMenu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Menu.Edit"));
        var convert = edit.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Loc.T("Convert.Menu"));
        Assert.Equal(6, convert.Items.Count);

        w.RunCommand("edit.convert.HighlightBold");
        Assert.Equal("Tekst **ważny** i **drugi**.\n\n| a | b |\n|---|---|\n| **x** | y |\n\n```\n==kod==\n```\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.StartsWith("Tekst ==ważny== i ==drugi==.", doc.Document.Text);

        // With a selection: only the spans it touches.
        int at = doc.Document.Text.IndexOf("drugi", StringComparison.Ordinal);
        w.Editor.Select(at, 2);
        w.RunCommand("edit.convert.HighlightItalic");
        Assert.StartsWith("Tekst ==ważny== i *drugi*.", doc.Document.Text);
        // No Close(): the document is modified, and closing would wait for the save prompt.
    }

    private static void Press(MainWindow w, Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers mods = Avalonia.Input.RawInputModifiers.None)
    {
        Avalonia.Headless.HeadlessWindowExtensions.KeyPress(w, key, mods, Avalonia.Input.PhysicalKey.None, null);
        Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(w, key, mods, Avalonia.Input.PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Enter_InAList_StartsTheNextItem_RenumbersAndEnds()
    {
        var (w, doc) = Open("1. pierwszy\n2. drugi\n");
        w.Editor.TextArea.Focus();
        w.Editor.CaretOffset = "1. pierwszy".Length;
        Press(w, Avalonia.Input.Key.Enter);
        Assert.Equal("1. pierwszy\n2. \n3. drugi\n", doc.Document.Text);
        Assert.Equal("1. pierwszy\n2. ".Length, w.Editor.CaretOffset);
        w.Editor.Document.Insert(w.Editor.CaretOffset, "nowy");
        Press(w, Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Shift);   // a line inside the item
        Assert.Equal("1. pierwszy\n2. nowy\n   \n3. drugi\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Press(w, Avalonia.Input.Key.Enter);
        Press(w, Avalonia.Input.Key.Enter);  // on the new empty item: the list ends there
        Assert.Equal("1. pierwszy\n2. nowy\n\n4. drugi\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void ListItem_AboveAndBelow_FromTheCommands()
    {
        var (w, doc) = Open("- a\n- b\n");
        w.Editor.CaretOffset = 1;
        w.RunCommand("md.list.itemBelow");
        Assert.Equal("- a\n- \n- b\n", doc.Document.Text);
        w.RunCommand("md.list.itemAbove");
        Assert.Equal("- a\n- \n- \n- b\n", doc.Document.Text);
        Assert.Equal("- a\n- ".Length, w.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public void ClearFormatting_OfTheSelection()
    {
        var (w, doc) = Open("Tekst **bardzo ważny** i ==zaznaczony== koniec.\n");
        w.Editor.Select(0, doc.Document.Text.IndexOf(" koniec", StringComparison.Ordinal));
        w.RunCommand("md.clearFormat");
        Assert.Equal("Tekst bardzo ważny i zaznaczony koniec.\n", doc.Document.Text);
        doc.Document.UndoStack.Undo();
        Assert.Equal("Tekst **bardzo ważny** i ==zaznaczony== koniec.\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void HorizontalRule_IsDrawnAcrossTheView_AndDeletedWhole()
    {
        var (w, doc) = Open("a\n\n---\n\nb\n");
        Assert.IsType<RuleElement>(Assert.Single(Line(w, 3).Elements));
        w.Editor.TextArea.Focus();
        w.Editor.CaretOffset = doc.Document.GetLineByNumber(3).Offset;
        Press(w, Avalonia.Input.Key.Delete);
        Assert.Equal("a\n\n\nb\n", doc.Document.Text);
    }

    [AvaloniaFact]
    public void ByDefault_NewTextIsLongLines()
    {
        Assert.Equal(WrapMode.OneLine, new AppSettings().ParagraphWrap);
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "n.md");
        File.WriteAllText(path, "# T\n");
        SettingsStore.UseDirectory(TestDirs.New());           // no saved settings: a new user
        var w = new MainWindow(SettingsStore.Load(), [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Loc.T("Wrap.OptionOneLineShort"), w.ParagraphCombo!.SelectedItem);
        w.Close();
    }

    [AvaloniaFact]
    public void ParagraphTools_AreHiddenForSourceCode()
    {
        var (w, _) = Open("class A { }\n", name: "a.cs");
        Assert.False(w.ParagraphCombo!.IsVisible);
        Assert.False(w.StatusLongest.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void CyrillicView_KeepsTheFormatting_AndConvertsTableCells()
    {
        var (w, doc) = Open("**Litwo** ojczyzno `kod`\n\n| a | b |\n|---|---|\n| kot | pies |\n", wrap: true);
        w.RunCommand("view.cyr.ru");
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Editor.IsReadOnly);
        Assert.NotNull(w.MarkdownViewForTests);
        var first = Line(w, 1);
        Assert.Contains(first.Elements, e => e is HiddenElement);                       // ** still hidden
        Assert.Contains(first.Elements, e => e is CyrillicWordElement);
        Assert.Contains(first.Elements, e => e is CyrillicWordElement && e.TextRunProperties.Typeface.Weight == Avalonia.Media.FontWeight.Bold);
        var row = Assert.IsType<TableRowElement>(Assert.Single(Line(w, 5).Elements));
        Assert.Equal("кот", row.Cells[0].Text);
        Assert.Equal("**Litwo** ojczyzno `kod`\n\n| a | b |\n|---|---|\n| kot | pies |\n", doc.Document.Text);

        w.RunCommand("view.cyr.plA");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("кот", ((TableRowElement)Line(w, 5).Elements[0]).Cells[0].Text);
        w.RunCommand("view.cyrNone");
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.Editor.IsReadOnly);
        Assert.Equal("kot", ((TableRowElement)Line(w, 5).Elements[0]).Cells[0].Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Export_BuildsAPageOfTheDocument()
    {
        var (w, doc) = Open("# Tytuł\n\n**gruby**\n");
        string html = MainWindow.ExportHtml(doc);
        Assert.Contains("<h1>Tytuł</h1>", html);
        Assert.Contains("<strong>gruby</strong>", html);
        Assert.Contains("<base href=\"file:///", html);
        w.Close();
    }
}

public class PdfExportTests
{
    [Fact]
    public async Task APage_IsPrintedToPdf()
    {
        if (PdfExport.FindBrowser() is null) return; // no Edge / Chrome on this machine
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        string pdf = Path.Combine(dir, "out.pdf");
        string html = MarkdownHtml.Page("t", MarkdownHtml.Body("# Tytuł\n\n| a | b |\n|---|---|\n| 1 | 2 |\n"));
        Assert.Null(await PdfExport.PrintAsync(html, pdf));
        var head = new byte[4];
        await using (var f = File.OpenRead(pdf)) await f.ReadExactlyAsync(head);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(head));
    }
}
