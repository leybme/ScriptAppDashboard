using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

public static class NotesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string NotesFile => Path.Combine(ConfigService.ConfigDir, "notes.json");

    public static List<NoteItem> Load()
    {
        try
        {
            if (File.Exists(NotesFile))
                return JsonSerializer.Deserialize<List<NoteItem>>(File.ReadAllText(NotesFile)) ?? new();
        }
        catch
        {
            // unreadable notes file — keep a copy so nothing is lost, then start empty
            try { File.Copy(NotesFile, NotesFile + ".bad", overwrite: true); } catch { }
        }
        return new();
    }

    public static void Save(List<NoteItem> notes)
    {
        Directory.CreateDirectory(ConfigService.ConfigDir);
        var tmp = NotesFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(notes, JsonOptions));
        File.Move(tmp, NotesFile, overwrite: true);
    }

    /// <summary>Visible text of an HTML fragment, for searching and list snippets.</summary>
    public static string ToPlainText(string html)
    {
        var noScripts = Regex.Replace(html, @"<(script|style)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var noTags = Regex.Replace(noScripts, @"<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(noTags), @"\s+", " ").Trim();
    }

    /// <summary>All whitespace-separated terms must appear in the title, tags or text (case-insensitive).</summary>
    public static IEnumerable<NoteItem> Search(IEnumerable<NoteItem> notes, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ordered = notes.OrderByDescending(n => n.Updated);
        if (terms.Length == 0) return ordered;

        return ordered.Where(n =>
        {
            var haystack = $"{n.Title} {n.Tags} {ToPlainText(n.Html)}";
            return terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static readonly Regex BulletLine = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberLine = new(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TimedLine = new(@"^\s*◷ (.+?) · (.*)$", RegexOptions.Compiled);
    private static readonly Regex BlockHtml = new(@"<(p|div|h[1-6]|ul|ol|li|br|table|pre|blockquote)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Turns the editor's plain list lines ("• x", "- x", "1. x", "◷ 2026-10-10 14:30 · x") into HTML lists.
    /// Other lines are left alone when the note already contains block HTML, otherwise they keep their line breaks.
    /// </summary>
    public static string RenderLists(string text)
    {
        var looksLikeHtml = BlockHtml.IsMatch(text);
        var sb = new System.Text.StringBuilder();
        string? openList = null; // the closing tag name of the list that is open
        string? openKind = null;

        void CloseList()
        {
            if (openList is null) return;
            sb.Append("</").Append(openList).Append('>');
            openList = openKind = null;
        }

        void Item(string kind, string tag, string openTag, string content)
        {
            if (openKind != kind)
            {
                CloseList();
                sb.Append(openTag);
                openList = tag;
                openKind = kind;
            }
            sb.Append("<li>").Append(content).Append("</li>");
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            Match m;
            if ((m = TimedLine.Match(line)).Success)
                Item("timed", "ul", "<ul class=\"timed\">",
                    "<span class=\"t\">" + WebUtility.HtmlEncode(m.Groups[1].Value) + "</span> " + m.Groups[2].Value);
            else if ((m = NumberLine.Match(line)).Success)
                Item("num", "ol", "<ol>", m.Groups[1].Value);
            else if ((m = BulletLine.Match(line)).Success)
                Item("bullet", "ul", "<ul>", m.Groups[1].Value);
            else
            {
                CloseList();
                sb.Append(line).Append(looksLikeHtml ? "\n" : "<br>\n");
            }
        }
        CloseList();
        return sb.ToString();
    }

    // ---- Markdown export ----------------------------------------------------

    private static readonly RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    /// <summary>Converts a note body (plain list lines and/or simple HTML) to Markdown.</summary>
    public static string HtmlToMarkdown(string html)
    {
        var md = html.Replace("\r\n", "\n");

        // the editor's own list lines
        md = Regex.Replace(md, @"^(\s*)•\s", "$1- ", RegexOptions.Multiline);
        md = Regex.Replace(md, @"^(\s*)◷ (.+?) · ", "$1- **$2** · ", RegexOptions.Multiline);

        md = Regex.Replace(md, @"<(script|style)\b.*?</\1>", "", Ci);
        md = Regex.Replace(md, @"<pre\b[^>]*>(.*?)</pre>", m => "\n```\n" + Regex.Replace(m.Groups[1].Value, "<[^>]+>", "").Trim('\n') + "\n```\n", Ci);
        md = Regex.Replace(md, @"<h([1-6])\b[^>]*>(.*?)</h\1>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + m.Groups[2].Value.Trim() + "\n\n", Ci);
        md = Regex.Replace(md, @"<(b|strong)\b[^>]*>(.*?)</\1>", "**$2**", Ci);
        md = Regex.Replace(md, @"<(i|em)\b[^>]*>(.*?)</\1>", "*$2*", Ci);
        md = Regex.Replace(md, @"<code\b[^>]*>(.*?)</code>", "`$1`", Ci);
        md = Regex.Replace(md, @"<a\b[^>]*href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a>", "[$2]($1)", Ci);
        md = Regex.Replace(md, @"<img\b[^>]*src\s*=\s*[""']([^""']*)[""'][^>]*>", "![]($1)", Ci);
        // numbered lists keep their numbers, bulleted lists become "- "
        md = Regex.Replace(md, @"<ol\b[^>]*>(.*?)</ol>", m =>
        {
            var n = 0;
            return "\n" + Regex.Replace(m.Groups[1].Value, @"<li\b[^>]*>(.*?)(</li>|(?=<li\b)|$)", li => $"\n{++n}. {li.Groups[1].Value.Trim()}", Ci) + "\n\n";
        }, Ci);
        md = Regex.Replace(md, @"<li\b[^>]*>(.*?)(</li>|(?=<li\b)|$)", li => "\n- " + li.Groups[1].Value.Trim(), Ci);
        md = Regex.Replace(md, @"</?(ul|ol)\b[^>]*>", "\n", Ci);
        md = Regex.Replace(md, @"<br\s*/?>", "\n", Ci);
        md = Regex.Replace(md, @"<hr\s*/?>", "\n\n---\n\n", Ci);
        md = Regex.Replace(md, @"</?(p|div|blockquote|table|tr)\b[^>]*>", "\n\n", Ci);
        md = Regex.Replace(md, @"</t[dh]>\s*<t[dh]\b[^>]*>", " | ", Ci);
        md = Regex.Replace(md, @"<[^>]+>", "");
        md = WebUtility.HtmlDecode(md);
        md = Regex.Replace(md, @"[ \t]+\n", "\n");
        md = Regex.Replace(md, @"\n{3,}", "\n\n");
        return md.Trim();
    }

    /// <summary>One note as a Markdown section: heading, a line of tags and dates, then the body.</summary>
    public static string ToMarkdown(NoteItem note, int headingLevel = 1)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(new string('#', headingLevel)).Append(' ')
          .AppendLine(string.IsNullOrWhiteSpace(note.Title) ? "(untitled)" : note.Title.Trim());
        sb.AppendLine();
        var meta = $"Created {note.Created:yyyy-MM-dd HH:mm} · updated {note.Updated:yyyy-MM-dd HH:mm}";
        if (!string.IsNullOrWhiteSpace(note.Tags))
            meta = string.Join(" ", note.Tags.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => "#" + t.TrimStart('#'))) + " · " + meta;
        sb.Append('_').Append(meta).AppendLine("_");
        sb.AppendLine();
        sb.AppendLine(HtmlToMarkdown(note.Html));
        return sb.ToString();
    }

    /// <summary>Several notes in one Markdown document, newest first.</summary>
    public static string ToMarkdown(IEnumerable<NoteItem> notes)
    {
        var list = notes.OrderByDescending(n => n.Updated).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Notes — exported {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"{list.Count} note(s)");
        foreach (var note in list)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(ToMarkdown(note, headingLevel: 2));
        }
        return sb.ToString();
    }

    /// <summary>A file name made from the title (invalid characters removed).</summary>
    public static string SafeFileName(string title, string fallback)
    {
        var name = string.Concat((title ?? "").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        if (name.Length > 60) name = name[..60].Trim();
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>Writes the note to a temp .html file (wrapping fragments in a document) and returns its path.</summary>
    public static string WritePreview(NoteItem note)
    {
        var body = note.Html;
        var isDocument = body.Contains("<html", StringComparison.OrdinalIgnoreCase);
        var content = isDocument
            ? body
            : "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(note.Title) +
              "</title><style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:760px;margin:2rem auto;padding:0 1rem;line-height:1.5}" +
              "ul.timed{list-style:none;padding-left:0}ul.timed .t{color:#777;font-variant-numeric:tabular-nums;margin-right:.4em}</style></head><body>" +
              RenderLists(body) + "</body></html>";
        var path = Path.Combine(Path.GetTempPath(), $"sad-note-{note.Id:N}.html");
        File.WriteAllText(path, content);
        return path;
    }
}
