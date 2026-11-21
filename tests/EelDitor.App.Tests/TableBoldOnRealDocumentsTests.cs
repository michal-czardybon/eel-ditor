using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>Ctrl+B on every row of the tables in real documents, selected or with the caret on it, never breaks the table.</summary>
public class TableBoldOnRealDocumentsTests
{
    [AvaloniaFact]
    public void EveryRow_EverySelection()
    {
        var problems = new List<string>();
        foreach (var file in new[]
        {
            @"docs\DESIGN.md",
            @"docs\DESIGN.md",
            @"docs\DESIGN.md",
        })
        {
            if (!File.Exists(file)) continue; // the documents of the repository
            string original = File.ReadAllText(file).Replace("\r\n", "\n");
            var lines = original.Split('\n');
            var md0 = MarkdownDocument.Parse(original);
            foreach (var table in md0.Tables)
                foreach (var row in table.Rows)
                    foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 })
                    {
                        // Only the table (and a line around it) — fast, and the same parse as in the editor.
                        int first = Math.Max(0, table.FirstLine - 1), last = Math.Min(lines.Length - 1, table.LastLine + 1);
                        string text = string.Join("\n", lines[first..(last + 1)]);
                        var editor = new TextEditor { Document = new TextDocument(text) };
                        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
                        int n = row.LineIndex - first + 1;
                        var line = editor.Document.GetLineByNumber(n);
                        switch (mode)
                        {
                            case 4: editor.CaretOffset = line.Offset; break;                                   // caret at the line start
                            case 5: editor.CaretOffset = line.EndOffset; break;                                // caret at the line end
                            case 0: editor.Select(line.Offset, line.Length); break;
                            case 1: editor.Select(line.Offset, line.TotalLength); break;
                            case 2: editor.Select(Math.Max(0, line.Offset - 1), line.Length + 1); break;
                            case 3: editor.Select(line.Offset + 2, Math.Max(0, line.Length - 4)); break;
                        }
                        MarkdownCommands.ToggleStyle(editor, view, "**", MdStyle.Bold);
                        var md1 = MarkdownDocument.Parse(editor.Document.Text);
                        var md0b = MarkdownDocument.Parse(text);
                        string before = lines[row.LineIndex];
                        string after = editor.Document.GetText(editor.Document.GetLineByNumber(n));
                        var t1 = md1.TableAtLine(n - 1);
                        string shownBefore = InlineStyleEdit.Visible(before, md0b.GetLine(n - 1)).Text;
                        string shownAfter = InlineStyleEdit.Visible(after, md1.GetLine(n - 1)).Text;
                        if (t1 is null || t1.Rows.Count != table.Rows.Count || t1.RowAtLine(n - 1)!.Cells.Count != row.Cells.Count || shownBefore != shownAfter)
                            problems.Add($"{Path.GetFileName(file)}:{row.LineIndex + 1} mode {mode}\n  before: {before}\n  after:  {after}\n  shown:  {shownAfter}");
                        view.Dispose();
                    }
        }
        Assert.True(problems.Count == 0, problems.Count + " problems:\n" + string.Join("\n", problems.Take(8)));
    }
}
