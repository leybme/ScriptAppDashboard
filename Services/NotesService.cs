using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Notes are stored one JSON file per note in <see cref="NotesDir"/>, so PCs that sync the data folder only clash
/// when they edit the same note. Pictures are files in <see cref="ImagesDir"/>, referenced as "images/name.png".
/// </summary>
public static class NotesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>JSON this app last wrote to each file ("" after deleting it), so watcher events from our own writes are ignored.</summary>
    private static readonly Dictionary<string, string> Written = new(StringComparer.OrdinalIgnoreCase);

    public static string NotesDir => Path.Combine(ConfigService.DataDir, "notes");
    public static string ImagesDir => Path.Combine(ConfigService.DataDir, "images");
    private static string FileFor(Guid id) => Path.Combine(NotesDir, id + ".json");

    public static List<NoteItem> Load()
    {
        MigrateSingleFile();
        var notes = new List<NoteItem>();
        if (!Directory.Exists(NotesDir)) return notes;
        var seen = new HashSet<Guid>();
        // one load at a time: two loads adopting the same conflict copy would make two copies of it
        lock (Gate)
        {
            // shortest names first: "<id>.json" comes before a sync conflict copy like "<id>-LAPTOP.json"
            foreach (var file in Directory.GetFiles(NotesDir, "*.json").OrderBy(f => f.Length))
            {
                if (ReadFile(file) is not { } note) continue;
                if (!IsOwnFile(file, note)) note = AdoptStray(file, note, seen);
                if (seen.Add(note.Id)) notes.Add(note);
            }
        }
        return notes;
    }

    public static void Save(NoteItem note)
    {
        var path = FileFor(note.Id);
        var json = JsonSerializer.Serialize(note, JsonOptions);
        lock (Gate) Written[path] = json;
        ConfigService.AtomicWrite(path, json);
    }

    /// <summary>Deletes the note's file, and its pictures unless another of <paramref name="others"/> shows them too.</summary>
    public static void Delete(NoteItem note, IEnumerable<NoteItem> others)
    {
        var path = FileFor(note.Id);
        lock (Gate) Written[path] = "";
        if (File.Exists(path)) File.Delete(path);
        var stillUsed = others.Where(n => n.Id != note.Id).SelectMany(n => ImageSources(n.Html)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var src in ImageSources(note.Html).Where(s => !stillUsed.Contains(s)))
        {
            try
            {
                if (ResolveImage(src) is { } file && IsInImagesDir(file))
                {
                    File.Delete(file);
                    ImageIndexService.Forget(file);
                }
            }
            catch
            {
                // in use or already gone
            }
        }
    }

    private static bool IsOwnFile(string file, NoteItem note)
        => string.Equals(Path.GetFileNameWithoutExtension(file), note.Id.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A file not named after its note: a sync conflict copy ("<id>-LAPTOP.json") or a renamed file.
    /// It becomes its own note (a copy gets a new id and a marked title), so neither version is lost.</summary>
    private static NoteItem AdoptStray(string file, NoteItem note, HashSet<Guid> seen)
    {
        if (seen.Contains(note.Id) || File.Exists(FileFor(note.Id)))
        {
            note.Id = Guid.NewGuid();
            note.Title = (note.Title.Length > 0 ? note.Title : "Note") + " (conflicting copy)";
        }
        try
        {
            Save(note);
            File.Delete(file);
        }
        catch
        {
            // read-only folder: show it anyway, try again next time
        }
        return note;
    }

    private static NoteItem? ReadFile(string file)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<NoteItem>(File.ReadAllText(file));
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(100); // the sync client is still writing it
            }
            catch
            {
                return null; // not a note (or half-synced): leave the file alone
            }
        }
        return null;
    }

    /// <summary>Splits the old single notes.json (from the data or the settings folder) into one file per note, once.</summary>
    private static void MigrateSingleFile()
    {
        if (Directory.Exists(NotesDir)) return;
        var legacy = new[] { ConfigService.DataDir, ConfigService.ConfigDir }
            .Select(d => Path.Combine(d, "notes.json"))
            .FirstOrDefault(File.Exists);
        if (legacy is null) return;
        lock (Gate)
        {
            if (Directory.Exists(NotesDir)) return;
            List<NoteItem> old;
            try
            {
                old = JsonSerializer.Deserialize<List<NoteItem>>(File.ReadAllText(legacy)) ?? new();
            }
            catch
            {
                // unreadable notes file — keep a copy so nothing is lost, then start empty
                try { File.Copy(legacy, legacy + ".bad", overwrite: true); } catch { }
                return;
            }
            // build the folder under another name first, so a crash halfway doesn't leave a partial notes folder
            var building = NotesDir + ".migrating";
            if (Directory.Exists(building)) Directory.Delete(building, recursive: true);
            Directory.CreateDirectory(building);
            foreach (var note in old)
                File.WriteAllText(Path.Combine(building, note.Id + ".json"), JsonSerializer.Serialize(note, JsonOptions));
            Directory.Move(building, NotesDir);
            File.Move(legacy, legacy + ".migrated", overwrite: true);
        }
    }

    /// <summary>Copies every note and picture into another data folder (when switching to a synced folder).
    /// A note already there is replaced only when this copy is newer.</summary>
    public static int CopyTo(string dataDir)
    {
        var copied = 0;
        var notesDir = Path.Combine(dataDir, "notes");
        Directory.CreateDirectory(notesDir);
        foreach (var note in Load())
        {
            var target = Path.Combine(notesDir, note.Id + ".json");
            if (File.Exists(target) && ReadFile(target) is { } there && there.Updated >= note.Updated) continue;
            ConfigService.AtomicWrite(target, JsonSerializer.Serialize(note, JsonOptions));
            copied++;
        }
        if (Directory.Exists(ImagesDir))
        {
            var imagesDir = Path.Combine(dataDir, "images");
            Directory.CreateDirectory(imagesDir);
            foreach (var file in Directory.GetFiles(ImagesDir))
            {
                var target = Path.Combine(imagesDir, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
        return copied;
    }

    // ---- other PCs' changes ---------------------------------------------------------------

    /// <summary>Raised on a worker thread when another program (usually the sync client) added or changed a note
    /// (the note) or removed one (null).</summary>
    public static event Action<Guid, NoteItem?>? ExternalChange;

    private static FileSystemWatcher? _watcher;
    private static System.Threading.Timer? _debounce;
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts watching the notes folder for changes made by other PCs.</summary>
    public static void Watch()
    {
        try
        {
            _watcher?.Dispose();
            Directory.CreateDirectory(NotesDir);
            _watcher = new FileSystemWatcher(NotesDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => Queue(e.FullPath);
            _watcher.Created += (_, e) => Queue(e.FullPath);
            _watcher.Deleted += (_, e) => Queue(e.FullPath);
            _watcher.Renamed += (_, e) => Queue(e.FullPath);
            _watcher.EnableRaisingEvents = true;
            _debounce ??= new System.Threading.Timer(_ => ProcessPending());
        }
        catch
        {
            // no live updates; the list still refreshes when the notes window is reopened
        }
    }

    private static void Queue(string path)
    {
        lock (Gate) Pending.Add(path);
        _debounce?.Change(700, System.Threading.Timeout.Infinite); // a sync client writes a file in several steps
    }

    private static void ProcessPending()
    {
        string[] paths;
        lock (Gate)
        {
            paths = Pending.ToArray();
            Pending.Clear();
        }
        foreach (var path in paths)
        {
            try
            {
                string? written;
                lock (Gate) written = Written.GetValueOrDefault(path);
                if (!File.Exists(path))
                {
                    if (written == "") continue; // we deleted it
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var gone)) ExternalChange?.Invoke(gone, null);
                    continue;
                }
                var json = File.ReadAllText(path);
                if (json == written) continue; // our own save
                if (ReadFile(path) is not { } note) continue;
                if (!IsOwnFile(path, note))
                {
                    lock (Gate)
                    {
                        if (!File.Exists(path)) continue; // a load adopted it meanwhile
                        note = AdoptStray(path, note, new HashSet<Guid>());
                    }
                }
                lock (Gate) Written[FileFor(note.Id)] = File.Exists(FileFor(note.Id)) ? File.ReadAllText(FileFor(note.Id)) : json;
                ExternalChange?.Invoke(note.Id, note);
            }
            catch
            {
                // locked or vanished meanwhile; a later event will bring it
            }
        }
    }

    // ---- pictures ---------------------------------------------------------------------------

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Copies a picture into the image folder; returns the src for the note ("images/20261011-0930-1a2b3c4d.png").</summary>
    public static string AddImage(string sourceFile)
    {
        var name = NewImageName(Path.GetExtension(sourceFile).ToLowerInvariant());
        Directory.CreateDirectory(ImagesDir);
        File.Copy(sourceFile, Path.Combine(ImagesDir, name));
        ImageIndexService.RequestScan();
        return "images/" + name;
    }

    /// <summary>Saves picture bytes (e.g. a pasted screenshot) into the image folder; returns the src for the note.</summary>
    public static string AddImage(byte[] data, string extension)
    {
        var name = NewImageName(extension);
        Directory.CreateDirectory(ImagesDir);
        File.WriteAllBytes(Path.Combine(ImagesDir, name), data);
        ImageIndexService.RequestScan();
        return "images/" + name;
    }

    private static string NewImageName(string extension)
        => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}{extension}";

    /// <summary>The file behind an image src ("images/x.png", an absolute path or a file:// URL), or null for web images.</summary>
    public static string? ResolveImage(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        if (Uri.TryCreate(src, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath : null;
        try
        {
            return Path.GetFullPath(Path.Combine(ConfigService.DataDir, WebUtility.UrlDecode(src).Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInImagesDir(string file)
        => Path.GetFullPath(file).StartsWith(Path.GetFullPath(ImagesDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static readonly Regex ImgSrc = new(@"(<img\b[^>]*?\bsrc\s*=\s*)([""'])(.*?)\2", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every image src in a note body.</summary>
    public static IEnumerable<string> ImageSources(string html)
        => ImgSrc.Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[3].Value));

    /// <summary>Rewrites the src of every local picture, e.g. to an absolute file URL or an embedded data URL.</summary>
    public static string MapImages(string html, Func<string, string?> map)
        => ImgSrc.Replace(html, m =>
        {
            var src = WebUtility.HtmlDecode(m.Groups[3].Value);
            return ResolveImage(src) is { } file && File.Exists(file) && map(file) is { } mapped
                ? m.Groups[1].Value + m.Groups[2].Value + WebUtility.HtmlEncode(mapped) + m.Groups[2].Value
                : m.Value;
        });

    public static string FileUrl(string file) => new Uri(file).AbsoluteUri;

    /// <summary>The picture embedded as a data: URL, so an exported HTML file is self-contained.</summary>
    public static string? DataUrl(string file)
    {
        var mime = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/png",
        };
        try { return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}"; }
        catch { return null; }
    }

    /// <summary>Deletes pictures no note refers to. Only older ones: a new picture may sit in a note not saved yet,
    /// or in a note another PC hasn't synced yet.</summary>
    public static void DeleteUnusedImages(IEnumerable<NoteItem> notes, TimeSpan minAge)
    {
        if (!Directory.Exists(ImagesDir)) return;
        var used = notes.SelectMany(n => ImageSources(n.Html)).Select(ResolveImage).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(ImagesDir))
        {
            try
            {
                if (!used.Contains(Path.GetFullPath(file)) && DateTime.Now - File.GetLastWriteTime(file) > minAge)
                {
                    File.Delete(file);
                    ImageIndexService.Forget(file);
                }
            }
            catch
            {
                // in use; next time
            }
        }
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

    // ---- Markdown export ----------------------------------------------------

    private static readonly RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    /// <summary>Converts a note body (editor HTML, old plain list lines, or any HTML) to Markdown.</summary>
    public static string HtmlToMarkdown(string html)
        => NoteDocument.ToMarkdown(NoteDocument.Parse(html), RawHtmlToMarkdown);

    /// <summary>
    /// Markdown for the AI that survives the trip back through <see cref="NoteDocument.FromMarkdown"/>: charts stay
    /// ```chart fences and HTML the editor keeps as source stays HTML (without blank lines, which would end the block).
    /// </summary>
    public static string NoteToAiMarkdown(string html) => BlocksToAiMarkdown(NoteDocument.Parse(html));

    /// <inheritdoc cref="NoteToAiMarkdown"/>
    public static string BlocksToAiMarkdown(IReadOnlyList<NoteBlock> blocks)
        => NoteDocument.ToMarkdown(blocks, raw => Regex.Replace(raw.Trim(), @"\n\s*\n", "\n"), chartFences: true);

    /// <summary>Best-effort Markdown for HTML the editor keeps as source (tables, styled blocks, full documents).</summary>
    private static string RawHtmlToMarkdown(string html)
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
        sb.AppendLine(HtmlToMarkdown(MapImages(note.Html, FileUrl)));
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

    /// <summary>Creates a complete HTML document with a visible title. Pictures are embedded, or with
    /// <paramref name="linkImages"/> linked by file URL (fine for a preview on this PC).</summary>
    public static string ToHtml(NoteItem note, bool linkImages = false)
    {
        var body = note.Html;
        var isDocument = body.Contains("<html", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(note.Title) ? "(untitled)" : note.Title.Trim();
        var encodedTitle = WebUtility.HtmlEncode(title);
        var heading = "<h1>" + encodedTitle + "</h1>";
        string content;
        if (isDocument)
        {
            if (Regex.IsMatch(body, @"<body\b", RegexOptions.IgnoreCase))
                content = Regex.Replace(body, @"<body\b[^>]*>", m => m.Value + heading, RegexOptions.IgnoreCase);
            else if (Regex.IsMatch(body, @"</head\s*>", RegexOptions.IgnoreCase))
                content = Regex.Replace(body, @"</head\s*>", m => m.Value + "<body>" + heading + "</body>", RegexOptions.IgnoreCase);
            else
                content = Regex.Replace(body, @"<html\b[^>]*>", m => m.Value + "<body>" + heading + "</body>", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(content, @"<title\b[^>]*>.*?</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                content = Regex.Replace(content, @"<title\b[^>]*>.*?</title>", "<title>" + encodedTitle + "</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            else
                content = Regex.Replace(content, @"<head\b[^>]*>", m => m.Value + "<title>" + encodedTitle + "</title>", RegexOptions.IgnoreCase);
        }
        else
        {
            content = "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + encodedTitle +
              "</title><style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:760px;margin:2rem auto;padding:0 1rem;line-height:1.5}" +
              "h1{border-bottom:1px solid #ccc;padding-bottom:.4em}ul.timed{list-style:none;padding-left:0}ul.timed .t{color:#777;font-variant-numeric:tabular-nums;margin-right:.4em}" +
              "blockquote{margin:0;padding-left:1em;border-left:3px solid #ccc;color:#555}pre{background:#f4f4f4;padding:.6em .8em;border-radius:4px;overflow:auto}" +
              "code{font-family:Consolas,Menlo,monospace}img{max-width:100%;height:auto}" +
              "table{border-collapse:collapse;margin:.6em 0}th,td{border:1px solid #ccc;padding:.3em .6em;text-align:left;vertical-align:top}th{background:#f4f4f4}" +
              "figure.chart{margin:1.2em 0}figure.chart figcaption{font-weight:600;margin-bottom:.4em}figure.chart summary{color:#777;font-size:.9em;cursor:pointer}</style></head><body>" +
              heading + NoteDocument.ToHtml(NoteDocument.Parse(body), b => ChartLayout.ToSvg(b)) + "</body></html>";
        }
        return MapImages(content, linkImages ? FileUrl : DataUrl);
    }

    /// <summary>Writes the note to a temp .html file and returns its path.</summary>
    public static string WritePreview(NoteItem note)
    {
        var content = ToHtml(note, linkImages: true);
        var path = Path.Combine(Path.GetTempPath(), $"sad-note-{note.Id:N}.html");
        File.WriteAllText(path, content);
        return path;
    }
}
