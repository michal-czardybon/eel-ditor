using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using CarbonPad.App.Documents;
using CarbonPad.App.Editor;
using CarbonPad.App.Services;
using CarbonPad.App.Views;

namespace CarbonPad.App.Tests;

/// <summary>Task boxes and image previews in the real editor.</summary>
public class MarkdownWidgetTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static (MainWindow W, EditorDocument Doc) Open(string name, string text, Action<string>? prepare = null)
    {
        string dir = TestDirs.New();
        Directory.CreateDirectory(dir);
        prepare?.Invoke(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        SettingsStore.UseDirectory(TestDirs.New());
        var w = new MainWindow(new AppSettings(), [path]);
        w.Show();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
        var doc = ((OpenFilesModel)w.DataContext!).Documents.Single(d => d.FilePath == path);
        return (w, doc);
    }

    private static VisualLine Line(MainWindow w, int number) =>
        w.Editor.TextArea.TextView.GetOrConstructVisualLine(w.Editor.Document.GetLineByNumber(number));

    [AvaloniaFact]
    public void TaskBox_IsDrawn_AndTheCommandTogglesIt()
    {
        var (w, doc) = Open("t.md", "- [ ] mleko\nzwykła linia\n");
        var box = Line(w, 1).Elements.OfType<TaskBoxElement>().Single();
        Assert.False(box.IsChecked);
        Assert.Equal(5, box.DocumentLength); // "- [ ]"

        w.Editor.CaretOffset = 3;
        w.RunCommand("md.task");
        Assert.StartsWith("- [x] mleko", doc.Document.Text);
        Assert.True(Line(w, 1).Elements.OfType<TaskBoxElement>().Single().IsChecked);

        // A click on the box unchecks it again.
        var tv = w.Editor.TextArea.TextView;
        tv.EnsureVisualLines();
        var vl = Line(w, 1);
        var inBox = vl.GetVisualPosition(0, VisualYPosition.LineMiddle) + new Avalonia.Vector(tv.WideSpaceWidth * 2, 0) - tv.ScrollOffset;
        var inWindow = tv.TranslatePoint(inBox, w)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(w, inWindow, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(w, inWindow, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("- [ ] mleko", doc.Document.Text);

        w.Editor.CaretOffset = doc.Document.GetLineByNumber(2).Offset;
        w.RunCommand("md.task");
        Assert.Contains("- [ ] zwykła linia", doc.Document.Text);
    }

    [AvaloniaFact]
    public void Image_ShowsAPreviewOfALocalFile_AndOnlyTheAltTextOtherwise()
    {
        var (w, _) = Open("i.md", "![kropka](img/k.png)\n![brak](nie-ma.png)\n![zdalny](https://example.com/a.png)\n", dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "img"));
            File.WriteAllBytes(Path.Combine(dir, "img", "k.png"), Convert.FromBase64String(OnePixelPng));
        });
        Assert.IsType<Image>(Line(w, 1).Elements.OfType<InlineObjectElement>().Single().Element);
        Assert.Empty(Line(w, 2).Elements.OfType<InlineObjectElement>());
        Assert.Empty(Line(w, 3).Elements.OfType<InlineObjectElement>()); // never fetched from the network
    }
}
