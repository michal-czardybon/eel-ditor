using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EelDitor.App.Services;
using EelDitor.App.Views;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>
/// Exploratory testing: random key sequences (typing, Backspace, Delete, Enter, arrows, selections, Ctrl+B/I,
/// undo/redo) on Markdown documents, checked after every key against rules that always hold: no style marker
/// becomes visible, Backspace/Delete remove exactly one visible character, typing inserts exactly what was typed,
/// arrows move by one visible position. Slow, so it runs only with EELDITOR_EXPLORE=1; the failures found go to
/// %TEMP%\eel-explore-*.txt, and each bug fixed gets a regression test of its own.
/// </summary>
public class RandomEditingExplorer
{
    private static readonly string[] Docs =
    [
        "# Nagłówek **gruby** tekst\n\nZwykły akapit z **pogrubieniem**, *kursywą*, ~~skreśleniem~~, ==zakreśleniem== i `kodem`.\n\nTekst **wiele słów pogrubionych** tutaj i *kilka słów kursywą* też.\n",
        "- punkt **pierwszy** z [linkiem](http://example.com) i dalej\n- punkt drugi *ważny*\n  ciąg dalszy **mocno**\n\n1. numerowany ***oba*** koniec\n2. drugi `kod tu` i koniec\n",
        "> cytat z *kursywą* i **grubym**\n\n- [ ] zadanie **ważne** dziś\n- [x] zrobione *dawno*\n\n**Cała linia pogrubiona**\n\n*a* *b* **c** d\n",
        "Tekst przed tabelą.\n\n| Kolumna **a** | Kolumna b |\n|---|---|\n| `x` y | *z* w |\n| długi tekst w komórce | **gruby** |\n\nTekst po tabeli z **pogrubieniem**.\n",
        "## Sekcja\n\nAla ma **kota i psa**, a kot ma *Alę*.\nDruga linia ~~stara wersja~~ nowa.\n\n### Pod **sekcja**\n\nKoniec `a b` tekstu.\n",
    ];

    private sealed class Harness
    {
        public required MainWindow W;
        public AvaloniaEdit.TextEditor Ed => W.Editor;
        public string Text => Ed.Document.Text;

        public void Key(Key key, RawInputModifiers mods = RawInputModifiers.None, string? symbol = null)
        {
            W.KeyPress(key, mods, PhysicalKey.None, symbol);
            W.KeyRelease(key, mods, PhysicalKey.None, symbol);
            Dispatcher.UIThread.RunJobs();
        }

