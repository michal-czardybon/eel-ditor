using System.Net;
using System.Text;

namespace EelDitor.Core.Html;

/// <summary>A node of a parsed HTML document.</summary>
public abstract class HtmlNode
{
    public HtmlElement? Parent { get; internal set; }
}

/// <summary>Text with entities decoded; whitespace as written.</summary>
public sealed class HtmlText(string text) : HtmlNode
{
    public string Text { get; internal set; } = text;

    public override string ToString() => Text;
}

/// <summary>An element: lower-case name, attributes (lower-case names, decoded values), children.</summary>
public sealed class HtmlElement(string name) : HtmlNode
{
    public string Name { get; } = name;
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<HtmlNode> Children { get; } = [];

    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : null;

    public IEnumerable<HtmlElement> Elements => Children.OfType<HtmlElement>();

    /// <summary>All descendant elements, depth first.</summary>
    public IEnumerable<HtmlElement> Descendants()
    {
        foreach (var e in Elements)
        {
            yield return e;
            foreach (var d in e.Descendants()) yield return d;
        }
    }

    /// <summary>The first descendant (or this element) with the given name.</summary>
    public HtmlElement? Find(string name) => Name == name ? this : Descendants().FirstOrDefault(e => e.Name == name);

    /// <summary>The text of all descendants, as written (no whitespace collapsing).</summary>
    public string InnerText
    {
        get
        {
            var sb = new StringBuilder();
            void Walk(HtmlNode n)
            {
                if (n is HtmlText t) sb.Append(t.Text);
                else if (n is HtmlElement e) foreach (var c in e.Children) Walk(c);
            }
            Walk(this);
            return sb.ToString();
        }
    }

    internal void Add(HtmlNode node)
    {
        node.Parent = this;
        if (node is HtmlText t && Children.Count > 0 && Children[^1] is HtmlText last) last.Text += t.Text;
        else Children.Add(node);
    }

    public override string ToString() => $"<{Name}>";
}

