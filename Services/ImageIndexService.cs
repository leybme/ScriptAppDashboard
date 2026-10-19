using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Reads the text of pictures in the background so the AI chat and the search can use it: pictures in notes (text kept
/// in <c>imageocr/&lt;file&gt;.txt</c> of DataDir) and captures that were never OCR'd (stored in the capture). One picture
/// per <see cref="TickAsync"/>, and only while the user has not touched the keyboard or mouse for a while. Never throws.
/// </summary>
public static class ImageIndexService
{
    private const double IdleSecondsNeeded = 15;
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(5);

    private sealed record Job(string? Image, Guid? Capture);

    private static readonly Queue<Job> Queue = new();
    private static DateTime _lastScan = DateTime.MinValue;
    private static volatile bool _busy;

    private static string IndexDir => Path.Combine(ConfigService.DataDir, "imageocr");
    private static string IndexPath(string imageFile) => Path.Combine(IndexDir, Path.GetFileName(imageFile) + ".txt");

    /// <summary>A picture was just added: look for new pictures at the next idle moment instead of in five minutes.</summary>
    public static void RequestScan() => _lastScan = DateTime.MinValue;

    /// <summary>The text found in a note picture (its src), or "" when it has not been read yet or has no text.</summary>
    public static string TextOf(string src)
    {
        try
        {
            return NotesService.ResolveImage(src) is { } file && File.Exists(IndexPath(file)) ? File.ReadAllText(IndexPath(file)) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Removes the text of a deleted picture.</summary>
    public static void Forget(string imageFile)
    {
        try { if (File.Exists(IndexPath(imageFile))) File.Delete(IndexPath(imageFile)); }
        catch { /* next time */ }
    }

    /// <summary>Called by a timer on the UI thread; does at most one OCR run, and none while the user is busy.</summary>
    public static async Task TickAsync()
    {
        if (_busy || !OcrService.IsAvailable || WorkLogService.IdleSeconds() < IdleSecondsNeeded) return;
        _busy = true;
        try
        {
            if (Queue.Count == 0 && DateTime.Now - _lastScan > ScanEvery)
            {
                _lastScan = DateTime.Now;
                await Task.Run(Scan);
            }
            if (!Queue.TryDequeue(out var job)) return;
            if (job.Capture is { } id)
            {
                if (CaptureService.Load(id) is { OcrDone: false } capture) await OcrService.EnsureAsync(capture);
            }
            else if (job.Image is { } file && File.Exists(file) && !File.Exists(IndexPath(file)))
            {
                var lines = await OcrService.ReadAsync(file);
                Directory.CreateDirectory(IndexDir);
                ConfigService.AtomicWrite(IndexPath(file), string.Join("\n", lines.Select(l => l.Text)));
            }
        }
        catch
        {
            // a bonus feature: try again at the next scan
        }
        finally
        {
            _busy = false;
        }
    }

    private static void Scan()
    {
        try
        {
            Queue.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in NotesService.Load().OrderByDescending(n => n.Updated))
                foreach (var src in NotesService.ImageSources(note.Html))
                    if (NotesService.ResolveImage(src) is { } file && NotesService.IsImageFile(file) && File.Exists(file)
                        && !File.Exists(IndexPath(file)) && seen.Add(file))
                        Queue.Enqueue(new Job(file, null));
            foreach (var capture in CaptureService.Load().Where(c => !c.OcrDone))
                Queue.Enqueue(new Job(null, capture.Id));
        }
        catch
        {
            // unreadable folder: next scan
        }
    }

    /// <summary>Only the words found in the pictures of a note, without labels, for searching; "" when none were read yet.</summary>
    public static string SearchTextOfNote(string html)
    {
        var sb = new StringBuilder();
        foreach (var src in NotesService.ImageSources(html).Distinct())
        {
            var text = TextOf(src).Replace('\n', ' ').Trim();
            if (text.Length > 0) sb.Append(text).Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Text of the pictures in a note body, for the AI: "Picture images/x.png contains: …".</summary>
    public static string TextOfNote(string html, int maxChars = 3000)
    {
        var sb = new StringBuilder();
        foreach (var src in NotesService.ImageSources(html).Distinct())
        {
            var text = TextOf(src).Replace('\n', ' ').Trim();
            if (text.Length == 0) continue;
            sb.AppendLine($"- {src} contains the text: {text}");
            if (sb.Length > maxChars) break;
        }
        return sb.Length > maxChars ? sb.ToString(0, maxChars) + "…" : sb.ToString();
    }
}
