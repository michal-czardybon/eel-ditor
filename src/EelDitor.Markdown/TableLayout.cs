namespace EelDitor.Markdown;

/// <summary>Computes virtual padding that visually aligns table columns without editing the source.</summary>
public static class TableLayout
{
    public static void ComputePaddings(MarkdownTable table, ILineSource source, LineRender[] lines)
    {
        int columns = table.Rows.Max(r => r.Cells.Count);
        var widths = new int[columns];
        int prefixWidth = 0;

        foreach (var row in table.Rows)
        {
            string text = source.GetLine(row.LineIndex);
            var render = lines[row.LineIndex];
            if (row.HasLeadingPipe) prefixWidth = Math.Max(prefixWidth, TextWidth.Of(text.AsSpan(0, row.LeadingPipe)));
            for (int c = 0; c < row.Cells.Count; c++)
            {
                if (!row.Cells[c].HasClosingPipe) continue;
                widths[c] = Math.Max(widths[c], CellWidth(text, row.Cells[c], render));
            }
        }

        foreach (var row in table.Rows)
        {
            string text = source.GetLine(row.LineIndex);
            var render = lines[row.LineIndex];

            if (row.HasLeadingPipe)
            {
                int w = TextWidth.Of(text.AsSpan(0, row.LeadingPipe));
                if (w < prefixWidth) render.Paddings.Add(new Padding(row.LeadingPipe, prefixWidth - w, ' '));
            }

            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                if (!cell.HasClosingPipe) continue;
                int missing = widths[c] - CellWidth(text, cell, render);
                if (missing <= 0) continue;

                if (row.Kind == MdLineKind.TableSeparator)
                {
                    int lastDash = text.LastIndexOf('-', cell.End - 1, cell.End - cell.Start);
                    int at = lastDash >= cell.Start ? lastDash + 1 : cell.End;
                    render.Paddings.Add(new Padding(at, missing, '-'));
                    continue;
                }

                int contentStart = cell.ContentStart(text), contentEnd = cell.ContentEnd(text);
                bool empty = contentStart >= contentEnd;
                var align = c < table.Alignments.Count ? table.Alignments[c] : ColumnAlignment.None;
                if (empty)
                {
                    render.Paddings.Add(new Padding(cell.End, missing, ' '));
                }
                else if (align == ColumnAlignment.Right)
                {
                    render.Paddings.Add(new Padding(contentStart, missing, ' '));
                }
                else if (align == ColumnAlignment.Center)
                {
                    int left = missing / 2;
                    if (left > 0) render.Paddings.Add(new Padding(contentStart, left, ' '));
                    render.Paddings.Add(new Padding(contentEnd, missing - left, ' '));
                }
                else
                {
                    render.Paddings.Add(new Padding(contentEnd, missing, ' '));
                }
            }
        }
    }

    /// <summary>Displayed width of a cell: source width minus hidden markup.</summary>
    public static int CellWidth(string line, TableCell cell, LineRender render) =>
        TextWidth.Of(line.AsSpan(cell.Start, cell.End - cell.Start)) - render.HiddenCount(cell.Start, cell.End);
}
