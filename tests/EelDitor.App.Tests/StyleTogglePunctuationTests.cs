using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using EelDitor.App.Editor;
using EelDitor.Markdown;

namespace EelDitor.App.Tests;

/// <summary>
/// Ctrl+B / Ctrl+I / … on a selection that starts or ends with punctuation ("**Uwaga:**"): the style comes on, and
/// the same selection takes it off again, back to the text as it was.
/// </summary>
public class StyleTogglePunctuationTests
{
    private static readonly (string Marker, MdStyle Style)[] Styles =
        [("**", MdStyle.Bold), ("*", MdStyle.Italic), ("~~", MdStyle.Strike), ("==", MdStyle.Highlight), ("`", MdStyle.Code)];

    // Not ">": at the start of a line it opens a quote, which is not what these cases are about.
    private static readonly string[] Punctuation = [":", ".", ",", ";", "!", "?", ")", "\"", "'", "…", "—", "%", "]", "-", "/", "*", "_", "~", "=", "<"];

    private static readonly string[] Contexts = ["{0}", "{0} dalej", "Przed {0}", "Przed {0} dalej", "- {0} dalej", "## {0}", "> {0} dalej", "1. {0}"];

    /// <summary>Toggles the style on the source range [start, end) as the editor command does; returns the new line.</summary>
    private static string Toggle(string line, int start, int end, string marker, MdStyle style)
    {
        var editor = new TextEditor { Document = new TextDocument(line), WordWrap = false };
        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
        editor.Select(start, end - start);
        MarkdownCommands.ToggleStyle(editor, view, marker, style);
        string result = editor.Document.Text;
        view.Dispose();
        return result;
    }

