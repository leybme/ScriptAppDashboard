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

public partial class WorkLogView : ShellTab
{
    private App AppRef => (App)Application.Current!;
    private WorkLogService.Summary? _summary;
    private bool _loading = true;

    public WorkLogView()
    {
        InitializeComponent();
        EnabledCheck.IsChecked = AppRef.Config.WorkLogEnabled;
        EnabledCheck.IsCheckedChanged += EnabledCheck_Changed;
        WorkHoursCheck.IsCheckedChanged += async (_, _) => { if (_ready) await RefreshAsync(); };
        ApplyWorkHoursTip();
        AppRef.SettingsChanged += OnSettingsChanged;
        _loading = false;
        _chatProfile = new AiChatProfile
        {
            Watermark = "Ask about this period… (Enter sends, Shift+Enter is a new line)",
            EmptyHint = "Ask about the time on screen, or press Report / Timesheet:\n\n• “What took most of my time?”\n• “Which of this was not work?”\n• “How long did I spend in the browser?”",
            ShareText = "Send this period",
            ShareTip = "The AI reads the summary and timeline of the range on screen. Untick to chat without sharing it.",
            GetContext = ChatContext,
            AnswerActions = new[] { ("note", "Save as note") },
            OnAnswerAction = (_, text) => Task.FromResult(SaveAnswer("Work log " + RangeText(_summary), "#worklog #ai", text)),
            Quick = new (string, string, Action)[]
            {
                ("AI report", "A standup-style report of this period: what you worked on, where focus went, what's next. You can save it as a note.", () => AskAi(timesheet: false)),
                ("AI timesheet", "Group this period into projects / tasks with hours, as a table. You can save it as a note.", () => AskAi(timesheet: true)),
                ("Distractions?", "Ask which apps or windows took time without being work", () => _ = Chat.Ask("Which apps or windows took the most time, and which of them look like distractions?")),
            },
        };
        _chatProfile.SetLabel("Work log");
    }

    private readonly AiChatProfile _chatProfile;
    private bool _ready;

    public override AiChatProfile? ChatProfile => _chatProfile;

    /// <summary>The tab came to the front: read the log again (it grows while the tab is away).</summary>
    public override void OnShown()
    {
        _ready = true;
        _ = RefreshAsync();
    }

    private static string RangeText(WorkLogService.Summary? s)
        => s is null ? "" : s.From == s.To ? $"{s.From:ddd yyyy-MM-dd}" : $"{s.From:yyyy-MM-dd} – {s.To:yyyy-MM-dd}";

    private string AiContext(WorkLogService.Summary s)
        => WorkLogService.ToAiContext(s, s.WorkHoursFilter is null ? null : new WorkHours(AppRef.Config));

    private AiChatContext ChatContext()
        => new(_summary is { } s ? AiContext(s) : "", _summary is null ? "the work log" : "the work log, " + RangeText(_summary),
            null, null, AiPrompts.AppGuide);

    /// <summary>Saves an AI answer as a note and opens it; returns the message for the chat.</summary>
    private string SaveAnswer(string title, string tags, string text)
    {
        var note = AiService.AnswerToNote(title, tags, text);
        try
        {
            AppRef.AddNote(note);
            AppRef.ShowNote(note.Id);
            return $"Saved as the note “{note.Title}”.";
        }
        catch (Exception ex)
        {
            return "Could not save the note: " + ex.Message;
        }
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
        if (!_ready) return;
        await RefreshAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_summary is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(WorkLogService.ToMarkdown(_summary));
        InfoText.Text = "Summary copied — paste it into an AI chat or a note for analysis.";
    }

    /// <summary>Sends the period on screen (with its timeline) to the AI as a turn of the chat; the answer can be saved as a note.</summary>
    private void AskAi(bool timesheet)
    {
        if (_summary is not { } s) return;
        if (s.ActiveSeconds == 0)
        {
            InfoText.Text = "Nothing recorded for this range — there is nothing for the AI to report on.";
            return;
        }
        var heading = (timesheet ? "Timesheet " : "Work report ") + RangeText(s);
        Shell.ShowChat(true);
        Chat.RunRequest(heading, (ct, partial) => Task.Run(() =>
            {
                var context = AiContext(s);
                return AiService.AskStreamAsync(timesheet ? AiPrompts.Timesheet(context) : AiPrompts.WorkReport(context), partial, ct);
            }, ct),
            new[] { ("note", "Save as note") },
            (_, text) => Task.FromResult(SaveAnswer(heading, timesheet ? "#timesheet #ai" : "#report #ai", text)));
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
              (ConfigService.IsSynced
                  ? $"Window titles are stored in your synced data folder ({WorkLogService.LogDir}); every PC that uses it adds its time here."
                  : $"Window titles are stored only on this machine in {WorkLogService.LogDir}.")
            : "The work log records foreground windows via Win32, so it is only available on Windows. Launches, timers and notes are still logged.";

        var hours = WorkHoursCheck.IsChecked == true ? new WorkHours(AppRef.Config) : null;
        _summary = await Task.Run(() => WorkLogService.Summarize(from, today, hours));
        ReportHost.Children.Clear(); // a second refresh may have started while this one was reading
        var s = _summary;
        _chatProfile.SetLabel("Work log: " + RangeText(s));

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
