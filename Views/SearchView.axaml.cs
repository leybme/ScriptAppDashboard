using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>One search box over notes and the work log (window titles / tabs, launches, timer runs).</summary>
public partial class SearchView : ShellTab
{
    private App AppRef => (App)Application.Current!;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _version;

    public SearchView()
    {
        InitializeComponent();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunSearchAsync();
        };
        QueryBox.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _chatProfile = new AiChatProfile
        {
            Watermark = "Ask about what you searched for… (Enter sends, Shift+Enter is a new line)",
            EmptyHint = "The AI searches your notes, screenshots and the words in your pictures for every question:\n\n• “What did I decide about the invoice template?”\n• “Where did I write down the server password hint?”",
            ShareText = "Send the search",
            ShareTip = "The AI sees what you searched for and what was found. Untick to chat without sharing it.",
            GetContext = () => new AiChatContext(SearchText(), "the search page", null, null, AiPrompts.AppGuide),
            AnswerActions = new[] { ("note", "Save as note") },
            OnAnswerAction = (_, text) =>
            {
                var note = AiService.AnswerToNote("Search: " + (QueryBox.Text ?? "").Trim(), "#search #ai", text);
                try
                {
                    AppRef.AddNote(note);
                    return Task.FromResult($"Saved as the note “{note.Title}”.");
                }
                catch (Exception ex)
                {
                    return Task.FromResult("Could not save the note: " + ex.Message);
                }
            },
        };
        _chatProfile.SetLabel("Search");
    }

    private readonly AiChatProfile _chatProfile;

    public override AiChatProfile? ChatProfile => _chatProfile;

    public override void OnShown() => FocusSearch();

    /// <summary>What the chat is told: the words searched for, and the result list as text.</summary>
    private string SearchText()
    {
        var query = (QueryBox.Text ?? "").Trim();
        if (query.Length == 0) return "(nothing searched for yet)";
        return $"Searched for: {query}\n{SummaryText.Text}\n" + string.Join("\n", _resultLines);
    }

    private List<string> _resultLines = new();

    public void FocusSearch()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    /// <summary>Starts a search for these words (the chat's "Open in Search tab").</summary>
    public void SearchFor(string query)
    {
        QueryBox.Text = query;
        _debounce.Stop();
        _ = RunSearchAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _debounce.Stop(); _ = RunSearchAsync(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private async void Range_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var query = (QueryBox.Text ?? "").Trim();
        var version = ++_version;
        if (query.Length == 0)
        {
            ResultHost.Children.Clear();
            SummaryText.Text = "Type a word or two — every word must match. Apps (your tiles), notes (including the words in their pictures) and captures are searched in full; the date range applies to the work log.";
            return;
        }

        var today = DateTime.Today;
        var from = today.AddDays(-(RangeCombo.SelectedIndex switch { 0 => 6, 2 => 89, 3 => 364, _ => 29 }));
        SummaryText.Text = "Searching…";

        var tiles = AppRef.Config.Scripts.ToList(); // a snapshot: the list belongs to the UI thread
        var results = await Task.Run(() => SearchService.Run(query, tiles, from, today));
        if (version != _version) return; // a newer search started meanwhile
        var (apps, notes, pictureText, captures, hits) = (results.Apps, results.Notes, results.PictureText, results.Captures, results.Hits);

        _resultLines = apps.Take(15).Select(s => $"App “{s.Name}”: {s.ScriptPath} {s.Arguments}".TrimEnd())
            .Concat(notes.Take(30).Select(n => pictureText.TryGetValue(n.Id, out var pics)
                ? $"Note “{n.Title}” ({n.Updated:yyyy-MM-dd}), in a picture: {Snippet(pics, query)}"
                : $"Note “{n.Title}” ({n.Updated:yyyy-MM-dd}): {Snippet(NotesService.ToPlainText(n.Html), query)}"))
            .Concat(captures.Take(15).Select(h => $"Capture “{CaptureService.DisplayTitle(h.Capture)}”: {h.Where}: {h.Snippet}"))
            .Concat(hits.Take(30).Select(h => $"Work log {h.Day:yyyy-MM-dd} [{h.App}] {h.Text}"))
            .ToList();
        ResultHost.Children.Clear();
        var windowSeconds = hits.Where(h => h.Kind == "window").Sum(h => h.Seconds);
        SummaryText.Text = $"{apps.Count} app(s) · {notes.Count} note(s) · {captures.Count} capture(s) · {hits.Count} work-log match(es)" +
                           (windowSeconds > 0 ? $" · {WorkLogService.FormatDuration(windowSeconds)} spent in matching windows" : "") +
                           (hits.Count >= 200 ? " · showing the first 200" : "");

        if (apps.Count == 0 && notes.Count == 0 && hits.Count == 0 && captures.Count == 0)
        {
            ResultHost.Children.Add(new TextBlock
            {
                Text = "Nothing found. Try fewer words, or a longer date range.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 16),
            });
            return;
        }

        if (apps.Count > 0)
        {
            ResultHost.Children.Add(Header($"Apps ({apps.Count})"));
            foreach (var app in apps) ResultHost.Children.Add(AppRow(app));
        }

        if (notes.Count > 0)
        {
            ResultHost.Children.Add(Header($"Notes ({notes.Count})"));
            foreach (var note in notes)
                ResultHost.Children.Add(NoteRow(note, query, pictureText.GetValueOrDefault(note.Id)));
        }

        if (captures.Count > 0)
        {
            ResultHost.Children.Add(Header($"Captures ({captures.Count})"));
            foreach (var capture in captures) ResultHost.Children.Add(CaptureRow(capture));
        }

        if (hits.Count > 0)
        {
            ResultHost.Children.Add(Header($"Work log ({hits.Count})"));
            DateTime? day = null;
            foreach (var hit in hits)
            {
                if (hit.Day != day)
                {
                    day = hit.Day;
                    var daySeconds = hits.Where(h => h.Day == hit.Day).Sum(h => h.Seconds);
                    ResultHost.Children.Add(new TextBlock
                    {
                        Text = $"{hit.Day:dddd yyyy-MM-dd}" + (daySeconds > 0 ? $" · {WorkLogService.FormatDuration(daySeconds)}" : ""),
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 8, 0, 0),
                    });
                }
                ResultHost.Children.Add(HitRow(hit));
            }
        }
    }

    /// <summary>A tile of the dashboard; a click runs it, as the tile does.</summary>
    private Control AppRow(ScriptItem app)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock
        {
            Text = "▶ " + app.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{app.ScriptPath} {app.Arguments}".Trim(),
            FontSize = 12,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var row = Row(panel, "Run this app");
        ToolTip.SetTip(row, "Run it — the same as clicking its tile");
        row.Click += (_, _) =>
        {
            try
            {
                LauncherService.Launch(app);
                SummaryText.Text = $"Launched “{app.Name}”.";
            }
            catch (Exception ex)
            {
                SummaryText.Text = $"Failed to launch “{app.Name}”: {ex.Message}";
            }
        };
        return row;
    }

    private Control NoteRow(NoteItem note, string query, string? pictureText = null)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title) ? "(untitled)" : note.Title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = pictureText is null
                ? Snippet(NotesService.ToPlainText(note.Html), query)
                : "🖼 in a picture: " + Snippet(pictureText, query),
            FontSize = 12,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{note.Updated:yyyy-MM-dd HH:mm}   {note.Tags}".TrimEnd(),
            FontSize = 11,
            Foreground = Brushes.Gray,
        });
        var row = Row(panel, "Open this note");
        row.Click += (_, _) => AppRef.ShowNote(note.Id);
        return row;
    }

    private Control CaptureRow(CaptureHit hit)
    {
        var c = hit.Capture;
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock
        {
            Text = "📷 " + CaptureService.DisplayTitle(c),
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{hit.Where}: {hit.Snippet}",
            FontSize = 12,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{c.Created:yyyy-MM-dd HH:mm}   {c.Tags}".TrimEnd(),
            FontSize = 11,
            Foreground = Brushes.Gray,
        });
        var row = Row(panel, "Open this capture");
        row.Click += (_, _) => AppRef.ShowCapture(c.Id);
        return row;
    }

    private Control HitRow(WorkLogService.LogHit hit)
    {
        var (icon, label) = hit.Kind switch
        {
            "launch" => ("▶", $"Launched {hit.Text}"),
            "timer" => ("⏱", $"Timer {hit.Text}"),
            _ => ("▢", $"[{hit.App}] {hit.Text}"),
        };
        var when = hit.First == hit.Last ? $"{hit.First:HH:mm}" : $"{hit.First:HH:mm}–{hit.Last:HH:mm}";
        var detail = hit.Kind == "window"
            ? $"{when} · {WorkLogService.FormatDuration(hit.Seconds)}"
            : $"{when}" + (hit.Count > 1 ? $" · {hit.Count}×" : "");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new TextBlock { Text = icon, Width = 22, Foreground = Brushes.Gray });
        var text = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var time = new TextBlock { Text = detail, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var row = Row(grid, "Copy this line");
        ToolTip.SetTip(row, label + "\n(click to copy)");
        row.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync($"{hit.Day:yyyy-MM-dd} {detail} {label}");
                SummaryText.Text = "Copied: " + label;
            }
        };
        return row;
    }

    private static Button Row(Control content, string automationName)
    {
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 5),
        };
        Avalonia.Automation.AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 12, 0, 2),
    };

    /// <summary>The text around the first matching word, so you can see why a note matched.</summary>
    private static string Snippet(string text, string query) => SearchService.Snippet(text, query);
}
