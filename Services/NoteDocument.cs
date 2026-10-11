using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ScriptAppDashboard.Services;

public enum BlockKind { Paragraph, Heading, Bullet, Number, Timed, Quote, Code, Divider, Raw, Image, Table, Chart }

/// <summary>
/// One block of a note in the visual editor. <see cref="Text"/> is inline markup — **bold**, *italic*, `code`,
/// [text](url) and simple inline HTML tags — except for Code (plain text), Raw (HTML source kept as-is)
/// and Image (the &lt;img&gt; tag of a picture on its own line). A Table keeps its cells in <see cref="Cells"/>;
/// a Chart draws its <see cref="Cells"/> and uses <see cref="Text"/> as its caption.
/// </summary>
public sealed class NoteBlock
{
    public BlockKind Kind { get; set; }
    public int Level { get; set; } = 2;
    public int Indent { get; set; }
    public string Text { get; set; } = "";
    public string Stamp { get; set; } = "";

    /// <summary>Table or chart cells, row by row, each one inline markup. A chart's first row names the series
    /// and its first column holds the labels.</summary>
    public List<List<string>> Cells { get; set; } = new();

    /// <summary>A table's first row is a header row (a chart's always is).</summary>
    public bool Header { get; set; } = true;

    /// <summary>A chart's type: bar, line or pie.</summary>
    public string ChartType { get; set; } = "bar";

    public bool IsList => Kind is BlockKind.Bullet or BlockKind.Number or BlockKind.Timed;

    /// <summary>A table or a chart: shown with its own controls instead of one text box.</summary>
    public bool IsGrid => Kind is BlockKind.Table or BlockKind.Chart;

    /// <summary>The text is rendered with formatting (rather than shown as source).</summary>
    public bool HasMarkup => Kind is not (BlockKind.Code or BlockKind.Raw or BlockKind.Divider or BlockKind.Image) && !IsGrid;

    /// <summary>A block without text to type in (a rule or a picture): the caret skips it.</summary>
    public bool IsAtomic => Kind is BlockKind.Divider or BlockKind.Image;
}

/// <summary>A run of rendered inline text; <see cref="Source"/> is the markup index of each character.</summary>
public sealed record InlineSpan(string Text, bool Bold, bool Italic, bool Code, string? Url, bool Muted, int[] Source);

