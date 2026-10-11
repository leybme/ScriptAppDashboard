using System;
using System.Collections.Generic;

namespace ScriptAppDashboard.Models;

public class ScriptItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string IconPath { get; set; } = "";

    /// <summary>Name of a MaterialIconKind enum value; used when IconPath is empty.</summary>
    public string IconKind { get; set; } = "";

    /// <summary>auto | python | pwsh | bash | node | cmd | direct | custom</summary>
    public string Interpreter { get; set; } = "auto";
    public string CustomCommand { get; set; } = "";
    public bool KeepConsoleOpen { get; set; }
}

public class AppConfig
{
    public List<ScriptItem> Scripts { get; set; } = new();
    public List<IntervalTimerItem> Timers { get; set; } = new();

    /// <summary>Show the "Copy time" button (main window) and "Time" button (notes).</summary>
    public bool TimestampButtonEnabled { get; set; } = true;

    /// <summary>.NET date format ("yyyy-MM-dd HH:mm") or a template with {…} parts ("[{HH:mm}] ").</summary>
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Format of the time on "◷" time-bullet lines in notes (same syntax as TimestampFormat).</summary>
    public string TimeBulletFormat { get; set; } = "yyyy-MM-dd HH:mm";

    /// <summary>Record foreground app / window titles / open windows to the daily work log.</summary>
    public bool WorkLogEnabled { get; set; } = true;
    public int WorkLogSampleSeconds { get; set; } = 30;

    /// <summary>No keyboard/mouse input for this long counts as idle (not logged as work time).</summary>
    public int WorkLogIdleMinutes { get; set; } = 5;

    /// <summary>Working hours, "HH:mm". The work log can count only this time; an end before the start wraps past midnight.</summary>
    public string WorkHoursStart { get; set; } = "09:00";
    public string WorkHoursEnd { get; set; } = "18:00";

    /// <summary>Working days as DayOfWeek numbers (0 = Sunday … 6 = Saturday).</summary>
    public List<int> WorkDays { get; set; } = new() { 1, 2, 3, 4, 5 };

    /// <summary>All interval timers are paused until this time; DateTime.MaxValue = until resumed, null = running.</summary>
    public DateTime? TimersPausedUntil { get; set; }

    /// <summary>claude | kiro | custom. Kiro is listed but not supported yet.</summary>
    public string AiProvider { get; set; } = "claude";

    /// <summary>Model passed to the Claude CLI (--model), e.g. "claude-haiku-5-5".</summary>
    public string AiModel { get; set; } = "claude-haiku-5-5";

    /// <summary>With the "custom" provider: a CLI that reads a prompt on stdin and prints the answer as text.</summary>
    public string AiCommand { get; set; } = "";

    /// <summary>Language AI answers are written in; empty = the language of the content.</summary>
    public string AiLanguage { get; set; } = "";

    /// <summary>Global hotkey that opens "Ask AI" (Windows), e.g. "Ctrl+Alt+Space"; empty = none.</summary>
    public string AiHotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Ask the AI for a title and tags when a note is saved without a title.</summary>
    public bool AiAutoTitle { get; set; } = true;

    /// <summary>Write an AI summary of the day into a note when working hours end.</summary>
    public bool AiDaySummary { get; set; } = true;

    /// <summary>End of the last working day that got a summary (so it is written once).</summary>
    public DateTime? AiDaySummaryLast { get; set; }
}

public class IntervalTimerItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public double IntervalMinutes { get; set; } = 30;
    public bool Enabled { get; set; } = true;

    /// <summary>beep | command | ai</summary>
    public string Action { get; set; } = "beep";

    /// <summary>Shell command line, used when Action is "command".</summary>
    public string Command { get; set; } = "";

    /// <summary>Overrides the app's AI command for this timer (e.g. another model); empty = the one in Settings.</summary>
    public string AiCommand { get; set; } = "";

    /// <summary>Sent to the AI when Action is "ai"; {placeholders} are filled in first (see AiService.Placeholders).</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Show a pop-up in the screen corner when the timer fires.</summary>
    public bool Notify { get; set; } = true;

    /// <summary>Optional text for the pop-up, e.g. "Look 6 m away for 20 seconds".</summary>
    public string Message { get; set; } = "";
}

public class NoteItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Html { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}
