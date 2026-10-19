using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

public partial class TimersWindow : Window
{
    private static readonly string[] Actions = { "beep", "command", "ai" };

    private App AppRef => (App)Application.Current!;
    private AppConfig Config => AppRef.Config;
    private IntervalTimerItem? _current;
    private bool _suppress;

    private readonly Dictionary<Guid, (IntervalTimerItem Timer, TextBlock Detail)> _rows = new();
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    public TimersWindow()
    {
        InitializeComponent();
        PlaceholderHint.Text = "Filled in when the timer fires: " +
                               string.Join(" · ", AiService.Placeholders.Select(p => $"{p.Name} {p.Description}"));
        _dock = new AiPanelDock(this, Chat);
        Chat.UseGeneral("Ask about timers, cron expressions or commands… (Enter sends, Shift+Enter is a new line)",
            "Ask about your timers:\n\n• “Cron expression for weekdays at 9:30”\n• “A command that shows free disk space”\n• “Which timers fire too often?”",
            "Send my timers", "The AI sees every timer's name, schedule and action, and the one in the editor. Untick to chat without sharing it.");
        Chat.GetContext = () => new AiChatContext(TimersText(), "the interval and cron timers", null, null, AiPrompts.AppGuide);
        Chat.SetQuickActions(
            ("Cron for…", "Ask for a cron expression", () => Chat.Prefill("Cron expression for ")),
            ("Command for…", "Ask for a shell command to use in a timer", () => Chat.Prefill("A command that ")));
        ApplyAiCommandHint();
        AppRef.SettingsChanged += ApplyAiCommandHint;
        AppRef.TimersReloaded += OnTimersReloaded;
        DescribeBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Describe_Click(null, new RoutedEventArgs());
        };
        RefreshList(null);
        TimerService.Ran += OnTimerRan;
        _countdown.Tick += (_, _) => UpdateCountdowns();
        _countdown.Start();
        ApplyPause();
        AppRef.TimersPauseChanged += ApplyPause;
        Closed += (_, _) =>
        {
            TimerService.Ran -= OnTimerRan;
            AppRef.TimersPauseChanged -= ApplyPause;
            AppRef.SettingsChanged -= ApplyAiCommandHint;
            AppRef.TimersReloaded -= OnTimersReloaded;
            _countdown.Stop();
        };
    }

    private readonly AiPanelDock _dock;

    private void Chat_Click(object? sender, RoutedEventArgs e) => _dock.Toggle();

    /// <summary>What the chat is told: every saved timer, and the one in the editor if it isn't saved yet.</summary>
    private string TimersText()
    {
        static string Line(IntervalTimerItem t) =>
            $"- {(string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name)}: " +
            (string.IsNullOrWhiteSpace(t.Cron) ? $"every {FormatInterval(t.IntervalMinutes)}" : "cron " + t.Cron) +
            $", {Describe(t.Action)}" + (t.Action == "command" ? $" `{t.Command}`" : "") + (t.Enabled ? "" : " (off)");
        var lines = Config.Timers.Select(Line).ToList();
        if (_current is null) lines.Add("In the editor, not saved yet:\n" + Line(ReadEditor(new IntervalTimerItem())));
        return lines.Count == 0 ? "(no timers yet)" : string.Join("\n", lines);
    }

    private void ApplyPause()
    {
        var paused = TimerService.IsPaused;
        PauseButton.IsVisible = !paused;
        ResumeButton.IsVisible = paused;
        if (paused) ResumeButton.Content = $"Resume  ·  {App.PauseText(TimerService.PausedUntil!.Value).ToLowerInvariant()}";
        UpdateCountdowns();
    }

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        TimerService.Pause(tag switch
        {
            "forever" => null,
            "tomorrow" => DateTime.Today.AddDays(1).AddHours(6) - DateTime.Now,
            _ => TimeSpan.FromMinutes(int.Parse(tag)),
        });
    }

    private void Resume_Click(object? sender, RoutedEventArgs e) => TimerService.Resume();

    /// <summary>Refreshes the "next in 4:32" part of each row without rebuilding the list.</summary>
    private void UpdateCountdowns()
    {
        foreach (var (timer, detail) in _rows.Values) detail.Text = DetailText(timer);
    }

    private static string DetailText(IntervalTimerItem timer)
    {
        var when = string.IsNullOrWhiteSpace(timer.Cron) ? $"every {FormatInterval(timer.IntervalMinutes)}" : "cron " + timer.Cron.Trim();
        var text = $"{when} · {Describe(timer.Action)}";
        if (!timer.Enabled) return text + " · off";
        if (TimerService.IsPaused) return text + " · paused";
        var left = TimerService.TimeUntilNext(timer);
        if (left is null) return text;
        var s = Math.Max(0, (int)Math.Ceiling(left.Value.TotalSeconds));
        return text + " · next in " + (s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}");
    }

    private void OnTimerRan(IntervalTimerItem timer, string output)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshList(_current?.Id, keepEditor: true);
            if (_current?.Id == timer.Id) OutputBox.Text = TimerService.GetLastOutput(timer.Id);
        });
    }

    private void RefreshList(Guid? select, bool keepEditor = false)
    {
        _suppress = true;
        try
        {
            TimerList.Items.Clear();
            _rows.Clear();
            ListBoxItem? toSelect = null;
            foreach (var timer in Config.Timers)
            {
                var panel = new StackPanel { Spacing = 1 };
                var name = new TextBlock
                {
                    Text = (timer.Enabled ? "● " : "○ ") + (string.IsNullOrWhiteSpace(timer.Name) ? "(unnamed)" : timer.Name),
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                // only override when disabled: a null Foreground would hide the text instead of using the theme colour
                if (!timer.Enabled) name.Foreground = Brushes.Gray;
                panel.Children.Add(name);
                var detail = new TextBlock
                {
                    Text = DetailText(timer),
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                panel.Children.Add(detail);
                _rows[timer.Id] = (timer, detail);
                var item = new ListBoxItem { Content = panel, Tag = timer };
                TimerList.Items.Add(item);
                if (timer.Id == select) toSelect = item;
            }
            TimerList.SelectedItem = toSelect;
            EmptyHint.IsVisible = Config.Timers.Count == 0;
        }
        finally
        {
            _suppress = false;
        }
        if (!keepEditor && select is null) LoadEditor(null);
    }

    private static string FormatInterval(double minutes)
    {
        var seconds = Math.Round(minutes * 60);
        if (seconds < 120) return $"{seconds:0} s";
        if (seconds % 3600 == 0) return $"{seconds / 3600:0} h";
        return $"{minutes:0.##} min";
    }

    private static string Describe(string action) => action switch
    {
        "command" => "command",
        "ai" => "AI prompt",
        _ => "beep",
    };

    private void TimerList_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (TimerList.SelectedItem is not ListBoxItem { Tag: IntervalTimerItem timer }) return;
        _current = timer;
        LoadEditor(timer);
    }

    private void LoadEditor(IntervalTimerItem? timer)
    {
        _suppress = true;
        var t = timer ?? new IntervalTimerItem();
        NameBox.Text = t.Name;
        // show the interval in the most natural unit: 30 s, 20 min, 2 h
        var seconds = Math.Round(t.IntervalMinutes * 60);
        if (seconds < 120) { UnitCombo.SelectedIndex = 0; MinutesBox.Value = (decimal)seconds; }
        else if (seconds % 3600 == 0) { UnitCombo.SelectedIndex = 2; MinutesBox.Value = (decimal)(seconds / 3600); }
        else { UnitCombo.SelectedIndex = 1; MinutesBox.Value = (decimal)(seconds / 60); }
        ScheduleCombo.SelectedIndex = string.IsNullOrWhiteSpace(t.Cron) ? 0 : 1;
        CronBox.Text = t.Cron;
        ActionCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Actions, t.Action));
        CommandBox.Text = t.Command;
        AiCommandBox.Text = t.AiCommand;
        PromptBox.Text = t.Prompt;
        MessageBox.Text = t.Message;
        NotifyCheck.IsChecked = t.Notify;
        EnabledCheck.IsChecked = t.Enabled;
        OutputBox.Text = timer is null ? "" : TimerService.GetLastOutput(timer.Id);
        StatusText.Text = timer is null ? "New timer — fill it in and press Save." : "";
        _suppress = false;
        ApplyActionVisibility();
        ApplyScheduleVisibility();
    }

    private void Action_Changed(object? sender, SelectionChangedEventArgs e) => ApplyActionVisibility();

    private void Schedule_Changed(object? sender, SelectionChangedEventArgs e) => ApplyScheduleVisibility();

    private void Cron_Changed(object? sender, TextChangedEventArgs e) => UpdateCronHint();

    private void ApplyScheduleVisibility()
    {
        // SelectionChanged fires while the XAML is still loading, before these panels exist
        if (IntervalPanel is null || CronPanel is null) return;
        var cron = ScheduleCombo.SelectedIndex == 1;
        IntervalPanel.IsVisible = !cron;
        CronPanel.IsVisible = cron;
        UpdateCronHint();
    }

    /// <summary>Shows the next run times of the typed expression, or what is wrong with it.</summary>
    private void UpdateCronHint()
    {
        if (CronHint is null || CronBox is null) return;
        var text = (CronBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            CronHint.Text = "minute hour day-of-month month day-of-week — e.g. “0 9 * * 1-5” = weekdays at 09:00, “*/15 * * * *” = every 15 minutes. Local time.";
            return;
        }
        if (CronSchedule.TryParse(text, out var error) is not { } cron)
        {
            CronHint.Text = error;
            return;
        }
        var next = cron.Upcoming(DateTime.Now, 3);
        CronHint.Text = next.Count == 0
            ? "This expression never matches a date."
            : "Next: " + string.Join(" · ", next.Select(d => d.ToString("ddd d MMM HH:mm")));
    }

    private void ApplyActionVisibility()
    {
        // SelectionChanged fires while the XAML is still loading, before these panels exist
        if (CommandPanel is null || AiPanel is null) return;
        var action = Actions[Math.Clamp(ActionCombo.SelectedIndex, 0, Actions.Length - 1)];
        CommandPanel.IsVisible = action == "command";
        AiPanel.IsVisible = action == "ai";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.J) { _dock.Toggle(); e.Handled = true; }
            else if (e.Key == Key.S) { SaveCurrent(); e.Handled = true; }
            else if (e.Key == Key.N) { New_Click(null, new RoutedEventArgs()); e.Handled = true; }
        }
        base.OnKeyDown(e);
    }

    private void Template_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || TemplateCombo is null || TemplateCombo.SelectedIndex <= 0) return;

        var t = TemplateCombo.SelectedIndex switch
        {
            1 => new IntervalTimerItem
            {
                Name = "Eye break", IntervalMinutes = 20, Action = "beep",
                Message = "Look at something 6 m (20 ft) away for 20 seconds.",
            },
            2 => new IntervalTimerItem
            {
                Name = "Stretch", IntervalMinutes = 45, Action = "beep",
                Message = "Stand up, roll your shoulders and check your posture.",
            },
            3 => new IntervalTimerItem
            {
                Name = "AI check-in", IntervalMinutes = 60, Action = "ai",
                Prompt = "It is {time}. Here is what I've done on my computer today:\n{today_summary}\n\n" +
                         "In at most three sentences: what I've mostly been working on, whether I seem distracted, " +
                         "and the most valuable thing to do next.",
            },
            _ => new IntervalTimerItem { Name = "Scheduled command", IntervalMinutes = 15, Action = "command" },
        };
        _current = null;
        TimerList.SelectedItem = null;
        LoadEditor(t);
        StatusText.Text = "Template loaded — adjust it if you like and press Save (Ctrl+S).";
        _suppress = true;
        TemplateCombo.SelectedIndex = 0;
        _suppress = false;
        NameBox.Focus();
    }

    private void ApplyAiCommandHint() => AiCommandBox.Watermark = AiService.Command;

    /// <summary>Another PC changed the shared timers; the editor keeps what is typed in it.</summary>
    private void OnTimersReloaded()
    {
        _current = _current is null ? null : Config.Timers.FirstOrDefault(t => t.Id == _current.Id);
        RefreshList(_current?.Id, keepEditor: true);
        StatusText.Text = "Timers were updated from another PC.";
    }

    private bool _describing;

    /// <summary>Lets the AI turn a sentence into a timer; it is loaded into the editor unsaved, for review.</summary>
    private async void Describe_Click(object? sender, RoutedEventArgs e)
    {
        var description = (DescribeBox.Text ?? "").Trim();
        if (_describing) return;
        if (description.Length == 0)
        {
            StatusText.Text = "Describe the timer first, e.g. “remind me to drink water every 40 minutes”.";
            DescribeBox.Focus();
            return;
        }
        _describing = true;
        DescribeButton.IsEnabled = false;
        StatusText.Text = AiService.Asking("set up the timer");
        try
        {
            var result = await AiService.AskAsync(AiPrompts.CreateTimer(description));
            if (!result.Ok)
            {
                StatusText.Text = AiService.Failed(result);
                return;
            }
            if (AiService.ExtractJson(result.Text) is not { } json)
            {
                StatusText.Text = "The AI's answer wasn't a timer — try describing it differently.";
                return;
            }
            var action = AiService.Str(json, "action");
            var timer = new IntervalTimerItem
            {
                Name = AiService.Str(json, "name"),
                IntervalMinutes = json.TryGetProperty("intervalMinutes", out var m) && m.TryGetDouble(out var minutes)
                    ? Math.Max(5 / 60.0, minutes) : 30,
                Action = Actions.Contains(action) ? action : "beep",
                Command = AiService.Str(json, "command"),
                Prompt = AiService.Str(json, "prompt"),
                Message = AiService.Str(json, "message"),
                Notify = !json.TryGetProperty("notify", out var n) || n.ValueKind != System.Text.Json.JsonValueKind.False,
            };
            _current = null;
            TimerList.SelectedItem = null;
            LoadEditor(timer);
            StatusText.Text = timer.Action == "command"
                ? "✨ Filled in by AI — check the command carefully, then press Save (Ctrl+S)."
                : "✨ Filled in by AI — check it, then press Save (Ctrl+S).";
        }
        finally
        {
            _describing = false;
            DescribeButton.IsEnabled = true;
        }
    }

    private void New_Click(object? sender, RoutedEventArgs e)
    {
        _current = null;
        TimerList.SelectedItem = null;
        LoadEditor(null);
        NameBox.Focus();
    }

    private IntervalTimerItem ReadEditor(IntervalTimerItem target)
    {
        target.Name = (NameBox.Text ?? "").Trim();
        var unitSeconds = UnitCombo.SelectedIndex switch { 0 => 1.0, 2 => 3600.0, _ => 60.0 };
        target.IntervalMinutes = (double)(MinutesBox.Value ?? 30m) * unitSeconds / 60.0;
        target.Cron = ScheduleCombo.SelectedIndex == 1 ? (CronBox.Text ?? "").Trim() : "";
        target.Action = Actions[Math.Clamp(ActionCombo.SelectedIndex, 0, Actions.Length - 1)];
        target.Command = (CommandBox.Text ?? "").Trim();
        target.AiCommand = (AiCommandBox.Text ?? "").Trim();
        target.Prompt = PromptBox.Text ?? "";
        target.Message = (MessageBox.Text ?? "").Trim();
        target.Notify = NotifyCheck.IsChecked == true;
        target.Enabled = EnabledCheck.IsChecked == true;
        return target;
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => SaveCurrent();

    private IntervalTimerItem? SaveCurrent()
    {
        var draft = ReadEditor(new IntervalTimerItem());
        if (ScheduleCombo.SelectedIndex == 1)
        {
            if (CronSchedule.TryParse(draft.Cron, out var cronError) is not { } cron || cron.Next(DateTime.Now) is null)
            {
                StatusText.Text = cronError.Length > 0 ? cronError : "This cron expression never matches a date.";
                CronBox.Focus();
                return null;
            }
        }
        else if (draft.IntervalMinutes * 60 < 5)
        {
            StatusText.Text = "The shortest interval is 5 seconds.";
            MinutesBox.Focus();
            return null;
        }
        if (draft.Action == "command" && draft.Command.Length == 0)
        {
            StatusText.Text = "Enter the command to run, or choose a different action.";
            CommandBox.Focus();
            return null;
        }
        if (draft.Action == "ai" && draft.Prompt.Trim().Length == 0)
        {
            StatusText.Text = "Enter the prompt to send to the AI.";
            PromptBox.Focus();
            return null;
        }

        var timer = ReadEditor(_current ?? new IntervalTimerItem());
        if (timer.Name.Length == 0) timer.Name = "Timer " + (Config.Timers.Count + 1);
        if (_current is null) Config.Timers.Add(timer);
        _current = timer;

        try
        {
            AppRef.SaveConfig();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
            return timer;
        }
        TimerService.Reschedule(timer.Id);
        StatusText.Text = !timer.Enabled ? "Saved (disabled)."
            : timer.Cron.Length > 0 ? "Saved. Fires on the cron schedule."
            : $"Saved. Fires every {FormatInterval(timer.IntervalMinutes)}.";
        RefreshList(timer.Id, keepEditor: true);
        return timer;
    }

    private void RunNow_Click(object? sender, RoutedEventArgs e)
    {
        // run what is on screen, saving it first so it persists
        var timer = SaveCurrent();
        if (timer is null) return;
        StatusText.Text = "Running…";
        TimerService.RunNow(timer);
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is not null)
        {
            if (!await Dialogs.Confirm(this, "Delete timer", $"Delete the timer “{_current.Name}”?", "Delete"))
                return;
            Config.Timers.RemoveAll(t => t.Id == _current.Id);
            try { AppRef.SaveConfig(); } catch (Exception ex) { StatusText.Text = "Could not save: " + ex.Message; }
            TimerService.Reschedule(_current.Id);
        }
        _current = null;
        RefreshList(null);
    }
}