    /// <summary>The source range of the visible text <paramref name="shown"/> (markers excluded).</summary>
    private static (int Start, int End) RangeOf(string line, string shown)
    {
        var render = MarkdownDocument.Parse(line).GetLine(0);
        var (text, _) = InlineStyleEdit.Visible(line, render);
        int v = text.IndexOf(shown, StringComparison.Ordinal);
        Assert.True(v >= 0, $"'{shown}' not shown in '{line}' (shows '{text}')");
        int start = -1, end = -1, seen = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (render.IsHidden(i)) continue;
            if (seen == v) start = i;
            seen++;
            if (seen == v + shown.Length) { end = i + 1; break; }
        }
        return (start, end);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (marker, _) in Styles)
            foreach (string p in Punctuation)
                foreach (string context in Contexts)
                {
                    yield return [marker, context, "Uwaga" + p];   // punctuation at the end ("**Uwaga:**")
                    yield return [marker, context, p + "Uwaga"];   // and at the start
                }
    }

    [AvaloniaFact]
    public void StyleOnAndOff_AroundPunctuation_ReturnsToTheOriginal()
    {
        var failures = new List<string>();
        foreach (var c in Cases())
        {
            string marker = (string)c[0], context = (string)c[1], word = (string)c[2];
            var style = Styles.First(s => s.Marker == marker).Style;
            string original = string.Format(context, word);
            var (a, b) = RangeOf(original, word);
            string on = Toggle(original, a, b, marker, style);
            var renderOn = MarkdownDocument.Parse(on).GetLine(0);
            var (shownOn, stylesOn) = InlineStyleEdit.Visible(on, renderOn);
            var (shownOriginal, _) = InlineStyleEdit.Visible(original, MarkdownDocument.Parse(original).GetLine(0));
            if (on == original) continue; // the style could not be added safely: left as it is (allowed)
            if (shownOn != shownOriginal) { failures.Add($"{marker} on changed the text: [{original}] -> [{on}]"); continue; }
            var (a2, b2) = RangeOf(on, word);
            if (!stylesOn[shownOn.IndexOf(word, StringComparison.Ordinal)..][..word.Length].All(s => s.HasFlag(style)))
            {
                failures.Add($"{marker} on did not style all of '{word}': [{original}] -> [{on}]");
                continue;
            }
            string off = Toggle(on, a2, b2, marker, style);
            if (off != original) failures.Add($"{marker} off: [{original}] -> on [{on}] -> off [{off}]");
        }
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Take(60)));
    }

    /// <summary>
    /// The same span selected in the ways a user selects it: the shown text only, with the hidden markers on one or
    /// both sides (the caret steps over them, so a mouse or Shift+End selection often takes them), the whole line.
    /// </summary>
    [AvaloniaFact]
    public void StyleOff_WhateverPartOfTheMarkersIsSelected()
    {
        var failures = new List<string>();
        foreach (var (marker, style) in Styles)
            foreach (string p in Punctuation.Where(p => p != ">"))
                foreach (string context in Contexts)
                    foreach (string word in new[] { "Uwaga" + p, p + "Uwaga", "Uwaga" })
                    {
                        string plain = string.Format(context, word);
                        var (a, b) = RangeOf(plain, word);
                        string styled = Toggle(plain, a, b, marker, style);
                        if (styled == plain) continue;
                        var (s, e) = RangeOf(styled, word);
                        int m = marker.Length;
                        var selections = new (string Name, int Start, int End)[]
                        {
                            ("text", s, e), ("markers before", s - m, e), ("markers after", s, e + m),
                            ("both markers", s - m, e + m), ("whole line", 0, styled.Length),
                        };
                        foreach (var (name, from, to) in selections)
                        {
                            if (name == "whole line" && context != "{0}") continue; // other words on the line would be restyled
                            if (from < 0 || to > styled.Length) continue;
                            string off = Toggle(styled, from, to, marker, style);
                            if (off != plain) failures.Add($"{marker} off ({name}): [{styled}] -> [{off}], expected [{plain}]");
                        }
                    }
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Take(60)));
    }

    /// <summary>Toggles on a multi-line document (a table): range in absolute offsets; grid = word wrap on.</summary>
    private static string ToggleIn(string text, int start, int end, string marker, MdStyle style, bool grid)
    {
        var editor = new TextEditor { Document = new TextDocument(text), WordWrap = grid };
        var window = new Avalonia.Controls.Window { Width = 900, Height = 500, Content = editor };
        window.Show();
        var view = new MarkdownView(editor, editor.Document, EditorPalette.Dark);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        editor.Select(start, end - start);
        MarkdownCommands.ToggleStyle(editor, view, marker, style);
        string result = editor.Document.Text;
        view.Dispose();
        window.Close();
        return result;
    }

    [AvaloniaFact]
    public void StyleOff_InTablesAndLongerText()
    {
        var failures = new List<string>();
        string[] words = ["Uwaga:", "Ważna uwaga:", "Uwaga: tak", "Krok 1:", "Uwaga (pilne):", "Uwaga:)", "x:"];
        string[] lines = ["{0}", "**Inne** i {0} dalej", "{0} oraz **inne**", "| {0} | b |", "| a | {0} |", "| a | {0} b |"];
        foreach (bool grid in new[] { false, true })
            foreach (string w in words)
                foreach (string l in lines)
                {
                    bool table = l.StartsWith('|');
                    string prefix = table ? "| h1 | h2 |\n|---|---|\n" : "";
                    string line = string.Format(l, "**" + w + "**");
                    string doc = prefix + line + "\n";
                    int lineStart = prefix.Length;
                    int s0 = lineStart + line.IndexOf("**" + w + "**", StringComparison.Ordinal) + 2, e0 = s0 + w.Length;
                    string expected = prefix + string.Format(l, w) + "\n";
                    foreach (var (name, from, to) in new[] { ("text", s0, e0), ("with markers", s0 - 2, e0 + 2), ("markers after", s0, e0 + 2) })
                    {
                        string off = ToggleIn(doc, from, to, "**", MdStyle.Bold, grid);
                        if (off != expected) failures.Add($"grid={grid} ({name}): [{line}] -> [{off.Replace("\n", "⏎")}]");
                    }
                }
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Take(60)));
    }

    /// <summary>
    /// The reported line: bold words and a plain colon after them, selected together to the end of the line. The
    /// colon does not decide: the bold comes off. (Before, Ctrl+B added bold and wrote "****…**:**", which looked like
    /// nothing happened.)
    /// </summary>
    [AvaloniaTheory]
    [InlineData("adjective", -1, "This also affects adjective agreement in the plural:")]   // from the word to the line end
    [InlineData("**adjective", -1, "This also affects adjective agreement in the plural:")] // from the markers
    [InlineData("affects", -1, "This also **affects adjective agreement in the plural:**")] // partly bold: all becomes bold, cleanly
    public void SelectionWithPlainPunctuation_AroundBoldWords(string from, int to, string expected)
    {
        const string line = "This also affects **adjective agreement in the plural**:";
        int a = line.IndexOf(from, StringComparison.Ordinal);
        Assert.Equal(expected, Toggle(line, a, to < 0 ? line.Length : to, "**", MdStyle.Bold));
    }

    /// <summary>Bold written as __ and italic as _ (files from elsewhere): Ctrl+B / Ctrl+I take them off too.</summary>
    [AvaloniaTheory]
    [InlineData("__Uwaga:__ dalej", "Uwaga:", "**", "Uwaga: dalej")]
    [InlineData("__Uwaga__ dalej", "Uwaga", "**", "Uwaga dalej")]
    [InlineData("Przed __Ważna uwaga:__", "uwaga", "**", "Przed __Ważna__ uwaga:")]
    [InlineData("_tak_ dalej", "tak", "*", "tak dalej")]
    [InlineData("*__Uwaga:__* dalej", "Uwaga:", "**", "*Uwaga:* dalej")]
    public void UnderscoreMarkers_ComeOff(string line, string selected, string marker, string expected)
    {
        var (a, b) = RangeOf(line, selected);
        Assert.Equal(expected, Toggle(line, a, b, marker, marker == "**" ? MdStyle.Bold : MdStyle.Italic));
    }

    /// <summary>The reported case: bold text ending with a colon, selected and Ctrl+B pressed.</summary>
    [AvaloniaTheory]
    [InlineData("**Uwaga:** dalej", "Uwaga:", "Uwaga: dalej")]
    [InlineData("**Uwaga:**", "Uwaga:", "Uwaga:")]
    [InlineData("Tekst **Uwaga:**", "Uwaga:", "Tekst Uwaga:")]
    // A double click selects the word without the colon: "Uwaga**:**" cannot stay bold (a ** between a letter and
    // punctuation opens nothing), so the colon loses the style too.
    [InlineData("**Uwaga:** dalej", "Uwaga", "Uwaga: dalej")]
    [InlineData("**(Uwaga)** dalej", "Uwaga", "(Uwaga) dalej")]
    [InlineData("**Uwaga!?** dalej", "Uwaga", "Uwaga!? dalej")]
    [InlineData("**Ważna uwaga:** dalej", "uwaga", "**Ważna** uwaga: dalej")]
    [InlineData("**Ważna uwaga:** dalej", "Ważna", "Ważna **uwaga:** dalej")]
    public void BoldEndingWithAColon_ComesOff(string line, string selected, string expected)
    {
        var (a, b) = RangeOf(line, selected);
        Assert.Equal(expected, Toggle(line, a, b, "**", MdStyle.Bold));
    }
}