/// <summary>Converts note HTML (including the old plain list lines) to editor blocks and back, and blocks to Markdown.</summary>
public static class NoteDocument
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex Tag = new(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)\b(?:[^>""']|""[^""]*""|'[^']*')*?(/?)>", Ci);
    private static readonly Regex LineSplit = new(@"\n|<br\s*/?>", Ci);
    private static readonly Regex TimedLine = new(@"^(\s*)◷ (.+?) · (.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberLine = new(@"^(\s*)\d+[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletLine = new(@"^(\s*)[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex QuoteLine = new(@"^>\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex BlockTag = new(@"<(p|div|h[1-6]|ul|ol|li|pre|blockquote|table|figure|hr|section|article)\b", Ci);
    private static readonly Regex StampSpan = new(@"^\s*<span\b[^>]*\bclass\s*=\s*[""']?t[""']?[^>]*>(.*?)</span>\s*", Ci);
    private static readonly Regex RawInline = new(@"\G<(/?)(span|u|s|del|ins|mark|sub|sup|small|big|img|kbd|abbr|font|q|cite|time|var|samp|wbr|br)\b[^<>]*>", Ci);
    private static readonly Regex LinkAt = new(@"\G\[((?:\\.|[^\]\\])*)\]\(([^()\s]*)\)", RegexOptions.Compiled);

    private static readonly HashSet<string> InlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "b", "strong", "i", "em", "code", "span", "u", "s", "del", "ins", "mark", "sub", "sup", "small", "big",
        "img", "kbd", "abbr", "font", "br", "q", "cite", "time", "var", "samp", "wbr", "label",
    };
    private static readonly HashSet<string> PassThrough = new(StringComparer.OrdinalIgnoreCase)
    {
        "span", "u", "s", "del", "ins", "mark", "sub", "sup", "small", "big", "img", "kbd", "abbr", "font", "q", "cite", "time", "var", "samp", "wbr",
    };
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "hr", "img", "input", "meta", "link", "wbr", "col", "area", "base", "source", "embed", "param", "track",
    };
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "main", "header", "footer", "aside", "nav", "center",
    };
    private const string Escapable = @"\*`[]<>";

    // ---- HTML → blocks ------------------------------------------------------

    /// <summary>Splits a note body into blocks. A full HTML document stays one Raw block.</summary>
    public static List<NoteBlock> Parse(string? html)
    {
        var blocks = new List<NoteBlock>();
        var s = (html ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (s.Trim().Length == 0) return blocks;
        if (Regex.IsMatch(s, @"<(html|body|head)\b|<!doctype", RegexOptions.IgnoreCase))
        {
            blocks.Add(new NoteBlock { Kind = BlockKind.Raw, Text = s.Trim() });
            return blocks;
        }
        ParseBlocks(s, blocks, quote: false);
        return blocks;
    }

    private static void ParseBlocks(string s, List<NoteBlock> output, bool quote)
    {
        var pos = 0;
        var looseStart = 0;

        void AddLoose(string chunk)
        {
            foreach (var line in LineSplit.Split(chunk))
            {
                if (StripTags(line).Trim().Length == 0 && !line.Contains("<img", StringComparison.OrdinalIgnoreCase)) continue;
                var block = AsImage(ParseLine(line, fromHtml: true, markdown: false));
                if (quote && block.Kind == BlockKind.Paragraph) block.Kind = BlockKind.Quote;
                output.Add(block);
            }
        }

        while (true)
        {
            var m = Tag.Match(s, pos);
            if (!m.Success) break;
            var openEnd = m.Index + m.Length;
            if (m.Groups[2].Length == 0)
            {
                // an HTML comment: keep it untouched
                AddLoose(s[looseStart..m.Index]);
                output.Add(new NoteBlock { Kind = BlockKind.Raw, Text = m.Value });
                pos = looseStart = openEnd;
                continue;
            }
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (InlineTags.Contains(name))
            {
                pos = openEnd;
                continue;
            }

            AddLoose(s[looseStart..m.Index]);
            pos = looseStart = openEnd;
            if (m.Groups[1].Value == "/") continue; // stray closing tag
            if (VoidTags.Contains(name) || m.Groups[3].Value == "/")
            {
                output.Add(name == "hr" ? new NoteBlock { Kind = BlockKind.Divider } : new NoteBlock { Kind = BlockKind.Raw, Text = m.Value });
                continue;
            }

            var (closeStart, closeEnd) = FindClose(s, name, openEnd);
            var inner = s[openEnd..closeStart];
            var outer = s[m.Index..closeEnd];
            pos = looseStart = closeEnd;
            var styled = Regex.IsMatch(m.Value, @"\sstyle\s*=", RegexOptions.IgnoreCase);

            if (styled && name is not ("ul" or "ol"))
            {
                // custom styling can't be edited visually — keep the element as written
                output.Add(new NoteBlock { Kind = BlockKind.Raw, Text = outer });
            }
            else if (name == "p" || Wrappers.Contains(name))
            {
                if (BlockTag.IsMatch(inner)) ParseBlocks(inner, output, quote);
                else if (name == "p" || StripTags(inner).Trim().Length > 0)
                    output.Add(AsImage(new NoteBlock { Kind = quote ? BlockKind.Quote : BlockKind.Paragraph, Text = InlineToMarkup(inner) }));
            }
            else if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
            {
                output.Add(new NoteBlock { Kind = BlockKind.Heading, Level = name[1] - '0', Text = InlineToMarkup(inner) });
            }
            else if (name is "ul" or "ol")
            {
                var timed = Regex.IsMatch(m.Value, @"\bclass\s*=\s*[""']?[^""'>]*\btimed\b", RegexOptions.IgnoreCase);
                ParseList(inner, name == "ol" ? BlockKind.Number : timed ? BlockKind.Timed : BlockKind.Bullet, 0, output);
            }
            else if (name == "pre")
            {
                var code = WebUtility.HtmlDecode(StripTags(inner));
                if (code.StartsWith('\n')) code = code[1..];
                output.Add(new NoteBlock { Kind = BlockKind.Code, Text = code.TrimEnd('\n') });
            }
            else if (name == "table" && ParseTable(m.Value, inner) is { } table)
            {
                output.Add(table);
            }
            else if (name == "figure" && ParseChart(m.Value, inner) is { } chart)
            {
                output.Add(chart);
            }
            else if (name == "blockquote")
            {
                if (BlockTag.IsMatch(inner)) ParseBlocks(inner, output, quote: true);
                else output.Add(new NoteBlock { Kind = BlockKind.Quote, Text = InlineToMarkup(inner) });
            }
            else
            {
                output.Add(new NoteBlock { Kind = BlockKind.Raw, Text = outer });
            }
        }
        AddLoose(s[looseStart..]);
    }

    private static void ParseList(string inner, BlockKind kind, int indent, List<NoteBlock> output)
    {
        var depth = 0;
        var itemStart = -1;

        void Flush(int end)
        {
            if (itemStart >= 0) AddItem(inner[itemStart..end]);
            itemStart = -1;
        }

        void AddItem(string content)
        {
            var nested = Regex.Match(content, @"<(ul|ol)\b", RegexOptions.IgnoreCase);
            var own = nested.Success ? content[..nested.Index] : content;
            var block = new NoteBlock { Kind = kind, Indent = indent };
            var stamp = StampSpan.Match(own);
            if (stamp.Success)
            {
                block.Kind = BlockKind.Timed;
                block.Stamp = WebUtility.HtmlDecode(StripTags(stamp.Groups[1].Value)).Trim();
                own = own[(stamp.Index + stamp.Length)..];
            }
            block.Text = InlineToMarkup(own);
            output.Add(block);
            if (!nested.Success) return;

            var rest = content[nested.Index..];
            var p = 0;
            while (true)
            {
                var m = Tag.Match(rest, p);
                if (!m.Success) break;
                var name = m.Groups[2].Value.ToLowerInvariant();
                if (m.Groups[1].Value == "/" || name is not ("ul" or "ol"))
                {
                    p = m.Index + m.Length;
                    continue;
                }
                var (cs, ce) = FindClose(rest, name, m.Index + m.Length);
                var timed = Regex.IsMatch(m.Value, @"\btimed\b", RegexOptions.IgnoreCase);
                ParseList(rest[(m.Index + m.Length)..cs], name == "ol" ? BlockKind.Number : timed ? BlockKind.Timed : BlockKind.Bullet, indent + 1, output);
                p = ce;
            }
        }

        for (var m = Tag.Match(inner); m.Success; m = m.NextMatch())
        {
            var name = m.Groups[2].Value.ToLowerInvariant();
            var close = m.Groups[1].Value == "/";
            if (name is "ul" or "ol")
            {
                depth += close ? -1 : 1;
                continue;
            }
            if (name != "li" || depth != 0) continue;
            Flush(m.Index);
            if (!close) itemStart = m.Index + m.Length;
        }
        Flush(inner.Length);
    }

    private static readonly Regex BareTag = new(@"^<[a-zA-Z0-9]+\s*/?>$", RegexOptions.Compiled);

    /// <summary>A plain table as a Table block: no attributes, no spans, header cells only in the first row and only
    /// inline content in cells. Anything else returns null and stays a Raw block, so nothing is lost.</summary>
    private static NoteBlock? ParseTable(string openTag, string inner)
    {
        if (!BareTag.IsMatch(openTag)) return null;
        var rows = new List<List<string>>();
        List<string>? row = null;
        int cellStart = -1, pos = 0, firstTh = 0;
        var cellTh = false;
        var ok = true;

        void EndCell(int end)
        {
            var content = inner[cellStart..end];
            if (BlockTag.IsMatch(content)) ok = false;
            row!.Add(InlineToMarkup(content));
            if (cellTh && rows.Count > 1) ok = false; // a header cell further down
            if (cellTh) firstTh++;
            cellStart = -1;
        }

        for (var t = Tag.Match(inner); t.Success && ok; t = t.NextMatch())
        {
            if (t.Groups[2].Length == 0) return null; // a comment
            var name = t.Groups[2].Value.ToLowerInvariant();
            if (InlineTags.Contains(name))
            {
                if (cellStart < 0) return null;
                continue;
            }
            if (name is not ("thead" or "tbody" or "tfoot" or "tr" or "th" or "td")) return null;
            var close = t.Groups[1].Value == "/";
            if (!close && !BareTag.IsMatch(t.Value)) return null; // colspan, class, …
            if (cellStart < 0 && inner[pos..t.Index].Trim().Length > 0) return null; // text outside the cells
            if (cellStart >= 0) EndCell(t.Index);
            if (name is "th" or "td")
            {
                if (!close)
                {
                    if (row is null) rows.Add(row = new List<string>());
                    cellStart = t.Index + t.Length;
                    cellTh = name == "th";
                }
            }
            else
            {
                row = null;
                if (name == "tr" && !close) rows.Add(row = new List<string>());
            }
            pos = t.Index + t.Length;
        }
        if (ok && cellStart >= 0) EndCell(inner.Length);
        else if (inner[pos..].Trim().Length > 0) return null;
        rows.RemoveAll(r => r.Count == 0);
        if (!ok || rows.Count == 0 || firstTh > 0 && firstTh != rows[0].Count) return null; // a mixed first row
        var columns = rows.Max(r => r.Count);
        foreach (var r in rows) while (r.Count < columns) r.Add("");
        return new NoteBlock { Kind = BlockKind.Table, Cells = rows, Header = firstTh > 0 };
    }

    private static readonly Regex ChartBody = new(@"^\s*(?:<figcaption>(.*?)</figcaption>)?\s*(<table\b[^>]*>)(.*)</table>\s*$", Ci);

    /// <summary>A &lt;figure class="chart" data-type="…"&gt; holding an optional caption and a plain table.</summary>
    private static NoteBlock? ParseChart(string openTag, string inner)
    {
        var cls = AttrValue(openTag, "class");
        var type = AttrValue(openTag, "data-type") ?? "bar";
        var rest = Regex.Replace(openTag, @"\s(class|data-type)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
        if (cls != "chart" || !BareTag.IsMatch(rest)) return null;
        var m = ChartBody.Match(inner);
        if (!m.Success || BlockTag.IsMatch(m.Groups[1].Value)) return null;
        if (ParseTable(m.Groups[2].Value, m.Groups[3].Value) is not { Header: true } table) return null;
        table.Kind = BlockKind.Chart;
        table.ChartType = ChartTypeName(type);
        table.Text = InlineToMarkup(m.Groups[1].Value);
        return table;
    }

    /// <summary>bar, line or pie (anything else is bar).</summary>
    public static string ChartTypeName(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "line" => "line",
        "pie" or "donut" => "pie",
        _ => "bar",
    };

    /// <summary>
    /// One line of text as a block. "• x", "- x", "1. x" and "◷ time · x" become list items;
    /// with <paramref name="markdown"/> also "# x" (heading) and "> x" (quote).
    /// </summary>
    public static NoteBlock ParseLine(string line, bool fromHtml, bool markdown)
    {
        string Inline(string text) => fromHtml ? InlineToMarkup(text) : text.Trim();
        int IndentOf(string lead) => Math.Min(6, lead.Count(c => c == '\t') + lead.Count(c => c == ' ') / 2);

        Match m;
        if ((m = TimedLine.Match(line)).Success)
            return new NoteBlock { Kind = BlockKind.Timed, Indent = IndentOf(m.Groups[1].Value), Stamp = m.Groups[2].Value.Trim(), Text = Inline(m.Groups[3].Value) };
        if ((m = NumberLine.Match(line)).Success)
            return new NoteBlock { Kind = BlockKind.Number, Indent = IndentOf(m.Groups[1].Value), Text = Inline(m.Groups[2].Value) };
        if ((m = BulletLine.Match(line)).Success)
            return new NoteBlock { Kind = BlockKind.Bullet, Indent = IndentOf(m.Groups[1].Value), Text = Inline(m.Groups[2].Value) };
        if (markdown && (m = HeadingLine.Match(line.Trim())).Success)
            return new NoteBlock { Kind = BlockKind.Heading, Level = m.Groups[1].Length, Text = Inline(m.Groups[2].Value) };
        if (markdown && (m = QuoteLine.Match(line.Trim())).Success)
            return new NoteBlock { Kind = BlockKind.Quote, Text = Inline(m.Groups[1].Value) };
        return new NoteBlock { Text = Inline(line) };
    }

    private static readonly Regex Fence = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    /// <summary>Markdown (e.g. an AI answer) as blocks: headings, lists, quotes, ``` code, --- rules, tables, and ```chart fences.</summary>
    public static List<NoteBlock> FromMarkdown(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Fence.IsMatch(line))
            {
                var info = line.Trim()[3..].Trim();
                var code = new List<string>();
                while (++i < lines.Length && !Fence.IsMatch(lines[i])) code.Add(lines[i]);
                var chart = Regex.Match(info, @"^chart\b\s*(\w*)", RegexOptions.IgnoreCase);
                blocks.Add(chart.Success && ChartFromLines(code, chart.Groups[1].Value) is { } c
                    ? c
                    : new NoteBlock { Kind = BlockKind.Code, Text = string.Join("\n", code) });
                continue;
            }
            if (line.Trim().Length == 0) continue;
            if (Rule.IsMatch(line))
            {
                blocks.Add(new NoteBlock { Kind = BlockKind.Divider });
                continue;
            }
            if (MarkdownImage.Match(line) is { Success: true } img)
            {
                blocks.Add(Image(img.Groups[2].Value, img.Groups[1].Value));
                continue;
            }
            if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
            {
                var rows = new List<string> { line };
                i++; // the separator
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|')) rows.Add(lines[++i]);
                blocks.Add(new NoteBlock { Kind = BlockKind.Table, Cells = PadRows(rows.Select(PipeCells)) });
                continue;
            }
            var block = ParseLine(line, fromHtml: false, markdown: true);
            if (block.Text.StartsWith("[ ] ") || block.Text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
                block.Text = (block.Text[1] == ' ' ? "☐ " : "☑ ") + block.Text[4..];
            blocks.Add(block);
        }
        return blocks;
    }

    /// <summary>The cells of a Markdown table row ("| a | b |"); "\|" is a literal bar.</summary>
    private static List<string> PipeCells(string row)
    {
        var s = row.Trim();
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith('|') && !s.EndsWith("\\|")) s = s[..^1];
        return Regex.Split(s, @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToList();
    }

    /// <summary>Rows padded with empty cells to the widest one.</summary>
    private static List<List<string>> PadRows(IEnumerable<List<string>> rows)
    {
        var list = rows.ToList();
        var columns = list.Count == 0 ? 0 : list.Max(r => r.Count);
        foreach (var r in list) while (r.Count < columns) r.Add("");
        return list;
    }

    /// <summary>
    /// Delimited text as rows of cells: quoted fields may hold the separator, line breaks and "" (as Excel copies them).
    /// </summary>
    public static List<List<string>> ParseDelimited(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < s.Length && s[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == separator) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        rows.Add(row);
        return rows;
    }

    /// <summary>
    /// Cells for pasted text that is a grid: spreadsheet cells (every line has the same number of tabs) or a Markdown
    /// table. Null for anything else.
    /// </summary>
    public static List<List<string>>? GridFromText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n').Split('\n');
        if (lines.Length < 2) return null;
        if (lines.All(l => l.TrimStart().StartsWith('|')) && TableSeparator.IsMatch(lines[1]))
            return PadRows(lines.Where((_, i) => i != 1).Select(PipeCells));
        var rows = ParseDelimited(text.Trim('\r', '\n'), '\t');
        if (rows.Count < 2 || rows[0].Count < 2 || rows.Any(r => r.Count != rows[0].Count)) return null;
        return rows.Select(r => r.Select(c => c.Trim()).ToList()).ToList();
    }

    /// <summary>
    /// A chart from the lines of a ```chart fence: optional "title: …" and "type: …" lines, then the data as a
    /// Markdown table or as comma, semicolon or tab separated lines (labels first, a header row naming the series).
    /// </summary>
    private static NoteBlock? ChartFromLines(List<string> lines, string type)
    {
        var title = "";
        var data = new List<string>();
        foreach (var line in lines)
        {
            var key = Regex.Match(line, @"^\s*(title|type)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (key.Success && data.Count == 0)
            {
                if (key.Groups[1].Value.Equals("title", StringComparison.OrdinalIgnoreCase)) title = key.Groups[2].Value.Trim();
                else type = key.Groups[2].Value;
            }
            else if (line.Trim().Length > 0 && !TableSeparator.IsMatch(line))
            {
                data.Add(line);
            }
        }
        if (data.Count < 2) return null;
        List<List<string>> cells;
        if (data.All(l => l.TrimStart().StartsWith('|')))
        {
            cells = PadRows(data.Select(PipeCells));
        }
        else
        {
            var separator = data[0].Contains('\t') ? '\t' : data[0].Contains(';') ? ';' : ',';
            cells = PadRows(ParseDelimited(string.Join("\n", data), separator).Select(r => r.Select(c => c.Trim()).ToList()));
        }
        if (cells[0].Count < 2) return null;
        return new NoteBlock { Kind = BlockKind.Chart, ChartType = ChartTypeName(type), Text = title, Cells = cells };
    }

    /// <summary>An empty table to start from: a header row and two rows.</summary>
    public static NoteBlock NewTable(int rows = 3, int columns = 3) => new()
    {
        Kind = BlockKind.Table,
        Cells = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat("", columns).ToList()).ToList(),
    };

    /// <summary>A chart with sample data to overwrite.</summary>
    public static NoteBlock NewChart() => new()
    {
        Kind = BlockKind.Chart,
        Cells = new List<List<string>>
        {
            new() { "Month", "Series 1" },
            new() { "Jan", "12" },
            new() { "Feb", "19" },
            new() { "Mar", "8" },
            new() { "Apr", "15" },
        },
    };

    /// <summary>A chart of a table's data (a table without a header row gets one).</summary>
    public static NoteBlock ChartFromTable(NoteBlock table)
    {
        var cells = table.Cells.Select(r => r.ToList()).ToList();
        if (!table.Header && cells.Count > 0)
            cells.Insert(0, cells[0].Select((_, i) => i == 0 ? "" : $"Series {i}").ToList());
        return new NoteBlock { Kind = BlockKind.Chart, Cells = cells };
    }

    /// <summary>The visible text of inline markup.</summary>
    public static string PlainText(string markup) => string.Concat(ParseInline(markup).Select(s => s.Text));

    private static readonly Regex LoneImage = new(@"^\s*(<img\b[^<>]*>)\s*$", Ci);
    private static readonly Regex MarkdownImage = new(@"^\s*!\[([^\]]*)\]\(([^()\s]+)\)\s*$", RegexOptions.Compiled);

    /// <summary>A paragraph holding nothing but one &lt;img&gt; is a picture block.</summary>
    private static NoteBlock AsImage(NoteBlock block)
    {
        if (block.Kind != BlockKind.Paragraph || LoneImage.Match(block.Text) is not { Success: true } m) return block;
        return new NoteBlock { Kind = BlockKind.Image, Text = m.Groups[1].Value };
    }

    /// <summary>A picture block for an image src ("images/x.png").</summary>
    public static NoteBlock Image(string src, string alt = "")
        => new() { Kind = BlockKind.Image, Text = $"<img src=\"{WebUtility.HtmlEncode(src)}\" alt=\"{WebUtility.HtmlEncode(alt)}\">" };

    /// <summary>The src of a picture block's &lt;img&gt; tag.</summary>
    public static string ImageSrc(NoteBlock block)
    {
        var m = Regex.Match(block.Text, @"\bsrc\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[2].Value) : "";
    }

    private static (int Start, int End) FindClose(string s, string name, int from)
    {
        var depth = 1;
        for (var m = Tag.Match(s, from); m.Success; m = m.NextMatch())
        {
            if (!m.Groups[2].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (m.Groups[1].Value == "/")
            {
                if (--depth == 0) return (m.Index, m.Index + m.Length);
            }
            else if (m.Groups[3].Value != "/")
            {
                depth++;
            }
        }
        return (s.Length, s.Length);
    }

    private static string StripTags(string s) => Regex.Replace(s, @"<[^>]+>", "");

    /// <summary>Inline HTML (b, i, code, a, br, …) as editor markup.</summary>
    public static string InlineToMarkup(string html)
    {
        var sb = new StringBuilder();
        var pending = new List<string>(); // opening markers not written yet (waiting for visible text)
        var links = new Stack<string>();
        int bold = 0, italic = 0;

        void FlushPending()
        {
            foreach (var p in pending) sb.Append(p);
            pending.Clear();
        }

        void Close(string marker, string opener)
        {
            var i = pending.LastIndexOf(opener);
            if (i >= 0)
            {
                pending.RemoveAt(i); // nothing visible inside — drop the pair
                return;
            }
            // keep spaces outside the markers: "**bold** text", not "**bold **text"
            var end = sb.Length;
            while (end > 0 && sb[end - 1] == ' ') end--;
            var spaces = sb.Length - end;
            sb.Length = end;
            sb.Append(marker).Append(' ', spaces);
        }

        void Text(string raw)
        {
            if (raw.Length == 0) return;
            var text = Regex.Replace(WebUtility.HtmlDecode(raw), @"\s+", " ");
            if (text.Trim().Length == 0)
            {
                if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n') sb.Append(' ');
                return;
            }
            if (text[0] == ' ')
            {
                if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n') sb.Append(' ');
                text = text.TrimStart();
            }
            FlushPending();
            sb.Append(Escape(text));
        }

        var pos = 0;
        while (true)
        {
            var m = Tag.Match(html, pos);
            Text(html[pos..(m.Success ? m.Index : html.Length)]);
            if (!m.Success) break;
            pos = m.Index + m.Length;
            if (m.Groups[2].Length == 0) continue; // comment
            var name = m.Groups[2].Value.ToLowerInvariant();
            var close = m.Groups[1].Value == "/";
            switch (name)
            {
                case "b" or "strong":
                    if (close) { if (bold > 0 && --bold == 0) Close("**", "**"); }
                    else if (bold++ == 0) pending.Add("**");
                    break;
                case "i" or "em":
                    if (close) { if (italic > 0 && --italic == 0) Close("*", "*"); }
                    else if (italic++ == 0) pending.Add("*");
                    break;
                case "code":
                    if (close) break;
                    var (cs, ce) = FindClose(html, "code", pos);
                    var code = WebUtility.HtmlDecode(StripTags(html[pos..cs])).Replace('`', '\'');
                    if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n' && char.IsWhiteSpace(html, Math.Max(0, m.Index - 1))) sb.Append(' ');
                    FlushPending();
                    sb.Append('`').Append(code).Append('`');
                    pos = ce;
                    break;
                case "a":
                    if (close)
                    {
                        if (links.Count > 0 && links.Pop() is { Length: > 0 } end) Close(end, "[");
                    }
                    else
                    {
                        var href = AttrValue(m.Value, "href");
                        if (href is null)
                        {
                            links.Push("");
                        }
                        else
                        {
                            pending.Add("[");
                            links.Push("](" + WebUtility.HtmlDecode(href).Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29") + ")");
                        }
                    }
                    break;
                case "br":
                    FlushPending();
                    sb.Append('\n');
                    break;
                default:
                    if (PassThrough.Contains(name))
                    {
                        FlushPending();
                        sb.Append(m.Value);
                    }
                    else if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n')
                    {
                        sb.Append(' '); // a block tag inside inline content separates words
                    }
                    break;
            }
        }
        while (links.Count > 0) if (links.Pop() is { Length: > 0 } end) Close(end, "[");
        if (italic > 0) Close("*", "*");
        if (bold > 0) Close("**", "**");
        return Regex.Replace(sb.ToString(), @" *\n *", "\n").Trim();
    }

    private static string? AttrValue(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    /// <summary>Backslash-escapes characters that would otherwise read as markup.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var prev = i > 0 ? text[i - 1] : 'x';
            var next = i + 1 < text.Length ? text[i + 1] : 'x';
            var escape = c switch
            {
                '\\' => Escapable.Contains(next),
                '`' => true,
                '*' => !(char.IsWhiteSpace(prev) && char.IsWhiteSpace(next)), // "2 * 3" can't be emphasis
                '[' => text.IndexOf("](", i, StringComparison.Ordinal) > i,
                '<' => char.IsLetter(next) || next == '/',
                _ => false,
            };
            if (escape) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- markup tokens ------------------------------------------------------

    private enum Tk { Text, Star2, Star1, Code, LinkOpen, LinkClose, Raw, Break }

    private sealed class Token
    {
        public Tk Kind;
        public string Text = "";
        public int Pos;
        public string? Url;
        public bool Paired;
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var linkClose = -1;
        var linkEnd = -1;
        var i = 0;
        while (i < s.Length)
        {
            if (i == linkClose)
            {
                list.Add(new Token { Kind = Tk.LinkClose, Pos = i });
                i = linkEnd;
                linkClose = -1;
                continue;
            }
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && Escapable.Contains(s[i + 1]))
            {
                list.Add(new Token { Kind = Tk.Text, Text = s[i + 1].ToString(), Pos = i + 1 });
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var close = s.IndexOf('`', i + 1);
                if (close > i + 1 && (linkClose < 0 || close < linkClose))
                {
                    list.Add(new Token { Kind = Tk.Code, Text = s[(i + 1)..close], Pos = i + 1 });
                    i = close + 1;
                    continue;
                }
            }
            if (c == '*')
            {
                var two = i + 1 < s.Length && s[i + 1] == '*';
                list.Add(new Token { Kind = two ? Tk.Star2 : Tk.Star1, Text = two ? "**" : "*", Pos = i });
                i += two ? 2 : 1;
                continue;
            }
            if (c == '[' && linkClose < 0)
            {
                var m = LinkAt.Match(s, i);
                if (m.Success)
                {
                    list.Add(new Token { Kind = Tk.LinkOpen, Pos = i, Url = m.Groups[2].Value });
                    linkClose = m.Groups[1].Index + m.Groups[1].Length;
                    linkEnd = m.Index + m.Length;
                    i++;
                    continue;
                }
            }
            if (c == '<')
            {
                var m = RawInline.Match(s, i);
                if (m.Success)
                {
                    var br = m.Groups[2].Value.Equals("br", StringComparison.OrdinalIgnoreCase);
                    list.Add(new Token { Kind = br ? Tk.Break : Tk.Raw, Text = m.Value, Pos = i });
                    i += m.Length;
                    continue;
                }
            }
            list.Add(new Token { Kind = c == '\n' ? Tk.Break : Tk.Text, Text = c.ToString(), Pos = i });
            i++;
        }
        if (linkClose >= 0) list.Add(new Token { Kind = Tk.LinkClose, Pos = s.Length });

        // pair emphasis markers: an opener can't be followed by a space, a closer can't follow one
        foreach (var kind in new[] { Tk.Star2, Tk.Star1 })
        {
            Token? open = null;
            foreach (var t in list)
            {
                if (t.Kind != kind) continue;
                var len = t.Text.Length;
                var before = t.Pos > 0 ? s[t.Pos - 1] : ' ';
                var after = t.Pos + len < s.Length ? s[t.Pos + len] : ' ';
                if (open is not null && !char.IsWhiteSpace(before))
                {
                    open.Paired = t.Paired = true;
                    open = null;
                }
                else if (!char.IsWhiteSpace(after))
                {
                    open = t;
                }
            }
        }
        return list;
    }

    /// <summary>Markup as formatted runs, for display.</summary>
    public static List<InlineSpan> ParseInline(string markup)
    {
        var spans = new List<InlineSpan>();
        bool bold = false, italic = false;
        string? url = null;
        var sb = new StringBuilder();
        var src = new List<int>();

        void Flush()
        {
            if (sb.Length == 0) return;
            spans.Add(new InlineSpan(sb.ToString(), bold, italic, false, url, false, src.ToArray()));
            sb.Clear();
            src.Clear();
        }

        void Add(string text, int pos)
        {
            for (var k = 0; k < text.Length; k++)
            {
                sb.Append(text[k]);
                src.Add(pos + k);
            }
        }

        foreach (var t in Tokenize(markup))
        {
            switch (t.Kind)
            {
                case Tk.Star2 when t.Paired: Flush(); bold = !bold; break;
                case Tk.Star1 when t.Paired: Flush(); italic = !italic; break;
                case Tk.Text or Tk.Star2 or Tk.Star1: Add(t.Text, t.Pos); break;
                case Tk.Break: Add("\n", t.Pos); break;
                case Tk.Code:
                    Flush();
                    spans.Add(new InlineSpan(t.Text, bold, italic, true, url, false, Enumerable.Range(t.Pos, t.Text.Length).ToArray()));
                    break;
                case Tk.LinkOpen: Flush(); url = t.Url; break;
                case Tk.LinkClose: Flush(); url = null; break;
                case Tk.Raw when t.Text.StartsWith("<img", StringComparison.OrdinalIgnoreCase):
                    Flush();
                    spans.Add(new InlineSpan("[image]", false, false, false, null, true, Enumerable.Repeat(t.Pos, 7).ToArray()));
                    break;
            }
        }
        Flush();
        return spans;
    }

    /// <summary>The markup index for a character position in the rendered text.</summary>
    public static int SourceIndex(IReadOnlyList<InlineSpan> spans, int rendered, int markupLength)
    {
        foreach (var span in spans)
        {
            if (rendered < span.Text.Length) return span.Source[Math.Max(0, rendered)];
            rendered -= span.Text.Length;
        }
        return markupLength;
    }

    /// <summary>Emphasis markers still open at the end of <paramref name="markup"/>, in opening order (used to split a block).</summary>
    public static List<string> OpenMarkers(string markup)
    {
        // within the text before the split, an opener whose closer is after the split is left unpaired
        return Tokenize(markup)
            .Where(t => t.Kind is Tk.Star2 or Tk.Star1 && !t.Paired
                        && t.Pos + t.Text.Length < markup.Length && !char.IsWhiteSpace(markup[t.Pos + t.Text.Length]))
            .Select(t => t.Text)
            .ToList();
    }

    private static string Encode(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Editor markup as inline HTML.</summary>
    public static string MarkupToHtml(string markup)
    {
        var sb = new StringBuilder();
        var open = new List<string>();
        string? openUrl = null;
        bool bold = false, italic = false;
        string? url = null;

        void Sync()
        {
            var k = open.FindIndex(t => t switch { "a" => url is null || url != openUrl, "b" => !bold, _ => !italic });
            if (k >= 0)
            {
                for (var j = open.Count - 1; j >= k; j--) sb.Append("</").Append(open[j]).Append('>');
                open.RemoveRange(k, open.Count - k);
            }
            if (url is not null && !open.Contains("a"))
            {
                sb.Append("<a href=\"").Append(Encode(url).Replace("\"", "&quot;")).Append("\">");
                open.Add("a");
                openUrl = url;
            }
            if (bold && !open.Contains("b")) { sb.Append("<b>"); open.Add("b"); }
            if (italic && !open.Contains("i")) { sb.Append("<i>"); open.Add("i"); }
        }

        foreach (var t in Tokenize(markup))
        {
            switch (t.Kind)
            {
                case Tk.Star2 when t.Paired: bold = !bold; break;
                case Tk.Star1 when t.Paired: italic = !italic; break;
                case Tk.Text or Tk.Star2 or Tk.Star1: Sync(); sb.Append(Encode(t.Text)); break;
                case Tk.Code: Sync(); sb.Append("<code>").Append(Encode(t.Text)).Append("</code>"); break;
                case Tk.LinkOpen: url = t.Url; break;
                case Tk.LinkClose: url = null; break;
                case Tk.Break: Sync(); sb.Append("<br>"); break;
                case Tk.Raw: Sync(); sb.Append(t.Text); break;
            }
        }
        bold = italic = false;
        url = null;
        Sync();
        return sb.ToString();
    }

    // ---- blocks → HTML / Markdown --------------------------------------------

    /// <summary>The number shown for each numbered item, and the nesting depth of each list item.</summary>
    public static (int[] Numbers, int[] Depths) ListInfo(IReadOnlyList<NoteBlock> blocks)
    {
        var numbers = new int[blocks.Count];
        var depths = new int[blocks.Count];
        var counters = new List<int>();
        var kinds = new List<BlockKind>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (!b.IsList)
            {
                counters.Clear();
                kinds.Clear();
                continue;
            }
            var d = Math.Min(b.Indent, counters.Count);
            if (counters.Count > d + 1)
            {
                counters.RemoveRange(d + 1, counters.Count - d - 1);
                kinds.RemoveRange(d + 1, kinds.Count - d - 1);
            }
            if (counters.Count == d + 1 && kinds[d] != b.Kind)
            {
                counters[d] = 0;
                kinds[d] = b.Kind;
            }
            if (counters.Count == d)
            {
                counters.Add(0);
                kinds.Add(b.Kind);
            }
            counters[d]++;
            depths[i] = d;
            if (b.Kind == BlockKind.Number) numbers[i] = counters[d];
        }
        return (numbers, depths);
    }

    private static int LastContent(IReadOnlyList<NoteBlock> blocks)
    {
        var last = blocks.Count;
        while (last > 0 && blocks[last - 1].Kind == BlockKind.Paragraph && blocks[last - 1].Text.Trim().Length == 0) last--;
        return last;
    }

    /// <summary>A table's cells as HTML, one row per line.</summary>
    private static string TableHtml(List<List<string>> cells, bool header)
    {
        var sb = new StringBuilder("<table>\n");
        for (var r = 0; r < cells.Count; r++)
        {
            var tag = r == 0 && header ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in cells[r]) sb.Append('<').Append(tag).Append('>').Append(MarkupToHtml(cell)).Append("</").Append(tag).Append('>');
            sb.Append("</tr>\n");
        }
        return sb.Append("</table>").ToString();
    }

    /// <summary>
    /// Blocks as clean HTML, one block per line. Trailing empty paragraphs are dropped. With <paramref name="chartImage"/>
    /// (for export) a chart also gets that picture (an &lt;svg&gt;) and its table folds away under "Data".
    /// </summary>
    public static string ToHtml(IReadOnlyList<NoteBlock> blocks, Func<NoteBlock, string>? chartImage = null)
    {
        var sb = new StringBuilder();
        var lists = new List<string>(); // open list tags, outermost first
        var (_, depths) = ListInfo(blocks);
        var inQuote = false;

        static string Close(string open) => open.StartsWith("<ol") ? "</ol>" : "</ul>";

        void PopList()
        {
            sb.Append("</li>\n").Append(Close(lists[^1])).Append('\n');
            lists.RemoveAt(lists.Count - 1);
        }

        var last = LastContent(blocks);
        for (var i = 0; i < last; i++)
        {
            var b = blocks[i];
            if (!b.IsList) while (lists.Count > 0) PopList();
            if (inQuote && b.Kind != BlockKind.Quote)
            {
                sb.Append("</blockquote>\n");
                inQuote = false;
            }
            switch (b.Kind)
            {
                case BlockKind.Paragraph:
                    sb.Append("<p>").Append(MarkupToHtml(b.Text)).Append("</p>\n");
                    break;
                case BlockKind.Heading:
                    var level = Math.Clamp(b.Level, 1, 6);
                    sb.Append("<h").Append(level).Append('>').Append(MarkupToHtml(b.Text)).Append("</h").Append(level).Append(">\n");
                    break;
                case BlockKind.Quote:
                    if (!inQuote) sb.Append("<blockquote>\n");
                    inQuote = true;
                    sb.Append("<p>").Append(MarkupToHtml(b.Text)).Append("</p>\n");
                    break;
                case BlockKind.Code:
                    sb.Append("<pre><code>").Append(Encode(b.Text)).Append("</code></pre>\n");
                    break;
                case BlockKind.Divider:
                    sb.Append("<hr>\n");
                    break;
                case BlockKind.Image:
                    sb.Append("<p>").Append(b.Text).Append("</p>\n");
                    break;
                case BlockKind.Raw:
                    sb.Append(b.Text).Append('\n');
                    break;
                case BlockKind.Table:
                    if (b.Cells.Count > 0) sb.Append(TableHtml(b.Cells, b.Header)).Append('\n');
                    break;
                case BlockKind.Chart:
                    sb.Append("<figure class=\"chart\" data-type=\"").Append(ChartTypeName(b.ChartType)).Append("\">\n");
                    if (b.Text.Trim().Length > 0) sb.Append("<figcaption>").Append(MarkupToHtml(b.Text.Trim())).Append("</figcaption>\n");
                    if (chartImage is null)
                    {
                        sb.Append(TableHtml(b.Cells, header: true)).Append('\n');
                    }
                    else
                    {
                        sb.Append(chartImage(b)).Append('\n');
                        sb.Append("<details><summary>Data</summary>\n").Append(TableHtml(b.Cells, header: true)).Append("\n</details>\n");
                    }
                    sb.Append("</figure>\n");
                    break;
                default:
                    var open = b.Kind switch { BlockKind.Number => "<ol>", BlockKind.Timed => "<ul class=\"timed\">", _ => "<ul>" };
                    var d = depths[i];
                    while (lists.Count > d + 1) PopList();
                    if (lists.Count == d + 1)
                    {
                        if (lists[^1] == open) sb.Append("</li>\n");
                        else PopList();
                    }
                    if (lists.Count == d)
                    {
                        sb.Append(open).Append('\n');
                        lists.Add(open);
                    }
                    sb.Append("<li>");
                    if (b.Kind == BlockKind.Timed && b.Stamp.Length > 0)
                        sb.Append("<span class=\"t\">").Append(Encode(b.Stamp)).Append("</span> ");
                    sb.Append(MarkupToHtml(b.Text));
                    break;
            }
        }
        while (lists.Count > 0) PopList();
        if (inQuote) sb.Append("</blockquote>\n");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Blocks as Markdown; Raw blocks go through <paramref name="rawHtml"/>.</summary>
    public static string ToMarkdown(IReadOnlyList<NoteBlock> blocks, Func<string, string> rawHtml)
    {
        var sb = new StringBuilder();
        var (numbers, depths) = ListInfo(blocks);
        NoteBlock? prev = null;
        var last = LastContent(blocks);
        for (var i = 0; i < last; i++)
        {
            var b = blocks[i];
            if (b.Kind == BlockKind.Paragraph && b.Text.Trim().Length == 0) continue;
            if (sb.Length > 0)
                sb.Append(prev is null ? "\n\n"
                    : prev.IsList && b.IsList ? "\n"
                    : prev.Kind == BlockKind.Quote && b.Kind == BlockKind.Quote ? "\n>\n"
                    : "\n\n");
            var text = MarkupToMarkdown(b.Text);
            var pad = new string(' ', depths[i] * 4);
            switch (b.Kind)
            {
                case BlockKind.Heading: sb.Append('#', Math.Clamp(b.Level, 1, 6)).Append(' ').Append(text); break;
                case BlockKind.Bullet: sb.Append(pad).Append("- ").Append(text); break;
                case BlockKind.Number: sb.Append(pad).Append(numbers[i]).Append(". ").Append(text); break;
                case BlockKind.Timed:
                    sb.Append(pad).Append("- ");
                    if (b.Stamp.Length > 0) sb.Append("**").Append(b.Stamp).Append("** · ");
                    sb.Append(text);
                    break;
                case BlockKind.Quote: sb.Append("> ").Append(text.Replace("\n", "\n> ")); break;
                case BlockKind.Code: sb.Append("```\n").Append(b.Text).Append("\n```"); break;
                case BlockKind.Divider: sb.Append("---"); break;
                case BlockKind.Raw: sb.Append(rawHtml(b.Text)); break;
                case BlockKind.Table: sb.Append(TableMarkdown(b.Cells, b.Header)); break;
                case BlockKind.Chart:
                    sb.Append("**").Append(text.Trim().Length > 0 ? text.Trim() : "Chart").Append("** (")
                      .Append(ChartTypeName(b.ChartType)).Append(" chart)\n\n").Append(TableMarkdown(b.Cells, header: true));
                    break;
                default: sb.Append(text); break;
            }
            prev = b;
        }
        return sb.ToString().Trim();
    }

    /// <summary>A Markdown table; a table without a header row gets an empty one (Markdown needs it).</summary>
    private static string TableMarkdown(List<List<string>> cells, bool header)
    {
        if (cells.Count == 0) return "";
        static string Cell(string markup) => MarkupToMarkdown(markup).Replace("|", "\\|").Replace("\n", "<br>").Trim();
        static string Line(IEnumerable<string> row) => "| " + string.Join(" | ", row) + " |";
        var columns = cells[0].Count;
        var sb = new StringBuilder();
        sb.Append(Line(header ? cells[0].Select(Cell) : Enumerable.Repeat("", columns))).Append('\n');
        sb.Append(Line(Enumerable.Repeat("---", columns)));
        foreach (var row in cells.Skip(header ? 1 : 0)) sb.Append('\n').Append(Line(row.Select(Cell)));
        return sb.ToString();
    }

    /// <summary>Markup is already Markdown-like; only the inline HTML tags need converting.</summary>
    private static string MarkupToMarkdown(string markup)
    {
        var md = Regex.Replace(markup, @"<img\b[^>]*\bsrc\s*=\s*[""']([^""']*)[""'][^>]*>", "![]($1)", RegexOptions.IgnoreCase);
        md = Regex.Replace(md, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        return Regex.Replace(md, @"(?<!\\)</?(span|u|s|del|ins|mark|sub|sup|small|big|kbd|abbr|font|q|cite|time|var|samp|wbr)\b[^<>]*>", "", RegexOptions.IgnoreCase);
    }
}
