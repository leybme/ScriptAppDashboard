using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>One hit of the capture search: where the words were found decides the order (own annotations first).</summary>
public sealed record CaptureHit(CaptureItem Capture, string Where, string Snippet, int Score);

/// <summary>
/// Screenshots are stored in <see cref="CapturesDir"/> (in the data folder, so synced PCs share them): the untouched
/// picture <c>&lt;id&gt;.png</c>, the settings and annotations <c>&lt;id&gt;.json</c>, and <c>&lt;id&gt;.annotated.png</c> with
/// the annotations drawn in (what goes into notes). One file set per capture, so two PCs never overwrite each other.
/// The search reads the JSON files: annotation text, title, tags, the AI description, the window title and the OCR text.
/// </summary>
public static class CaptureService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string CapturesDir => Path.Combine(ConfigService.DataDir, "captures");

    public static string JsonPath(Guid id) => Path.Combine(CapturesDir, id + ".json");
    public static string ImagePath(Guid id) => Path.Combine(CapturesDir, id + ".png");
    public static string AnnotatedPath(Guid id) => Path.Combine(CapturesDir, id + ".annotated.png");

    /// <summary>Raised on a worker thread after a capture was saved or deleted.</summary>
    public static event Action<Guid>? Changed;

    public static List<CaptureItem> Load()
    {
        var list = new Dictionary<Guid, CaptureItem>();
        if (!Directory.Exists(CapturesDir)) return new List<CaptureItem>();
        foreach (var file in Directory.GetFiles(CapturesDir, "*.json"))
        {
            if (Read(file) is not { } item) continue;
            // a sync conflict copy ("<id>-LAPTOP.json") of the same capture: the newer one wins
            if (!list.TryGetValue(item.Id, out var other) || other.Updated < item.Updated) list[item.Id] = item;
        }
        return list.Values.OrderByDescending(c => c.Created).ToList();
    }

    public static CaptureItem? Load(Guid id) => File.Exists(JsonPath(id)) ? Read(JsonPath(id)) : null;

    private static CaptureItem? Read(string file)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<CaptureItem>(File.ReadAllText(file));
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(100); // the sync client is still writing it
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Creates a capture from PNG bytes (the untouched picture) and saves it.</summary>
    public static CaptureItem Create(byte[] png, int width, int height, OpenWindow? source)
    {
        var item = new CaptureItem
        {
            Width = width,
            Height = height,
            SourceApp = source?.App ?? "",
            SourceTitle = source?.Title ?? "",
        };
        Directory.CreateDirectory(CapturesDir);
        WriteBytes(ImagePath(item.Id), png);
        Save(item);
        return item;
    }

    public static void Save(CaptureItem item)
    {
        item.Updated = DateTime.Now;
        Directory.CreateDirectory(CapturesDir);
        ConfigService.AtomicWrite(JsonPath(item.Id), JsonSerializer.Serialize(item, JsonOptions));
        Changed?.Invoke(item.Id);
    }

    /// <summary>Replaces the untouched picture (only after a crop).</summary>
    public static void SaveOriginal(Guid id, byte[] png) => WriteBytes(ImagePath(id), png);

    /// <summary>Saves the picture with the annotations drawn in.</summary>
    public static void SaveAnnotated(Guid id, byte[] png) => WriteBytes(AnnotatedPath(id), png);

    /// <summary>Removes the annotated copy (a capture with no annotations shows the original).</summary>
    public static void DeleteAnnotated(Guid id)
    {
        try { if (File.Exists(AnnotatedPath(id))) File.Delete(AnnotatedPath(id)); }
        catch { /* in use */ }
    }

    public static void Delete(Guid id)
    {
        foreach (var file in new[] { JsonPath(id), ImagePath(id), AnnotatedPath(id) })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* in use: the next delete or sync cleans up */ }
        }
        Changed?.Invoke(id);
    }

    /// <summary>The file to show or insert: the annotated picture when there is one, else the original.</summary>
    public static string BestImage(Guid id) => File.Exists(AnnotatedPath(id)) ? AnnotatedPath(id) : ImagePath(id);

    private static void WriteBytes(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    // ---- search -----------------------------------------------------------------------------

    /// <summary>Captures where every word of <paramref name="query"/> is found in the title, tags, description, annotation
    /// text, window title or OCR text, best matches first.</summary>
    public static List<CaptureHit> Search(IEnumerable<CaptureItem> captures, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = new List<CaptureHit>();
        if (words.Length == 0) return hits;
        foreach (var c in captures)
        {
            var fields = Fields(c);
            if (!words.All(w => fields.Any(f => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))) continue;
            // the best field that holds the first word decides the label and the order
            var best = fields.Where(f => f.Text.Contains(words[0], StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Score).FirstOrDefault() ?? fields.First(f => words.Any(w => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
            hits.Add(new CaptureHit(c, best.Label, Snippet(best.Text, words[0]), best.Score));
        }
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Capture.Created).ToList();
    }

    private sealed record Field(string Label, string Text, int Score);

    private static List<Field> Fields(CaptureItem c)
    {
        var fields = new List<Field>();
        var notes = string.Join("\n", c.Annotations.Where(a => a.Text.Length > 0).Select(a => a.Text));
        if (notes.Length > 0) fields.Add(new Field("annotation", notes, 5));
        if (c.Title.Length > 0) fields.Add(new Field("title", c.Title, 4));
        if (c.Tags.Length > 0) fields.Add(new Field("tags", c.Tags, 4));
        if (c.Description.Length > 0) fields.Add(new Field("description", c.Description, 3));
        if (c.SourceApp.Length + c.SourceTitle.Length > 0) fields.Add(new Field("window", c.SourceApp + " " + c.SourceTitle, 2));
        var visible = VisibleOcr(c);
        if (visible.Count > 0) fields.Add(new Field("text in picture", string.Join("\n", visible.Select(o => o.Text)), 1));
        return fields;
    }

    /// <summary>The text around the first occurrence of <paramref name="word"/>.</summary>
    public static string Snippet(string text, string word)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        var at = Math.Max(0, flat.IndexOf(word, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, at - 30);
        var end = Math.Min(flat.Length, at + 90);
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }

    /// <summary>True when a Redact or Blur annotation covers most of the OCR line: its text is hidden in the picture, so it
    /// must not be searchable, copyable or sent to the AI either.</summary>
    public static bool IsHidden(OcrLine line, IEnumerable<Annotation> annotations)
    {
        var area = Math.Max(1, line.W * line.H);
        foreach (var a in annotations)
        {
            if (a.Kind is not (AnnotationKind.Redact or AnnotationKind.Blur)) continue;
            var w = Math.Min(a.Right, line.X + line.W) - Math.Max(a.Left, line.X);
            var h = Math.Min(a.Bottom, line.Y + line.H) - Math.Max(a.Top, line.Y);
            if (w > 0 && h > 0 && w * h / area >= 0.4) return true;
        }
        return false;
    }

    /// <summary>The OCR lines that are still readable in the picture (not under a Redact / Blur).</summary>
    public static List<OcrLine> VisibleOcr(CaptureItem c) => c.Ocr.Where(l => !IsHidden(l, c.Annotations)).ToList();

    /// <summary>A title for a capture nobody named: what was in front, or the time.</summary>
    public static string DisplayTitle(CaptureItem c)
        => c.Title.Length > 0 ? c.Title
            : c.SourceTitle.Length > 0 ? c.SourceTitle
            : $"Capture {c.Created:yyyy-MM-dd HH:mm}";

    /// <summary>Every text the picture carries as one block, for the AI prompts and a Markdown export.</summary>
    public static string AnnotationText(CaptureItem c)
        => string.Join("\n", c.Annotations.Where(a => a.Text.Length > 0).Select(a => a.Text));
}
