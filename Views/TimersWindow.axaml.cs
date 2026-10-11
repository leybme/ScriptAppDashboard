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
        ApplyAiCommandHint();
        AppRef.SettingsChanged += ApplyAiCommandHint;
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
            _countdown.Stop();
        };
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
        var text = $"every {FormatInterval(timer.IntervalMinutes)} · {Describe(timer.Action)}";
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
    }

    private void Action_Changed(object? sender, SelectionChangedEventArgs e) => ApplyActionVisibility();

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
            if (e.Key == Key.S) { SaveCurrent(); e.Handled = true; }
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
        StatusText.Text = $"✨ Asking {AiService.DisplayName} to set up the timer…";
        try
        {
            var result = await AiService.AskAsync(AiPrompts.CreateTimer(description));
            if (!result.Ok)
            {
                StatusText.Text = "AI failed: " + result.Text;
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
        if (draft.IntervalMinutes * 60 < 5)
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
        StatusText.Text = timer.Enabled ? $"Saved. Fires every {FormatInterval(timer.IntervalMinutes)}." : "Saved (disabled).";
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
