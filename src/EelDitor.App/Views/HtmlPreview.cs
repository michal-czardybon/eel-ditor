using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EelDitor.App.Editor;
using EelDitor.Core.Html;

namespace EelDitor.App.Views;

/// <summary>
/// A read-only preview of HTML drawn by Eel-ditor itself (no browser engine): <see cref="HtmlParser"/> builds the tree
/// and this control turns it into Avalonia text blocks, lists, tables, code blocks and local images. Scripts, styles
/// and remote content are never run or fetched; CSS is ignored except <c>text-align</c>.
/// </summary>
public sealed class HtmlPreview : Border
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _content;
    private Dictionary<string, Control> _anchors = new(StringComparer.Ordinal);

    /// <summary>A click on a link that is not an anchor in the page (the address as written).</summary>
    public event Action<string>? LinkActivated;

    public HtmlPreview()
    {
        _content = new StackPanel { Margin = new Thickness(20, 14, 20, 24) };
        _scroll = new ScrollViewer
        {
            Content = _content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Child = _scroll;
    }

    /// <summary>The controls of the rendered page (tests).</summary>
    internal StackPanel Content => _content;

    /// <summary>Renders <paramref name="html"/>; the scroll position is kept, so a live preview does not jump.</summary>
    public void Show(string html, string? baseDirectory, EditorPalette palette, double fontSize, FontFamily monoFont)
    {
        var offset = _scroll.Offset;
        Background = palette.Background;
        _content.Children.Clear();
        var renderer = new HtmlRenderer(palette, fontSize, monoFont, baseDirectory, Math.Max(200, Bounds.Width - 60), OnLink);
        var doc = HtmlParser.Parse(html);
        renderer.Render(doc.Find("body") ?? doc, _content);
        _anchors = renderer.Anchors;
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Background);
    }

    /// <summary>A line of dim text instead of a page (no preview for this file).</summary>
    public void ShowMessage(string message, EditorPalette palette)
    {
        Background = palette.Background;
        _content.Children.Clear();
        _anchors.Clear();
        _content.Children.Add(new TextBlock { Text = message, Foreground = palette.Dim, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic });
    }

    private void OnLink(string url)
    {
        if (url.StartsWith('#'))
        {
            if (_anchors.TryGetValue(Uri.UnescapeDataString(url[1..]), out var target)) target.BringIntoView();
            return;
        }
        LinkActivated?.Invoke(url);
    }
}

/// <summary>Turns a parsed HTML tree into controls. One instance per rendering.</summary>
internal sealed class HtmlRenderer
{
    internal readonly EditorPalette Palette;
    internal readonly double FontSize;
    internal readonly FontFamily MonoFont;
    internal readonly string? BaseDirectory;
    internal readonly double MaxImageWidth;
    internal readonly Action<string> OpenLink;

    public HtmlRenderer(EditorPalette palette, double fontSize, FontFamily mono, string? baseDirectory, double maxImageWidth, Action<string> openLink)
    {
        Palette = palette;
        FontSize = fontSize;
        MonoFont = mono;
        BaseDirectory = baseDirectory;
        MaxImageWidth = maxImageWidth;
        OpenLink = openLink;
    }


