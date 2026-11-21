using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using EelDitor.App.Editor;

namespace EelDitor.App.Tests;

public class ImageInsertTests
{
    [Theory]
    [InlineData(@"C:\docs", @"C:\docs\img\kot.png", "![kot](img/kot.png)")]
    [InlineData(@"C:\docs\notes", @"C:\docs\img\kot.png", "![kot](../img/kot.png)")]
    [InlineData(@"C:\docs", @"C:\docs\moje zdjęcia\kot (1).jpg", "![kot (1)](<moje zdjęcia/kot (1).jpg>)")]
    [InlineData(@"C:\docs", @"D:\obrazy\a.png", "![a](D:/obrazy/a.png)")]
    [InlineData(null, @"C:\obrazy\a.png", "![a](C:/obrazy/a.png)")]
    public void ImageMarkdown_UsesARelativePathWithForwardSlashes(string? dir, string image, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, MarkdownCommands.ImageMarkdown(dir, image));
    }

    [Fact]
    public void OnlyImageFilesCount()
    {
        Assert.True(MarkdownCommands.IsImageFile("a.PNG"));
        Assert.True(MarkdownCommands.IsImageFile("a.svg"));
        Assert.False(MarkdownCommands.IsImageFile("a.md"));
    }

    [AvaloniaFact]
    public void InsertImages_PutsOnePerLineAtTheOffset_AndTheCaretAfterThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        var editor = new TextEditor { Document = new AvaloniaEdit.Document.TextDocument("Tekst.\r\nKoniec.") };
        MarkdownCommands.InsertImages(editor, @"C:\docs", [@"C:\docs\a.png", @"C:\docs\b.png"], 8);
        Assert.Equal("Tekst.\r\n![a](a.png)\r\n![b](b.png)Koniec.", editor.Document.Text);
        Assert.Equal(8 + "![a](a.png)\r\n![b](b.png)".Length, editor.CaretOffset);
    }
}
