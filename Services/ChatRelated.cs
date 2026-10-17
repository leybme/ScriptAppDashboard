using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>
/// What the AI chat is told besides the open note: the notes and captures that match the words of the question, and
/// the text found in the open note's pictures. The AI cannot search by itself, so the app does it before each question.
/// </summary>
public static class ChatRelated
{
    private const int MaxNotes = 4, MaxCaptures = 3, NoteExcerpt = 500;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "this", "with", "what", "which", "when", "where", "how", "does", "did", "can", "could",
        "you", "your", "are", "was", "were", "have", "has", "from", "about", "into", "make", "show", "tell", "give", "find",
        "note", "notes", "please", "all", "any", "not", "but", "then", "than", "there", "their", "them", "will", "would",
    };

    /// <summary>The words of a question worth searching for (3+ letters, no filler), at most 6.</summary>
    public static List<string> Keywords(string question)
        => question.Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '?', '!', '"', '\'', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();

    /// <summary>A block of text for the prompt, or "" when nothing matches. Runs file reads: call it off the UI thread.</summary>
    public static string Build(string question, Guid? openNote, string openNoteHtml)
    {
        var sb = new StringBuilder();
        var words = Keywords(question);
        if (words.Count > 0)
        {
            try { AddNotes(sb, words, openNote); } catch { /* optional context */ }
            try { AddCaptures(sb, words); } catch { /* optional context */ }
        }
        try
        {
            var pictures = ImageIndexService.TextOfNote(openNoteHtml);
            if (pictures.Length > 0)
            {
                sb.AppendLine("Text found by OCR in the pictures of the open note (you cannot see the pictures themselves):");
                sb.AppendLine(pictures);
            }
        }
        catch { /* optional context */ }
        return sb.ToString().TrimEnd();
    }

    private static void AddNotes(StringBuilder sb, List<string> words, Guid? openNote)
    {
        var scored = new List<(NoteItem Note, string Text, int Score)>();
        foreach (var note in NotesService.Load())
        {
            if (note.Id == openNote) continue;
            var text = NotesService.ToPlainText(note.Html);
            var score = 0;
            foreach (var w in words)
            {
                if (note.Title.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (note.Tags.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) score += 1;
            }
            if (score > 0) scored.Add((note, text, score));
        }
        var best = scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Note.Updated).Take(MaxNotes).ToList();
        if (best.Count == 0) return;
        sb.AppendLine("Other notes that match the question (id, title, excerpt):");
        foreach (var (note, text, _) in best)
            sb.AppendLine($"- [{note.Id}] “{note.Title}” ({note.Updated:yyyy-MM-dd}): {Excerpt(text, words)}");
        sb.AppendLine();
    }

    private static void AddCaptures(StringBuilder sb, List<string> words)
    {
        var captures = CaptureService.Load();
        if (captures.Count == 0) return;
        var scores = new Dictionary<Guid, (CaptureItem Capture, int Score)>();
        foreach (var w in words)
            foreach (var hit in CaptureService.Search(captures, w))
            {
                scores.TryGetValue(hit.Capture.Id, out var s);
                scores[hit.Capture.Id] = (hit.Capture, s.Score + hit.Score);
            }
        var best = scores.Values.OrderByDescending(s => s.Score).ThenByDescending(s => s.Capture.Created).Take(MaxCaptures).ToList();
        if (best.Count == 0) return;
        sb.AppendLine("Screenshots (captures) that match the question (id, title, what they show, text in the picture):");
        foreach (var (c, _) in best)
        {
            var title = c.Title.Length > 0 ? c.Title : c.SourceTitle;
            var annotations = string.Join("; ", c.Annotations.Where(a => a.Text.Length > 0).Select(a => a.Text));
            var ocr = string.Join(" ", c.Ocr.Where(l => !CaptureService.IsHidden(l, c.Annotations)).Select(l => l.Text));
            sb.Append($"- [{c.Id}] “{title}” ({c.Created:yyyy-MM-dd}, {c.SourceApp})");
            if (c.Description.Length > 0) sb.Append($" — {c.Description}");
            if (annotations.Length > 0) sb.Append($" — annotations: {annotations}");
            if (ocr.Length > 0) sb.Append($" — text: {(ocr.Length > 400 ? ocr[..400] + "…" : ocr)}");
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    /// <summary>About <see cref="NoteExcerpt"/> characters around the first matching word.</summary>
    private static string Excerpt(string text, List<string> words)
    {
        var at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 80);
        var end = Math.Min(text.Length, start + NoteExcerpt);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}
