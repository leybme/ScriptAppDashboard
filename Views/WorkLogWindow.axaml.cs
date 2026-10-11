using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

public partial class WorkLogWindow : Window
{
    private App AppRef => (App)Application.Current!;
    private WorkLogService.Summary? _summary;
    private bool _loading = true;

    public WorkLogWindow()
    {
        InitializeComponent();
        EnabledCheck.IsChecked = AppRef.Config.WorkLogEnabled;
        EnabledCheck.IsCheckedChanged += EnabledCheck_Changed;
        WorkHoursCheck.IsCheckedChanged += async (_, _) => { if (IsLoaded) await RefreshAsync(); };
        ApplyWorkHoursTip();
        AppRef.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) => AppRef.SettingsChanged -= OnSettingsChanged;
        _loading = false;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void OnSettingsChanged()
    {
        ApplyWorkHoursTip();
        await RefreshAsync();
    }

    private void ApplyWorkHoursTip()
        => ToolTip.SetTip(WorkHoursCheck,
            $"Count only time inside your working hours: {new WorkHours(AppRef.Config).Describe()}\n(change them in Settings → Work log)");

    private void EnabledCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppRef.Config.WorkLogEnabled = EnabledCheck.IsChecked == true;
        try { AppRef.SaveConfig(); } catch (Exception ex) { InfoText.Text = "Could not save the setting: " + ex.Message; }
    }

    private async void Range_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await RefreshAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_summary is null) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(WorkLogService.ToMarkdown(_summary));
        InfoText.Text = "Summary copied — paste it into an AI chat or a note for analysis.";
    }

    private async void AiReport_Click(object? sender, RoutedEventArgs e) => await AskAiAsync(timesheet: false);

    private async void AiTimesheet_Click(object? sender, RoutedEventArgs e) => await AskAiAsync(timesheet: true);

    /// <summary>Sends the period on screen (with its timeline) to the AI; the answer can be saved as a note.</summary>
    private async Task AskAiAsync(bool timesheet)
    {
        if (_summary is not { } s) return;
        if (s.ActiveSeconds == 0)
        {
            InfoText.Text = "Nothing recorded for this range — there is nothing for the AI to report on.";
            return;
        }
        var hours = s.WorkHoursFilter is null ? null : new WorkHours(AppRef.Config);
        var range = s.From == s.To ? $"{s.From:ddd yyyy-MM-dd}" : $"{s.From:yyyy-MM-dd} – {s.To:yyyy-MM-dd}";
        var heading = (timesheet ? "Timesheet " : "Work report ") + range;
        var choice = await AiResultWindow.Show(this, heading, ct => Task.Run(() =>
        {
            var context = WorkLogService.ToAiContext(s, hours);
            return AiService.AskAsync(timesheet ? AiPrompts.Timesheet(context) : AiPrompts.WorkReport(context), ct);
        }, ct), ("note", "Save as note"));
        if (choice is not { } c) return;

        var note = new Models.NoteItem
        {
            Title = heading,
            Tags = timesheet ? "#timesheet #ai" : "#report #ai",
            Html = NoteDocument.ToHtml(NoteDocument.FromMarkdown(AiService.StripOuterFence(c.Text))),
        };
        try
        {
            AppRef.AddNote(note);
            AppRef.ShowNote(note.Id);
        }
        catch (Exception ex)
        {
            InfoText.Text = "Could not save the note: " + ex.Message;
        }
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WorkLogService.LogDir);
            Process.Start(new ProcessStartInfo { FileName = WorkLogService.LogDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InfoText.Text = "Could not open the folder: " + ex.Message;
        }
    }

    private async Task RefreshAsync()
    {
        var today = DateTime.Today;
        var from = RangeCombo.SelectedIndex switch { 1 => today.AddDays(-6), 2 => today.AddDays(-29), _ => today };

        ReportHost.Children.Clear();
        InfoText.Text = WorkLogService.IsSupported
            ? $"Sampled every {AppRef.Config.WorkLogSampleSeconds}s while you are active (idle after {AppRef.Config.WorkLogIdleMinutes} min). " +
              $"Window titles are stored only on this machine in {WorkLogService.LogDir}."
            : "The work log records foreground windows via Win32, so it is only available on Windows. Launches, timers and notes are still logged.";

        var hours = WorkHoursCheck.IsChecked == true ? new WorkHours(AppRef.Config) : null;
        _summary = await Task.Run(() => WorkLogService.Summarize(from, today, hours));
        ReportHost.Children.Clear(); // a second refresh may have started while this one was reading
        var s = _summary;

        if (s.Apps.Count == 0 && s.Launches.Count == 0 && s.TimerRuns.Count == 0 && s.Notes.Count == 0)
        {
            ReportHost.Children.Add(new TextBlock
            {
                Text = !WorkLogService.IsSupported
                    ? "Window tracking is only available on Windows."
                    : !AppRef.Config.WorkLogEnabled
                        ? "Recording is turned off. Tick “Record work log” above to start."
                        : s.OutsideWorkSeconds > 0
                            ? $"Nothing inside your working hours ({s.WorkHoursFilter}) for this range — " +
                              $"{WorkLogService.FormatDuration(s.OutsideWorkSeconds)} was recorded outside them."
                            : "Nothing recorded for this range yet.\n\nThe log fills in while the dashboard runs (it can sit in the tray) and you are actively using the computer — idle time is not counted.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 20),
            });
            return;
        }

        ReportHost.Children.Add(Header($"Active time: {WorkLogService.FormatDuration(s.ActiveSeconds)}"));
        if (s.WorkHoursFilter is not null)
            ReportHost.Children.Add(new TextBlock
            {
                Text = $"Working hours only ({s.WorkHoursFilter}) · {WorkLogService.FormatDuration(s.OutsideWorkSeconds)} outside them not counted",
                FontSize = 12,
                Foreground = Brushes.Gray,
            });

        AddBarSection("Time per app", s.Apps.ConvertAll(a => (a.App, a.Seconds)));
        var byHour = new List<(string, double)>();
        for (var h = 0; h < 24; h++)
            if (s.Hours[h] > 0) byHour.Add(($"{h:00}:00 – {h + 1:00}:00", s.Hours[h]));
        AddBarSection("By hour of day", byHour);
        AddBarSection("Top windows / browser tabs",
            s.Titles.ConvertAll(t => ($"[{t.App}] {Shorten(t.Title)}", t.Seconds)));
        if (s.Days.Count > 1)
            AddBarSection("Per day", s.Days.ConvertAll(d => (d.Day.ToString("ddd yyyy-MM-dd"), d.Seconds)));

        AddCountSection("Launched from the dashboard", s.Launches);
        AddCountSection("Interval timers fired", s.TimerRuns);
        if (s.Notes.Count > 0)
        {
            ReportHost.Children.Add(Header("Notes saved"));
            foreach (var n in s.Notes) ReportHost.Children.Add(new TextBlock { Text = n, FontSize = 12 });
        }
    }

    private void AddBarSection(string title, List<(string Label, double Seconds)> rows)
    {
        if (rows.Count == 0) return;
        ReportHost.Children.Add(Header(title));
        var max = rows.Max(r => r.Seconds);
        foreach (var (label, seconds) in rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
            grid.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
            var time = new TextBlock { Text = WorkLogService.FormatDuration(seconds), Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);

            ReportHost.Children.Add(grid);
            ReportHost.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(max, 1), Value = seconds, Height = 6, ShowProgressText = false });
        }
    }

    private void AddCountSection(string title, List<(string Name, int Count)> rows)
    {
        if (rows.Count == 0) return;
        ReportHost.Children.Add(Header(title));
        foreach (var (name, count) in rows)
            ReportHost.Children.Add(new TextBlock { Text = $"{name} — {count}×", FontSize = 12 });
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 14, 0, 2),
    };

    private static string Shorten(string title) => title.Length <= 90 ? title : title[..90] + "…";
}
