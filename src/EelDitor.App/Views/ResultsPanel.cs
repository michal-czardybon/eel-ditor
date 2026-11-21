using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EelDitor.App.Services;
using EelDitor.Core.Search;

namespace EelDitor.App.Views;

/// <summary>A row in the results table: either a file header or a hit.</summary>
public sealed class ResultRow
{
    public string? FileHeader { get; init; }
    public int Count { get; set; }
    public SearchHit? Hit { get; init; }
}

public sealed class ResultSet
{
    public required string Title { get; init; }
    public ObservableCollection<ResultRow> Rows { get; } = [];
    public int HitCount { get; set; }
    public override string ToString() => Title;
}

/// <summary>
/// Results table shown at the bottom of the window for "Find all" and "Find in files".
/// A flat virtualized list (file header rows + hit rows) stays fast with tens of thousands of hits.
/// </summary>
public sealed class ResultsPanel : DockPanel
{
    private readonly ListBox _list = new();
    private readonly ComboBox _history = new() { MinWidth = 280, FontSize = 12 };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), FontSize = 12 };
    private readonly ObservableCollection<ResultSet> _sets = [];
    private readonly Dictionary<string, ResultRow> _headers = new(StringComparer.OrdinalIgnoreCase);

    public ResultsPanel()
    {
        var close = new Button { Content = "✕", Padding = new Thickness(8, 0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var clear = new Button { Content = Loc.T("Results.Clear"), Padding = new Thickness(8, 0), FontSize = 12, Background = Brushes.Transparent };
        clear.Click += (_, _) =>
        {
            if (_history.SelectedItem is ResultSet s) _sets.Remove(s);
            _history.SelectedIndex = _sets.Count - 1;
            if (_sets.Count == 0) { _list.ItemsSource = null; _summary.Text = ""; }
        };
        _history.ItemsSource = _sets;
        _history.SelectionChanged += (_, _) =>
        {
            if (_history.SelectedItem is ResultSet s) { _list.ItemsSource = s.Rows; UpdateSummary(s); }
        };

        var header = new DockPanel { Height = 26 }.Res(BackgroundProperty, "ChromeBrush");
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = Loc.T("Results.Title"), FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) }, _history, _summary },
        });
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        _list.Background = Brushes.Transparent;
        _list.FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, Cascadia Mono, monospace");
        _list.FontSize = 12;
        _list.ItemTemplate = new FuncDataTemplate<ResultRow>((row, _) => BuildRow(row), supportsRecycling: false);
        _list.DoubleTapped += (_, _) => Navigate();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(); e.Handled = true; } };
        Children.Add(_list);
    }

    public event Action<SearchHit>? HitActivated;
    public event Action? CloseRequested;

    public ResultSet? Current => _history.SelectedItem as ResultSet;

    public ResultSet Begin(string title)
    {
        var set = new ResultSet { Title = title };
        _headers.Clear();
        _sets.Add(set);
        while (_sets.Count > 15) _sets.RemoveAt(0);
        _history.SelectedItem = set;
        _list.ItemsSource = set.Rows;
        UpdateSummary(set);
        return set;
    }

    public void AddHits(ResultSet set, IReadOnlyList<SearchHit> hits)
    {
        foreach (var group in hits.GroupBy(h => h.FilePath ?? h.DocumentKey?.ToString() ?? ""))
        {
            string key = group.Key;
            if (!_headers.TryGetValue(key, out var header))
            {
                header = new ResultRow { FileHeader = group.First().FilePath ?? key };
                _headers[key] = header;
                set.Rows.Add(header);
            }
            int insertAt = set.Rows.IndexOf(header) + 1 + header.Count;
            foreach (var h in group)
            {
                set.Rows.Insert(insertAt++, new ResultRow { Hit = h });
                header.Count++;
                set.HitCount++;
            }
            // Refresh header text with the new count.
            int hi = set.Rows.IndexOf(header);
            set.Rows[hi] = new ResultRow { FileHeader = header.FileHeader, Count = header.Count };
            _headers[key] = set.Rows[hi];
        }
        UpdateSummary(set);
    }

    public void Finish(ResultSet set, string summary)
    {
        _summary.Text = summary;
    }

    private void UpdateSummary(ResultSet set) =>
        _summary.Text = Loc.T("Results.Summary", set.HitCount, set.Rows.Count(r => r.FileHeader is not null));

    /// <summary>Moves the selection to the next/previous hit and activates it (F4 / Shift+F4).</summary>
    public void Step(int direction)
    {
        if (_list.ItemsSource is not IList<ResultRow> rows || rows.Count == 0) return;
        int i = _list.SelectedIndex;
        for (int n = 0; n < rows.Count; n++)
        {
            i = ((i + direction) % rows.Count + rows.Count) % rows.Count;
            if (rows[i].Hit is not null) break;
        }
        _list.SelectedIndex = i;
        _list.ScrollIntoView(i);
        Navigate();
    }

    private void Navigate()
    {
        if (_list.SelectedItem is ResultRow { Hit: { } hit }) HitActivated?.Invoke(hit);
    }

    private static Control BuildRow(ResultRow row)
    {
        if (row.FileHeader is not null)
        {
            return new TextBlock
            {
                Text = $"{row.FileHeader}  ({row.Count})",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
            }.Res(TextBlock.ForegroundProperty, "AccentTextBrush");
        }
        var hit = row.Hit!;
        var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        tb.Inlines!.Add(new Run($"{hit.Line,7}:{hit.Column,-5} ").Res(TextElement.ForegroundProperty, "DimTextBrush"));
        string text = hit.LineText;
        int s = Math.Clamp(hit.LineMatchStart, 0, text.Length), l = Math.Clamp(hit.LineMatchLength, 0, text.Length - s);
        tb.Inlines.Add(new Run(text[..s].TrimStart()));
        tb.Inlines.Add(new Run(text.Substring(s, l)) { FontWeight = FontWeight.Bold }.Res(TextElement.ForegroundProperty, "AccentTextBrush"));
        tb.Inlines.Add(new Run(text[(s + l)..]));
        return tb;
    }
}
