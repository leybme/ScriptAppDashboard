using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>The "@" quick search of the chat: results as rows in the conversation, with no AI involved.</summary>
public partial class AiChatPanel
{
    private const int RowsPerGroup = 5;

    private App AppRef => (App)Application.Current!;

    /// <summary>Searches for the words, in the background, and shows what was found as a turn of the conversation.</summary>
    private async Task QuickSearchAsync(string query)
    {
        var list = Turns;
        var turn = new Turn { Question = "@" + query, State = State.Search };
        list.Add(turn);
        StatusText.Text = "";
        if (ReferenceEquals(Turns, list)) Render();
        if (query.Length == 0) return;

        var tiles = AppRef.Config.Scripts.ToList(); // a snapshot: the list belongs to the UI thread
        var today = DateTime.Today;
        try
        {
            turn.Search = await Task.Run(() => SearchService.Run(query, tiles, today.AddDays(-29), today));
        }
        catch (Exception ex)
        {
            turn.State = State.Failed;
            turn.Answer = "Search failed: " + ex.Message;
        }
        if (ReferenceEquals(Turns, list)) Render();
    }

    private Control SearchReply(Turn turn)
    {
        var panel = new StackPanel { Spacing = 4 };
        var query = turn.Question[1..];
        if (query.Length == 0)
        {
            panel.Children.Add(Note("Type a word after @, for example @invoice. Every word must match."));
            return panel;
        }
        if (turn.Search is not { } found)
        {
            panel.Children.Add(Note("Searching…"));
            return panel;
        }
        if (found.Total == 0)
        {
            panel.Children.Add(Note($"Nothing found for “{query}”. Try fewer words."));
            return panel;
        }

        panel.Children.Add(Note($"{found.Apps.Count} app(s) · {found.Notes.Count} note(s) · {found.Captures.Count} capture(s) · {found.Hits.Count} work-log match(es)"));

        void Group(string title, int count, IEnumerable<Control> rows)
        {
            if (count == 0) return;
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var row in rows) panel.Children.Add(row);
            if (count > RowsPerGroup) panel.Children.Add(Note($"…and {count - RowsPerGroup} more — open the Search tab to see all."));
        }

        Group("Apps", found.Apps.Count, found.Apps.Take(RowsPerGroup).Select(app => ResultRow(
            "▶ " + app.Name, $"{app.ScriptPath} {app.Arguments}".Trim(), "Run this app", () => RunApp(app))));
        Group("Notes", found.Notes.Count, found.Notes.Take(RowsPerGroup).Select(note => ResultRow(
            string.IsNullOrWhiteSpace(note.Title) ? "(untitled)" : note.Title,
            found.PictureText.TryGetValue(note.Id, out var pictures)
                ? "🖼 in a picture: " + SearchService.Snippet(pictures, query)
                : SearchService.Snippet(NotesService.ToPlainText(note.Html), query),
            "Open this note", () => AppRef.ShowNote(note.Id))));
        Group("Captures", found.Captures.Count, found.Captures.Take(RowsPerGroup).Select(hit => ResultRow(
            "📷 " + CaptureService.DisplayTitle(hit.Capture), $"{hit.Where}: {hit.Snippet}",
            "Open this capture", () => AppRef.ShowCapture(hit.Capture.Id))));
        if (found.Hits.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Work log", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var hit in found.Hits.Take(RowsPerGroup))
                panel.Children.Add(Note($"{hit.Day:MMM d} [{hit.App}] {hit.Text}"));
            if (found.Hits.Count > RowsPerGroup) panel.Children.Add(Note($"…and {found.Hits.Count - RowsPerGroup} more — open the Search tab to see all."));
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SmallButton("Open in Search tab", "Show everything that was found, with the date range of the work log", () =>
        {
            if (TopLevel.GetTopLevel(this) is not MainWindow shell) return;
            shell.ShowTab("search");
            shell.Tab<SearchView>("search").SearchFor(query);
        }));
        panel.Children.Add(buttons);
        return panel;
    }

    private void RunApp(ScriptItem app)
    {
        try
        {
            LauncherService.Launch(app);
            StatusText.Text = $"Launched “{app.Name}”.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to launch “{app.Name}”: {ex.Message}";
        }
    }

    /// <summary>One result: a title and a line of detail; a click opens or runs it.</summary>
    private static Control ResultRow(string title, string detail, string automationName, Action click)
    {
        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4),
        };
        AutomationProperties.SetName(button, automationName);
        ToolTip.SetTip(button, automationName);
        button.Click += (_, _) => click();
        return button;
    }
}
