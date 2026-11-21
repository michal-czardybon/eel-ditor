namespace EelDitor.Markdown;

[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
    Link = 16,
    Heading = 32,
    /// <summary><c>==text==</c> (Obsidian, Typora, markdown-it).</summary>
    Highlight = 64,
    /// <summary>Footnote reference <c>[^1]</c> or the label of a definition <c>[^1]:</c>.</summary>
    Footnote = 128,
    /// <summary>Alt text (or path) of an image <c>![alt](path)</c>.</summary>
    Image = 256,
    /// <summary>Text of a completed task <c>- [x] …</c>.</summary>
    TaskDone = 512,
    /// <summary><c>~x~</c> (Pandoc, markdown-it) or <c>&lt;sub&gt;x&lt;/sub&gt;</c>.</summary>
    Subscript = 1024,
    /// <summary><c>^x^</c> (Pandoc, markdown-it) or <c>&lt;sup&gt;x&lt;/sup&gt;</c>.</summary>
    Superscript = 2048,
}

/// <summary>An image: <see cref="AnchorStart"/>/<see cref="AnchorLength"/> is the hidden tail (<c>](path)</c>) where the preview is drawn.</summary>
public readonly record struct ImageRef(int AnchorStart, int AnchorLength, string Path);

public enum MdLineKind
{
    Normal,
    Heading,
    FenceDelimiter,
    Code,
    TableHeader,
    TableSeparator,
    TableBody,
    /// <summary>A horizontal rule: <c>---</c>, <c>***</c>, <c>___</c> (not a setext heading underline, not front matter).</summary>
    Rule,
}

/// <summary>A run of source characters that is not displayed (markup).</summary>
public readonly record struct HiddenRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>A range of visible characters with a style.</summary>
public readonly record struct StyledRange(int Start, int End, MdStyle Style, string? Url = null);

/// <summary>Virtual characters displayed before source column <see cref="Offset"/>, not present in the document.</summary>
public readonly record struct Padding(int Offset, int Width, char Fill);

/// <summary>Everything needed to display one source line. All offsets are relative to the line start.</summary>
public sealed class LineRender
{
    public static readonly LineRender Plain = new() { Kind = MdLineKind.Normal };

    public MdLineKind Kind { get; init; }
    public int HeadingLevel { get; init; }
    public List<HiddenRange> Hidden { get; } = [];
    public List<StyledRange> Styles { get; } = [];
    public List<Padding> Paddings { get; } = [];
    public List<ImageRef> Images { get; } = [];

    /// <summary>HTML line breaks <c>&lt;br&gt;</c> (also <c>&lt;br/&gt;</c>, <c>&lt;br /&gt;</c>): a new line inside a table cell.</summary>
    public List<HiddenRange> Breaks { get; } = [];

    /// <summary>The line break tag starting at <paramref name="column"/>, if any.</summary>
    public HiddenRange? BreakAt(int column)
    {
        foreach (var b in Breaks)
            if (b.Start == column) return b;
        return null;
    }

    /// <summary>Column of a task box <c>[ ]</c>/<c>[x]</c> (three characters), or -1.</summary>
    public int TaskColumn { get; init; } = -1;

    /// <summary>Where the drawn task box starts: the bullet (<c>-</c>, <c>*</c>, <c>+</c>), which it replaces, or the box itself after a number.</summary>
    public int TaskStart { get; init; } = -1;

    public bool TaskChecked { get; init; }

    /// <summary>Label of a footnote definition on this line (<c>[^label]: …</c>), or null.</summary>
    public string? FootnoteDefinition { get; init; }

    /// <summary>Index into <see cref="MarkdownDocument.Tables"/> when the line is a table row, otherwise -1.</summary>
    public int TableIndex { get; init; } = -1;

    public bool IsTableRow => Kind is MdLineKind.TableHeader or MdLineKind.TableSeparator or MdLineKind.TableBody;

    /// <summary>The string the user sees: hidden ranges removed and paddings inserted. Used by tests.</summary>
    public string Display(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length + 16);
        int pi = 0;
        var paddings = Paddings.OrderBy(p => p.Offset).ToList();
        for (int i = 0; i <= line.Length; i++)
        {
            while (pi < paddings.Count && paddings[pi].Offset == i)
            {
                sb.Append(paddings[pi].Fill, paddings[pi].Width);
                pi++;
            }
            if (i == line.Length) break;
            if (!IsHidden(i)) sb.Append(line[i]);
        }
        return sb.ToString();
    }

    public bool IsHidden(int column)
    {
        foreach (var h in Hidden)
            if (column >= h.Start && column < h.End) return true;
        return false;
    }

    public int HiddenCount(int start, int end)
    {
        int n = 0;
        foreach (var h in Hidden)
        {
            int s = Math.Max(start, h.Start), e = Math.Min(end, h.End);
            if (e > s) n += e - s;
        }
        return n;
    }
}

public enum ColumnAlignment { None, Left, Center, Right }

/// <summary>One cell of a table row. Offsets are relative to the line start.</summary>
public sealed record TableCell(int Start, int End, int ClosingPipe)
{
    /// <summary>Position of the pipe closing this cell, or -1 when the row has no trailing pipe.</summary>
    public bool HasClosingPipe => ClosingPipe >= 0;

    public int ContentStart(string line)
    {
        int i = Start;
        while (i < End && line[i] is ' ' or '\t') i++;
        return i;
    }

    public int ContentEnd(string line)
    {
        int i = End;
        while (i > Start && line[i - 1] is ' ' or '\t') i--;
        return i;
    }
}

public sealed class TableRow
{
    public required int LineIndex { get; init; }
    public required MdLineKind Kind { get; init; }
    public required bool HasLeadingPipe { get; init; }

    /// <summary>Offset of the leading pipe (or -1).</summary>
    public int LeadingPipe { get; init; } = -1;

    public List<TableCell> Cells { get; } = [];
}

public sealed class MarkdownTable
{
    public required int FirstLine { get; init; }
    public List<TableRow> Rows { get; } = [];
    public List<ColumnAlignment> Alignments { get; } = [];

    public int LastLine => FirstLine + Rows.Count - 1;
    public int ColumnCount => Alignments.Count;
    public TableRow Header => Rows[0];
    public TableRow Separator => Rows[1];

    public TableRow? RowAtLine(int line) =>
        line >= FirstLine && line <= LastLine ? Rows[line - FirstLine] : null;
}

/// <summary>Source of document lines (text without terminators).</summary>
public interface ILineSource
{
    int LineCount { get; }
    string GetLine(int index);
}

public sealed class StringLineSource : ILineSource
{
    private readonly string[] _lines;

    public StringLineSource(string text) =>
        _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public int LineCount => _lines.Length;
    public string GetLine(int index) => _lines[index];
}