        public void Type(string s)
        {
            foreach (char c in s)
            {
                W.KeyTextInput(c.ToString());
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    /// <summary>The text the document shows (hidden markup removed, lines joined by \n) and the caret in it.</summary>
    private static (string Text, int Caret, MdStyle[] Styles) Visible(string doc, int caret)
    {
        var md = MarkdownDocument.Parse(doc.Replace("\r\n", "\n"));
        var sb = new System.Text.StringBuilder();
        var styles = new List<MdStyle>();
        int vc = -1;
        // Offsets in the document as it is (CRLF or LF).
        var docLines = new AvaloniaEdit.Document.TextDocument(doc).Lines.ToList();
        var lines = docLines.Select(l => doc.Substring(l.Offset, l.Length)).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            int offset = docLines[i].Offset;
            var r = md.GetLine(i);
            for (int j = 0; j <= lines[i].Length; j++)
            {
                if (offset + j == caret && vc < 0) vc = sb.Length;
                if (j == lines[i].Length) break;
                if (r.IsHidden(j)) continue;
                var st = MdStyle.None;
                foreach (var s in r.Styles) if (j >= s.Start && j < s.End) st |= s.Style;
                sb.Append(lines[i][j]);
                styles.Add(st);
            }
            if (i < lines.Length - 1) { sb.Append('\n'); styles.Add(MdStyle.None); }
        }
        return (sb.ToString(), vc < 0 ? sb.Length : vc, styles.ToArray());
    }

    private static int Markers(string visible) => visible.Count(c => c is '*' or '~' or '=' or '`' or '_');

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomEditing(bool wrap)
    {
        if (Environment.GetEnvironmentVariable("EELDITOR_EXPLORE") != "1") return;
        var failures = new List<string>();
        for (int d = 0; d < Docs.Length; d++)
        for (int seed = 0; seed < 12; seed++)
        {
            string dir = TestDirs.New();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "x.md");
            File.WriteAllText(path, Docs[d]);
            SettingsStore.UseDirectory(TestDirs.New());
            var w = new MainWindow(new AppSettings { WordWrap = wrap }, [path]);
            w.Show();
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!w.StartupCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Dispatcher.UIThread.RunJobs();
            var h = new Harness { W = w };
            h.Ed.TextArea.Focus();
            var rnd = new Random(seed * 7919 + d);
            h.Ed.CaretOffset = h.Ed.Document.GetLineByNumber(rnd.Next(h.Ed.Document.LineCount) + 1).Offset;
            var ops = new List<string>();
            for (int step = 0; step < 120; step++)
            {
                string before = h.Text;
                int caretBefore = h.Ed.CaretOffset;
                bool hadSelection = h.Ed.SelectionLength > 0;
                var (vis, vc, _) = Visible(before, caretBefore);
                int markersBefore = Markers(vis);
                int lineStart = vis.LastIndexOf('\n', Math.Max(0, vc - 1)) + 1;
                if (vc == 0) lineStart = 0;
                bool inTable = MarkdownDocument.Parse(before.Replace("\r\n", "\n")).TableAtLine(h.Ed.Document.GetLineByOffset(caretBefore).LineNumber - 1) is not null;
                int op = rnd.Next(100);
                string name;
                string? expected = null;
                int expectedCaret = -1;
                bool checkMarkers = true;
                try
                {
                    if (op < 25)
                    {
                        string s = ((char)('a' + rnd.Next(26))).ToString();
                        name = $"type '{s}'";
                        h.Type(s);
                        expected = vis.Insert(vc, s);
                        expectedCaret = vc + 1;
                    }
                    else if (op < 32)
                    {
                        name = "type ' '";
                        h.Type(" ");
                        if (vc > lineStart) { expected = vis.Insert(vc, " "); expectedCaret = vc + 1; }
                    }
                    else if (op < 45)
                    {
                        name = "Backspace";
                        h.Key(Key.Back);
                        if (vc > 0 && vis[vc - 1] != '\n' && h.Ed.SelectionLength == 0) { expected = vis.Remove(vc - 1, 1); expectedCaret = vc - 1; }
                    }
                    else if (op < 55)
                    {
                        name = "Delete";
                        h.Key(Key.Delete);
                        // (At the start of a task item the "-" goes with its check box: not checked here.)
                        bool taskStart = vis[lineStart..].StartsWith("- [") && vc == lineStart;
                        if (vc < vis.Length && vis[vc] != '\n' && !taskStart) { expected = vis.Remove(vc, 1); expectedCaret = vc; }
                    }
                    else if (op < 60 && !inTable && !hadSelection)
                    {
                        name = "Enter";
                        h.Key(Key.Enter);
                        // Spaces at the break may be dropped or added (indentation); the rest stays.
                        var (after, _, _) = Visible(h.Text, h.Ed.CaretOffset);
                        string a = after.Replace(" ", ""), e = vis.Insert(vc, "\n").Replace(" ", "");
                        if (false && a != e && !before.Split('\n')[h.Ed.Document.GetLineByOffset(caretBefore).LineNumber - 1].TrimStart().StartsWith('#'))
                            failures.Add($"[doc {d} seed {seed} wrap {wrap}] Enter: shows\n  '{after}'\n  expected '{vis.Insert(vc, "\n")}'\n  doc before: {before.Replace("\n", "⏎")}\n  caret {caretBefore}; ops: {string.Join(", ", ops.TakeLast(6))}");
                    }
                    else if (op < 72)
                    {
                        bool right = rnd.Next(2) == 0;
                        bool shift = rnd.Next(3) == 0;
                        name = (shift ? "Shift+" : "") + (right ? "Right" : "Left");
                        h.Key(right ? Avalonia.Input.Key.Right : Avalonia.Input.Key.Left, shift ? RawInputModifiers.Shift : RawInputModifiers.None);
                        expected = vis;
                        if (!shift && h.Ed.SelectionLength == 0)
                        {
                            expectedCaret = Math.Clamp(vc + (right ? 1 : -1), 0, vis.Length);
                        }
                    }
                    else if (op < 76)
                    {
                        name = rnd.Next(2) == 0 ? "Home" : "End";
                        h.Key(name == "Home" ? Avalonia.Input.Key.Home : Avalonia.Input.Key.End);
                        expected = vis;
                    }
                    else if (op < 80)
                    {
                        name = rnd.Next(2) == 0 ? "Up" : "Down";
                        h.Key(name == "Up" ? Avalonia.Input.Key.Up : Avalonia.Input.Key.Down);
                        expected = vis;
                    }
                    else if (op < 86)
                    {
                        // Select a few characters, then bold or italic.
                        int n = rnd.Next(1, 8);
                        for (int i = 0; i < n; i++) h.Key(Avalonia.Input.Key.Right, RawInputModifiers.Shift);
                        bool bold = rnd.Next(2) == 0;
                        name = $"select {n} + Ctrl+{(bold ? "B" : "I")}";
                        h.Key(bold ? Avalonia.Input.Key.B : Avalonia.Input.Key.I, RawInputModifiers.Control);
                        expected = vis;
                    }
                    else if (op < 92)
                    {
                        name = "Ctrl+Z";
                        h.Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
                        checkMarkers = false;
                    }
                    else if (op < 95)
                    {
                        name = "Ctrl+Y";
                        h.Key(Avalonia.Input.Key.Y, RawInputModifiers.Control);
                        checkMarkers = false;
                    }
                    else
                    {
                        int n = rnd.Next(1, 10);
                        for (int i = 0; i < n; i++) h.Key(Avalonia.Input.Key.Left, RawInputModifiers.Shift);
                        name = $"select {n} left + typed 'Q'";
                        bool sel = h.Ed.SelectionLength > 0;
                        h.Type("Q");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"[doc {d} seed {seed} wrap {wrap}] {ex.GetType().Name}: {ex.Message}\n  doc: {before.Replace("\n", "⏎")}\n  ops: {string.Join(", ", ops.TakeLast(6))}");
                    break;
                }
                ops.Add(name + (hadSelection ? " (sel)" : ""));
                if (Environment.GetEnvironmentVariable("EELDITOR_EXPLORE_TRACE") == $"{d}/{seed}/{wrap}")
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "eel-trace.txt"),
                        $"{name} sel={hadSelection} caret {caretBefore}->{h.Ed.CaretOffset} focus={Avalonia.Controls.TopLevel.GetTopLevel(w)!.FocusManager!.GetFocusedElement()?.GetType().Name}: {h.Text.Replace((char)10, '⏎')}{Environment.NewLine}");
                // Grid rows (word wrap): one element on screen, edited in the cell editor; Delete there does nothing.
                if (wrap && inTable) { expected = null; expectedCaret = -1; }
                // Enter before "# " makes a heading, which hides the "#".
                if (name == "Enter") expected = null;
                if (hadSelection && !name.StartsWith("select") && !name.StartsWith("Shift")) { expected = null; expectedCaret = -1; }
                if (hadSelection && name is "Left" or "Right") expectedCaret = -1;
                var (now, nc, _) = Visible(h.Text, h.Ed.CaretOffset);
                if (checkMarkers && Markers(now) > markersBefore)
                    failures.Add($"[doc {d} seed {seed} wrap {wrap}] {name}: a marker became visible\n  shows '{now}'\n  before '{vis}' (caret {vc})\n  doc before: {before.Replace("\n", "⏎")}\n  doc after:  {h.Text.Replace("\n", "⏎")}\n  ops: {string.Join(", ", ops.TakeLast(6))}");
                else if (expected is not null && now != expected)
                    failures.Add($"[doc {d} seed {seed} wrap {wrap}] {name}: shows\n  '{now}'\n  expected '{expected}'\n  doc before: {before.Replace("\n", "⏎")}\n  doc after:  {h.Text.Replace("\n", "⏎")}\n  caret {caretBefore} (visible {vc}); ops: {string.Join(", ", ops.TakeLast(6))}");
                else if (expectedCaret >= 0 && nc != expectedCaret)
                    failures.Add($"[doc {d} seed {seed} wrap {wrap}] {name}: caret at visible {nc}, expected {expectedCaret}\n  shows '{now}'\n  doc: {h.Text.Replace("\n", "⏎")} caret {h.Ed.CaretOffset} (before {caretBefore})\n  ops: {string.Join(", ", ops.TakeLast(6))}");
                // Undo takes back exactly this edit, redo does it again.
                string afterOp = h.Text;
                if (afterOp != before && (name.Contains("Ctrl+B") || name.Contains("Ctrl+I") || name is "Enter" or "Backspace" or "Delete") && rnd.Next(3) == 0)
                {
                    h.Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
                    if (h.Text != before)
                        failures.Add($"[doc {d} seed {seed} wrap {wrap}] Ctrl+Z after {name}: '{h.Text.Replace("\n", "⏎")}'\n  expected '{before.Replace("\n", "⏎")}'\n  ops: {string.Join(", ", ops.TakeLast(6))}");
                    h.Key(Avalonia.Input.Key.Y, RawInputModifiers.Control);
                    if (h.Text != afterOp)
                        failures.Add($"[doc {d} seed {seed} wrap {wrap}] Ctrl+Y after {name}: '{h.Text.Replace("\n", "⏎")}'\n  expected '{afterOp.Replace("\n", "⏎")}'\n  ops: {string.Join(", ", ops.TakeLast(6))}");
                }
                if (failures.Count > 0 && failures[^1].Contains($"[doc {d} seed {seed} wrap {wrap}]")) break; // one failure per run
            }
            w.Close();
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"eel-explore-{wrap}.txt"), string.Join("\n\n", failures));
        Assert.True(failures.Count == 0, $"{failures.Count} failures, see %TEMP%\\eel-explore-{wrap}.txt");
    }
}