/// <summary>
/// A forgiving HTML parser for previews: tags, attributes, entities, comments, void elements, raw-text elements
/// (script, style, textarea, title) and the usual implied end tags (p, li, dt/dd, tr, td/th, option, table sections).
/// It never throws; any input gives a tree. It does not run scripts, apply CSS or fetch anything.
/// </summary>
public static class HtmlParser
{
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "textarea", "title", "xmp" };

    /// <summary>Elements that end an open paragraph when they start.</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer",
        "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
    };

    /// <summary>Parses <paramref name="html"/> into a tree under a <c>#document</c> element.</summary>
    public static HtmlElement Parse(string html)
    {
        var root = new HtmlElement("#document");
        var stack = new List<HtmlElement> { root };
        HtmlElement Current() => stack[^1];
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0) return;
            Current().Add(new HtmlText(WebUtility.HtmlDecode(text.ToString())));
            text.Clear();
        }

        // Pops up to and including the innermost open element named one of <paramref name="names"/>, but not past a
        // boundary element (e.g. an <li> inside a nested <ul> must not close the outer one).
        bool CloseOpen(string[] names, string[] boundaries)
        {
            for (int k = stack.Count - 1; k > 0; k--)
            {
                string n = stack[k].Name;
                if (names.Contains(n)) { stack.RemoveRange(k, stack.Count - k); return true; }
                if (boundaries.Contains(n)) return false;
            }
            return false;
        }

        int i = 0;
        int len = html.Length;
        while (i < len)
        {
            char c = html[i];
            if (c != '<' || i + 1 >= len) { text.Append(c); i++; continue; }
            char next = html[i + 1];

            if (next == '!')
            {
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? len : end + 3;
                    continue;
                }
                if (string.Compare(html, i, "<![CDATA[", 0, 9, StringComparison.Ordinal) == 0)
                {
                    int end = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    FlushText();
                    Current().Add(new HtmlText(html[(i + 9)..(end < 0 ? len : end)]));
                    i = end < 0 ? len : end + 3;
                    continue;
                }
                int gt = html.IndexOf('>', i + 2); // <!DOCTYPE …>
                i = gt < 0 ? len : gt + 1;
                continue;
            }
            if (next == '?')
            {
                int gt = html.IndexOf('>', i + 2);
                i = gt < 0 ? len : gt + 1;
                continue;
            }
            if (next == '/')
            {
                int j = i + 2;
                int nameStart = j;
                while (j < len && IsNameChar(html[j])) j++;
                if (j == nameStart) { text.Append(c); i++; continue; } // "</ " is text
                string name = html[nameStart..j].ToLowerInvariant();
                int gt = html.IndexOf('>', j);
                i = gt < 0 ? len : gt + 1;
                FlushText();
                if (name == "br") { Current().Add(new HtmlElement("br")); continue; } // </br> is a <br>
                for (int k = stack.Count - 1; k > 0; k--)
                    if (stack[k].Name == name) { stack.RemoveRange(k, stack.Count - k); break; }
                continue;
            }
            if (!char.IsAsciiLetter(next)) { text.Append(c); i++; continue; }

            // A start tag.
            int p = i + 1;
            while (p < len && IsNameChar(html[p])) p++;
            string tag = html[(i + 1)..p].ToLowerInvariant();
            var element = new HtmlElement(tag);
            bool selfClosing = false;
            while (p < len)
            {
                while (p < len && char.IsWhiteSpace(html[p])) p++;
                if (p >= len) break;
                if (html[p] == '>') { p++; break; }
                if (html[p] == '/' && p + 1 < len && html[p + 1] == '>') { selfClosing = true; p += 2; break; }
                if (html[p] == '/') { p++; continue; }
                int an = p;
                while (p < len && !char.IsWhiteSpace(html[p]) && html[p] is not ('=' or '>') && !(html[p] == '/' && p + 1 < len && html[p + 1] == '>')) p++;
                string attr = html[an..p].ToLowerInvariant();
                while (p < len && char.IsWhiteSpace(html[p])) p++;
                string value = "";
                if (p < len && html[p] == '=')
                {
                    p++;
                    while (p < len && char.IsWhiteSpace(html[p])) p++;
                    if (p < len && html[p] is '"' or '\'')
                    {
                        char q = html[p];
                        int close = html.IndexOf(q, p + 1);
                        if (close < 0) close = len;
                        value = html[(p + 1)..close];
                        p = Math.Min(len, close + 1);
                    }
                    else
                    {
                        int vs = p;
                        while (p < len && !char.IsWhiteSpace(html[p]) && html[p] != '>') p++;
                        value = html[vs..p];
                    }
                }
                if (attr.Length > 0) element.Attributes.TryAdd(attr, WebUtility.HtmlDecode(value));
            }
            i = p;
            FlushText();

            // Implied end tags.
            if (ClosesParagraph.Contains(tag)) CloseOpen(["p"], ["button", "table", "td", "th", "li", "dd", "dt", "blockquote", "div", "section", "article"]);
            switch (tag)
            {
                case "li": CloseOpen(["li"], ["ul", "ol", "menu", "table"]); break;
                case "dt" or "dd": CloseOpen(["dt", "dd"], ["dl", "table"]); break;
                case "tr": CloseOpen(["tr"], ["table", "thead", "tbody", "tfoot"]); break;
                case "td" or "th": CloseOpen(["td", "th"], ["tr", "table"]); break;
                case "thead" or "tbody" or "tfoot": CloseOpen(["thead", "tbody", "tfoot"], ["table"]); break;
                case "option": CloseOpen(["option"], ["select", "datalist", "optgroup"]); break;
            }
            Current().Add(element);
            if (Void.Contains(tag) || selfClosing) continue;
            if (RawText.Contains(tag))
            {
                int end = IndexOfEndTag(html, i, tag);
                string raw = html[i..(end < 0 ? len : end)];
                if (raw.Length > 0) element.Add(new HtmlText(tag is "textarea" or "title" ? WebUtility.HtmlDecode(raw) : raw));
                int gt = end < 0 ? -1 : html.IndexOf('>', end);
                i = gt < 0 ? len : gt + 1;
                continue;
            }
            stack.Add(element);
        }
        FlushText();
        return root;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or ':' or '_';

    private static int IndexOfEndTag(string html, int from, string tag)
    {
        for (int k = html.IndexOf("</", from, StringComparison.Ordinal); k >= 0; k = html.IndexOf("</", k + 2, StringComparison.Ordinal))
            if (string.Compare(html, k + 2, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (k + 2 + tag.Length >= html.Length || !IsNameChar(html[k + 2 + tag.Length])))
                return k;
        return -1;
    }
}
