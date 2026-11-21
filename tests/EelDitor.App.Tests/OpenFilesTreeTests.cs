using AvaloniaEdit.Document;
using EelDitor.App.Documents;

namespace EelDitor.App.Tests;

public class OpenFilesTreeTests
{
    private static EditorDocument Doc(string path) => new(new TextDocument(""), path);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void NestedDirectories_BecomeNestedGroups()
    {
        string root = Path.Combine(Path.GetTempPath(), "tree-test");
        string P(params string[] parts) => Path.Combine([root, .. parts]);
        var model = new OpenFilesModel();
        var readme = Doc(P("README.md"));
        var view = Doc(P("src", "App", "Views", "Main.cs"));
        var core = Doc(P("src", "Core", "Git.cs"));
        var other = Doc(Path.Combine(Path.GetTempPath(), "elsewhere", "x.txt"));
        foreach (var d in new[] { readme, view, core, other }) model.Add(d);

        // root (README.md) contains "src\App\Views" and "src\Core"; "elsewhere" stays separate.
        var top = model.Groups.Single(g => g.Directory == root);
        Assert.Equal(2, model.Groups.Count);
        Assert.Equal([Path.Combine("src", "App", "Views"), Path.Combine("src", "Core")], top.Children.Select(c => c.Display));
        Assert.Equal(new object[] { top.Children[0], top.Children[1], readme }, top.Items);
        Assert.Equal(new[] { readme, view, core }.OrderBy(d => d.FilePath), top.AllFiles.OrderBy(d => d.FilePath));
        Assert.Equal(new[] { readme, view, core, other }.Length, model.VisualOrder().Count());

        // When the outer file is filtered out, the inner directories are top-level again.
        model.Filter = "Git";
        Assert.Single(model.Groups);
        Assert.Equal(Path.Combine(root, "src", "Core"), model.Groups[0].Directory);
        Assert.Null(model.Groups[0].Parent);
    }
}