    /// <summary>Elements whose id/name can be the target of "#…" links, and the control drawn for them.</summary>
    public Dictionary<string, Control> Anchors { get; } = new(StringComparer.Ordinal);

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        { "head", "script", "style", "title", "template", "meta", "link", "base", "colgroup", "col", "param", "source", "track", "datalist" };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "body", "center", "details", "dialog", "dd", "div", "dl", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "html", "li",
        "main", "menu", "nav", "noscript", "ol", "p", "pre", "section", "summary", "table", "ul", "legend", "caption",
        "tbody", "thead", "tfoot", "tr", "td", "th", "textarea", "iframe", "video", "audio", "canvas", "svg", "object", "#document",
    };

    private static readonly double[] HeadingScale = [2.0, 1.5, 1.25, 1.1, 1.0, 0.9];

    /// <summary>Inline formatting in effect.</summary>
    private readonly record struct Style(
        bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, bool Mono = false,
        bool Mark = false, int Script = 0, double Scale = 1, string? Link = null, bool Dim = false);

    // Not new Style(): the parameterless struct constructor zeroes Scale.
    public void Render(HtmlElement root, Panel target) => RenderBlocks(root, target, new Style(Scale: 1), 0, ParagraphMargin);

    private Thickness ParagraphMargin => new(0, 0, 0, FontSize * 0.7);

    private Thickness TightMargin => new(0, 0, 0, 2);

    // ------------------------------------------------------------------ blocks

    /// <summary>The children of a block element: runs of inline content become paragraphs, block children are drawn in order.</summary>
    private void RenderBlocks(HtmlElement element, Panel target, Style style, int depth, Thickness paragraphMargin, TextAlignment align = TextAlignment.Left)
    {
        Paragraph? paragraph = null;
        Paragraph Current()
        {
            if (paragraph is null)
            {
                paragraph = new Paragraph(this, align) { Margin = paragraphMargin };
            }
            return paragraph;
        }
        void Flush()
        {
            paragraph?.AddTo(target);
            paragraph = null;
        }

        void Inline(HtmlElement e, Style s)
        {
            if (Skipped.Contains(e.Name)) return;
            if (Blocks.Contains(e.Name)) { Flush(); RenderBlock(e, target, s, depth); return; }
            var p = Current();
            p.Anchor(e, Anchors);
            switch (e.Name)
            {
                case "br": p.AddBreak(); return;
                case "wbr": return;
                case "img": p.AddControl(Image(e)); return;
                case "input": p.AddText(InputText(e), s); return;
                case "select": p.AddText("[" + (e.Elements.FirstOrDefault(o => o.Name == "option")?.InnerText.Trim() ?? "") + " ▾]", s); return;
                case "button": p.AddText("[", s); break;
                case "q": p.AddText("„", s); break;
            }
            var inner = InlineStyle(e, s);
            foreach (var child in e.Children)
            {
                if (child is HtmlText t) Current().AddText(t.Text, inner);
                else if (child is HtmlElement ce) Inline(ce, inner);
            }
            if (e.Name == "q") Current().AddText("”", s);
            if (e.Name == "button") Current().AddText("]", s);
        }

        foreach (var child in element.Children)
        {
            if (child is HtmlText t)
            {
                if (paragraph is null && string.IsNullOrWhiteSpace(t.Text)) continue;
                Current().AddText(t.Text, style);
            }
            else if (child is HtmlElement e) Inline(e, style);
        }
        Flush();
    }

    private void RenderBlock(HtmlElement e, Panel target, Style s, int depth)
    {
        Control? drawn = null;
        int before = target.Children.Count;
        var align = Alignment(e);
        switch (e.Name)
        {
            case "p":
                RenderBlocks(e, target, s, depth, ParagraphMargin, align);
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
            {
                int level = e.Name[1] - '1';
                var hs = s with { Bold = true, Scale = s.Scale * HeadingScale[level] };
                var panel = new StackPanel { Margin = new Thickness(0, level <= 1 ? FontSize * 0.8 : FontSize * 0.5, 0, FontSize * 0.4) };
                RenderBlocks(e, panel, hs, depth, new Thickness(0), align);
                if (level <= 1) panel.Children.Add(new Border { Height = 1, Background = Palette.Dim, Opacity = 0.4, Margin = new Thickness(0, 4, 0, 0) });
                target.Children.Add(drawn = panel);
                break;
            }
            case "ul" or "ol" or "menu":
                target.Children.Add(drawn = List(e, s, depth));
                break;
            case "li":
                target.Children.Add(drawn = ListRows([e], ordered: false, start: 1, s, depth));
                break;
            case "dl":
                RenderBlocks(e, target, s, depth, TightMargin);
                break;
            case "dt":
                RenderBlocks(e, target, s with { Bold = true }, depth, TightMargin, align);
                break;
            case "dd":
            {
                var panel = new StackPanel { Margin = new Thickness(FontSize * 2, 0, 0, FontSize * 0.4) };
                RenderBlocks(e, panel, s, depth, TightMargin, align);
                target.Children.Add(drawn = panel);
                break;
            }
            case "blockquote":
            {
                var inner = new StackPanel();
                RenderBlocks(e, inner, s with { Dim = true }, depth, ParagraphMargin, align);
                target.Children.Add(drawn = new Border
                {
                    BorderBrush = Palette.Dim, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2),
                    Margin = new Thickness(0, 0, 0, FontSize * 0.7), Child = inner,
                });
                break;
            }
            case "pre" or "textarea" or "xmp":
                target.Children.Add(drawn = CodeBlock(e.InnerText));
                break;
            case "hr":
                target.Children.Add(drawn = new Border { Height = 1, Background = Palette.Dim, Opacity = 0.6, Margin = new Thickness(0, FontSize * 0.6) });
                break;
            case "table":
                target.Children.Add(drawn = Table(e, s, depth));
                break;
            case "tr" or "td" or "th" or "tbody" or "thead" or "tfoot" or "caption":
                RenderBlocks(e, target, s, depth, TightMargin, align); // table parts outside a table
                break;
            case "summary" or "legend":
                RenderBlocks(e, target, s with { Bold = true }, depth, TightMargin, align);
                break;
            case "iframe" or "video" or "audio" or "canvas" or "svg" or "object":
            {
                string what = e.Attr("title") ?? e.Attr("src") ?? e.Attr("data") ?? "";
                target.Children.Add(drawn = new TextBlock
                {
                    Text = $"[{e.Name}{(what.Length > 0 ? ": " + what : "")}]", Foreground = Palette.Dim, FontStyle = FontStyle.Italic,
                    FontSize = FontSize, Margin = TightMargin, TextWrapping = TextWrapping.Wrap,
                });
                break;
            }
            default: // div, section, article, body, …
                RenderBlocks(e, target, s, depth, e.Name is "figcaption" ? TightMargin : ParagraphMargin, align);
                break;
        }
        drawn ??= target.Children.Count > before ? target.Children[before] : null;
        if (drawn is not null)
            foreach (var key in new[] { e.Attr("id"), e.Attr("name") })
                if (!string.IsNullOrEmpty(key)) Anchors.TryAdd(key, drawn);
    }

    private static TextAlignment Alignment(HtmlElement e)
    {
        string? a = e.Attr("align");
        if (a is null && e.Attr("style") is { } css && css.IndexOf("text-align", StringComparison.OrdinalIgnoreCase) is int k and >= 0)
            a = css[(k + 10)..].TrimStart(' ', ':').Split(';')[0].Trim();
        if (e.Name == "center") a = "center";
        return a?.ToLowerInvariant() switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
    }

    private Control List(HtmlElement list, Style s, int depth)
    {
        bool ordered = list.Name == "ol";
        int start = int.TryParse(list.Attr("start"), out int st) ? st : 1;
        var items = list.Elements.Where(c => !Skipped.Contains(c.Name)).ToList();
        return ListRows(items, ordered, start, s, depth);
    }

    private Grid ListRows(IReadOnlyList<HtmlElement> items, bool ordered, int start, Style s, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(FontSize * 0.4, 0, 0, depth == 0 ? FontSize * 0.7 : 0) };
        int n = start;
        string bullet = (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
        foreach (var item in items)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int row = grid.RowDefinitions.Count - 1;
            var content = new StackPanel();
            if (item.Name == "li")
            {
                string marker = ordered ? n++ + "." : bullet;
                var markerBlock = new TextBlock
                {
                    Text = marker, FontSize = FontSize, Foreground = Palette.Dim, Margin = new Thickness(0, 0, 8, 0),
                    MinWidth = ordered ? FontSize * 1.6 : FontSize * 0.9, TextAlignment = ordered ? TextAlignment.Right : TextAlignment.Left,
                };
                bool task = item.Attr("class")?.Contains("task", StringComparison.Ordinal) == true
                    || item.Elements.FirstOrDefault() is { Name: "input" } box && box.Attr("type")?.Equals("checkbox", StringComparison.OrdinalIgnoreCase) == true;
                if (!task) grid.Children.Add(markerBlock);
                Grid.SetRow(markerBlock, row);
                RenderBlocks(item, content, s, depth + 1, TightMargin, Alignment(item));
                foreach (var key in new[] { item.Attr("id"), item.Attr("name") })
                    if (!string.IsNullOrEmpty(key)) Anchors.TryAdd(key, content);
            }
            else RenderBlock(item, content, s, depth + 1);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
        }
        return grid;
    }

    private Control Table(HtmlElement table, Style s, int depth)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, FontSize * 0.7) };
        if (table.Elements.FirstOrDefault(e => e.Name == "caption") is { } caption)
            RenderBlocks(caption, panel, s with { Bold = true }, depth, TightMargin);

        var rows = new List<HtmlElement>();
        void Collect(HtmlElement e)
        {
            foreach (var c in e.Elements)
                if (c.Name == "tr") rows.Add(c);
                else if (c.Name is "thead" or "tbody" or "tfoot") Collect(c);
        }
        Collect(table);

        var grid = new Grid();
        var taken = new HashSet<(int, int)>();
        int columns = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int col = 0;
            foreach (var cell in rows[r].Elements.Where(c => c.Name is "td" or "th"))
            {
                while (taken.Contains((r, col))) col++;
                int colSpan = Math.Clamp(int.TryParse(cell.Attr("colspan"), out int cs) ? cs : 1, 1, 100);
                int rowSpan = Math.Clamp(int.TryParse(cell.Attr("rowspan"), out int rs) ? rs : 1, 1, 1000);
                for (int dr = 0; dr < rowSpan; dr++)
                    for (int dc = 0; dc < colSpan; dc++) taken.Add((r + dr, col + dc));
                bool header = cell.Name == "th" || cell.Parent?.Parent?.Name == "thead";
                var content = new StackPanel { MaxWidth = 480 };
                RenderBlocks(cell, content, header ? s with { Bold = true } : s, depth + 1, TightMargin, Alignment(cell));
                var border = new Border
                {
                    BorderBrush = Palette.Dim, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4),
                    Background = header ? Palette.CodeBackground : null, Child = content,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, col);
                Grid.SetRowSpan(border, rowSpan);
                Grid.SetColumnSpan(border, colSpan);
                grid.Children.Add(border);
                col += colSpan;
                columns = Math.Max(columns, col);
            }
        }
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        while (grid.RowDefinitions.Count < taken.Select(t => t.Item1 + 1).DefaultIfEmpty(0).Max()) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { BorderBrush = Palette.Dim, BorderThickness = new Thickness(1, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = grid },
        });
        return panel;
    }

    private Control CodeBlock(string text)
    {
        if (text.StartsWith("\r\n", StringComparison.Ordinal)) text = text[2..];
        else if (text.StartsWith('\n')) text = text[1..];
        text = text.TrimEnd('\r', '\n').Replace("\r\n", "\n");
        return new Border
        {
            Background = Palette.CodeBackground, CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, FontSize * 0.7),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock { Text = text, FontFamily = MonoFont, FontSize = FontSize, Foreground = Palette.Foreground, TextWrapping = TextWrapping.NoWrap },
            },
        };
    }

    // ------------------------------------------------------------------ inline

    private static Style InlineStyle(HtmlElement e, Style s) => e.Name switch
    {
        "b" or "strong" => s with { Bold = true },
        "i" or "em" or "cite" or "var" or "dfn" or "address" => s with { Italic = true },
        "u" or "ins" => s with { Underline = true },
        "s" or "strike" or "del" => s with { Strike = true },
        "code" or "kbd" or "samp" or "tt" => s with { Mono = true },
        "mark" => s with { Mark = true },
        "sub" => s with { Script = -1 },
        "sup" => s with { Script = 1 },
        "small" => s with { Scale = s.Scale * 0.85 },
        "big" => s with { Scale = s.Scale * 1.2 },
        "a" when e.Attr("href") is { Length: > 0 } href && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) => s with { Link = href.Trim() },
        _ => s,
    };

    private static string InputText(HtmlElement e)
    {
        string type = e.Attr("type")?.ToLowerInvariant() ?? "text";
        bool on = e.Attributes.ContainsKey("checked");
        return type switch
        {
            "checkbox" => on ? "☑ " : "☐ ",
            "radio" => on ? "◉ " : "○ ",
            "hidden" => "",
            "submit" or "button" or "reset" => "[" + (e.Attr("value") ?? type) + "]",
            _ => "[" + (e.Attr("value") ?? e.Attr("placeholder") ?? "") + "____]",
        };
    }

    private Control Image(HtmlElement e)
    {
        string src = e.Attr("src") ?? "";
        string alt = e.Attr("alt") ?? "";
        Bitmap? bitmap = DataImage(src) ?? (src.Length > 0 ? ImageCache.Load(BaseDirectory, src) : null);
        if (bitmap is null)
            return new TextBlock
            {
                Text = "[" + (alt.Length > 0 ? alt : Path.GetFileName(src)) + "]", Foreground = Palette.Dim, FontStyle = FontStyle.Italic, FontSize = FontSize,
            };
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = MaxImageWidth };
        if (double.TryParse(e.Attr("width")?.Replace("px", ""), out double w) && w > 0) image.Width = Math.Min(w, MaxImageWidth);
        if (double.TryParse(e.Attr("height")?.Replace("px", ""), out double h) && h > 0) image.Height = h;
        if (alt.Length > 0) ToolTip.SetTip(image, alt);
        return image;
    }

    /// <summary>A <c>data:image/…;base64,</c> source (decoded locally, nothing is fetched).</summary>
    private static Bitmap? DataImage(string src)
    {
        if (!src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
        int comma = src.IndexOf(',');
        if (comma < 0 || !src.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(src[(comma + 1)..]));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>One paragraph: text runs with their styles, line breaks and inline images; links are clickable.</summary>
    private sealed class Paragraph
    {
        private readonly HtmlRenderer _r;
        private readonly SelectableTextBlock _block;
        private readonly List<(int Start, int End, string Url)> _links = [];
        private int _length;
        private bool _lastSpace = true;
        private bool _hasContent;
        private readonly List<string> _anchors = [];

        public Thickness Margin { get => _block.Margin; init => _block.Margin = value; }

        /// <summary>Text positions a line break and an embedded control take in the layout (measured once).</summary>
        private static readonly int BreakLength = new InlineCollection { new LineBreak() }.Text?.Length ?? 1;

        public Paragraph(HtmlRenderer r, TextAlignment align)
        {
            _r = r;
            _block = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = r.FontSize, Foreground = r.Palette.Foreground, TextAlignment = align,
                Background = Brushes.Transparent, // without a background the block is not hit by clicks on links
                Inlines = [],
            };
            _block.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            _block.PointerMoved += OnMoved;
        }

        public void Anchor(HtmlElement e, Dictionary<string, Control> anchors)
        {
            foreach (var key in new[] { e.Attr("id"), e.Attr("name") })
                if (!string.IsNullOrEmpty(key) && !anchors.ContainsKey(key)) { anchors[key] = _block; _anchors.Add(key); }
        }

        public void AddText(string text, Style s)
        {
            if (text.Length == 0) return;
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    if (!_lastSpace) { sb.Append(' '); _lastSpace = true; }
                }
                else { sb.Append(c); _lastSpace = false; }
            }
            if (sb.Length == 0) return;
            var run = new Run(sb.ToString());
            Apply(run, s);
            _block.Inlines!.Add(run);
            if (s.Link is not null) _links.Add((_length, _length + run.Text!.Length, s.Link));
            _length += run.Text!.Length;
            if (sb.ToString().Trim().Length > 0) _hasContent = true;
        }

        public void AddBreak()
        {
            _block.Inlines!.Add(new LineBreak());
            _length += BreakLength;
            _lastSpace = true;
            _hasContent = true;
        }

        public void AddControl(Control control)
        {
            var container = new InlineUIContainer(control);
            _block.Inlines!.Add(container);
            _length += new InlineCollection { new InlineUIContainer(new Border()) }.Text?.Length ?? 1;
            _lastSpace = false;
            _hasContent = true;
        }

        public void AddTo(Panel target)
        {
            if (!_hasContent)
            {
                foreach (var key in _anchors) _r.Anchors.Remove(key);
                return;
            }
            target.Children.Add(_block);
        }

        private void Apply(Run run, Style s)
        {
            double size = _r.FontSize * s.Scale * (s.Script != 0 ? 0.75 : 1);
            run.FontSize = size;
            if (s.Bold) run.FontWeight = FontWeight.Bold;
            if (s.Italic) run.FontStyle = FontStyle.Italic;
            if (s.Mono)
            {
                run.FontFamily = _r.MonoFont;
                run.Background = _r.Palette.CodeBackground;
            }
            if (s.Mark) run.Background = _r.Palette.Highlight;
            if (s.Script != 0) run.BaselineAlignment = s.Script > 0 ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
            if (s.Dim) run.Foreground = _r.Palette.Dim;
            var decorations = new TextDecorationCollection();
            if (s.Underline || s.Link is not null) decorations.AddRange(TextDecorations.Underline);
            if (s.Strike) decorations.AddRange(TextDecorations.Strikethrough);
            if (decorations.Count > 0) run.TextDecorations = decorations;
            if (s.Link is not null) run.Foreground = _r.Palette.Accent;
        }

        /// <summary>The link at a point of the block, or null.</summary>
        internal string? LinkAt(Point point)
        {
            // The rectangles of each link's text, not HitTestPoint: after a line break it reports clicks on the text as
            // outside, and its nearest position would also catch clicks past the end of a line.
            if (_links.Count == 0) return null;
            var layout = _block.TextLayout;
            foreach (var (start, end, url) in _links)
                foreach (var rect in layout.HitTestTextRange(start, end - start))
                    if (rect.Inflate(1).Contains(point)) return url;
            return null;
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _block.SelectionStart != _block.SelectionEnd) return;
            if (LinkAt(e.GetPosition(_block)) is { } url) _r.OpenLink(url);
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            string? url = LinkAt(e.GetPosition(_block));
            _block.Cursor = url is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(_block, url);
        }
    }
}
