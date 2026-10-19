using System;
using System.Collections.Generic;
using System.Linq;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>What one search found, in every place it looks.</summary>
public sealed record SearchResults(
    string Query,
    List<ScriptItem> Apps,
    List<NoteItem> Notes,
    Dictionary<Guid, string> PictureText,
    List<CaptureHit> Captures,
    List<WorkLogService.LogHit> Hits)
{
    public int Total => Apps.Count + Notes.Count + Captures.Count + Hits.Count;
}

/// <summary>
/// One search over the apps (dashboard tiles), notes (including the words OCR found in their pictures), captures and the work log.
/// Used by the Search tab and by the chat's <c>@</c> quick search. Reads files: call it off the UI thread.
/// </summary>
public static class SearchService
{
    /// <param name="scripts">A snapshot of the dashboard's tiles (the list itself belongs to the UI thread).</param>
    public static SearchResults Run(string query, IReadOnlyList<ScriptItem> scripts, DateTime workLogFrom, DateTime workLogTo)
    {
        var terms = NotesService.Terms(query);
        if (terms.Length == 0)
            return new SearchResults(query, new(), new(), new(), new(), new());

        var apps = scripts.Where(s =>
        {
            var text = $"{s.Name} {s.ScriptPath} {s.Arguments} {(s.Interpreter == "custom" ? s.CustomCommand : s.Interpreter)}";
            return terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        }).ToList();

        // a note also matches through the words OCR found in its pictures (when that text was read already)
        var notes = NotesService.Search(NotesService.Load(), query, note => ImageIndexService.SearchTextOfNote(note.Html)).Take(100).ToList();
        // for notes that match only through a picture, the picture text is what to show
        var pictures = notes.Where(note => !NotesService.Matches(note, terms))
            .ToDictionary(note => note.Id, note => ImageIndexService.SearchTextOfNote(note.Html));

        var hits = WorkLogService.Search(query, workLogFrom, workLogTo);
        var captures = CaptureService.Search(CaptureService.Load(), query).Take(50).ToList();
        return new SearchResults(query, apps, notes, pictures, captures, hits);
    }

    /// <summary>The text around the first matching word, so you can see why a result matched.</summary>
    public static string Snippet(string text, string query)
    {
        var first = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, first - 40);
        var end = Math.Min(text.Length, first + 100);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}
