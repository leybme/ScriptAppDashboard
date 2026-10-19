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
public partial class SearchWindow : Window
{
    private App AppRef => (App)Application.Current!;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _version;

    public SearchWindow()
    {
        InitializeComponent();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunSearchAsync();
        };
        QueryBox.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        Closed += (_, _) => _debounce.Stop();
    }

    public void FocusSearch()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { _debounce.Stop(); _ = RunSearchAsync(); e.Handled = true; }
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
            SummaryText.Text = "Type a word or two — every word must match. Notes are searched in full; the date range applies to the work log.";
            return;
        }

        var today = DateTime.Today;
        var from = today.AddDays(-(RangeCombo.SelectedIndex switch { 0 => 6, 2 => 89, 3 => 364, _ => 29 }));
        SummaryText.Text = "Searching…";

        var (notes, hits, captures) = await Task.Run(() =>
        {
            var n = NotesService.Search(NotesService.Load(), query).Take(100).ToList();
            var h = WorkLogService.Search(query, from, today);
            var c = CaptureService.Search(CaptureService.Load(), query).Take(50).ToList();
            return (n, h, c);
        });
        if (version != _version) return; // a newer search started meanwhile

        ResultHost.Children.Clear();
        var windowSeconds = hits.Where(h => h.Kind == "window").Sum(h => h.Seconds);
        SummaryText.Text = $"{notes.Count} note(s) · {captures.Count} capture(s) · {hits.Count} work-log match(es)" +
                           (windowSeconds > 0 ? $" · {WorkLogService.FormatDuration(windowSeconds)} spent in matching windows" : "") +
                           (hits.Count >= 200 ? " · showing the first 200" : "");

        if (notes.Count == 0 && hits.Count == 0 && captures.Count == 0)
        {
            ResultHost.Children.Add(new TextBlock
            {
                Text = "Nothing found. Try fewer words, or a longer date range.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 16),
            });
            return;
        }

        if (notes.Count > 0)
        {
            ResultHost.Children.Add(Header($"Notes ({notes.Count})"));
            foreach (var note in notes) ResultHost.Children.Add(NoteRow(note, query));
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

    private Control NoteRow(NoteItem note, string query)
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
            Text = Snippet(NotesService.ToPlainText(note.Html), query),
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
            if (GetTopLevel(this)?.Clipboard is { } clipboard)
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
    private static string Snippet(string text, string query)
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
