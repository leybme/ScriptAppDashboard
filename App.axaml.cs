using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard;

public partial class App : Application
{
    public AppConfig Config { get; private set; } = new();
    public static bool IsExiting { get; private set; }

    private TrayIcon? _trayIcon;
    private Views.MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Config = ConfigService.Load();
        WorkLogService.Start(Config);
        TimerService.Start(Config);
        TimerService.Ran += (timer, output) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => NotifyTimer(timer, output));
        SettingsChanged += RefreshTrayMenu;
        TimerService.PauseChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // persist the pause so it survives a restart; SaveConfig also rebuilds the tray menu
            try { SaveConfig(); } catch { RefreshTrayMenu(); }
            TimersPauseChanged?.Invoke();
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // the app lives in the tray; closing/hiding windows must not end it
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SetupTrayIcon();

            var startMinimized = desktop.Args?.Contains("--minimized") == true;
            if (!startMinimized)
                ShowMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SaveConfig()
    {
        ConfigService.Save(Config);
        RefreshTrayMenu();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new Views.MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private Views.NotesWindow? _notesWindow;
    private Views.TimersWindow? _timersWindow;
    private Views.WorkLogWindow? _workLogWindow;

    public void ShowNotes(bool newNote = false)
    {
        if (_notesWindow is null)
        {
            _notesWindow = new Views.NotesWindow();
            _notesWindow.Closed += (_, _) => _notesWindow = null;
        }
        _notesWindow.Show();
        if (_notesWindow.WindowState == WindowState.Minimized) _notesWindow.WindowState = WindowState.Normal;
        _notesWindow.Activate();
        if (newNote) _notesWindow.NewNote();
    }

    public void ShowTimers()
    {
        if (_timersWindow is null)
        {
            _timersWindow = new Views.TimersWindow();
            _timersWindow.Closed += (_, _) => _timersWindow = null;
        }
        _timersWindow.Show();
        if (_timersWindow.WindowState == WindowState.Minimized) _timersWindow.WindowState = WindowState.Normal;
        _timersWindow.Activate();
    }

    private Views.SettingsWindow? _settingsWindow;

    /// <summary>Raised after the settings window saved, so open windows can refresh.</summary>
    public event Action? SettingsChanged;

    public void NotifySettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>Raised on the UI thread after the interval timers were paused or resumed.</summary>
    public event Action? TimersPauseChanged;

    /// <summary>"Paused until 14:30" / "Paused until you resume" / "Paused until Mon 09:00".</summary>
    public static string PauseText(DateTime until)
        => until == DateTime.MaxValue ? "Paused until you resume"
            : until.Date == DateTime.Today ? $"Paused until {until:HH:mm}"
            : $"Paused until {until:ddd HH:mm}";

    private Views.SearchWindow? _searchWindow;

    public void ShowSearch()
    {
        if (_searchWindow is null)
        {
            _searchWindow = new Views.SearchWindow();
            _searchWindow.Closed += (_, _) => _searchWindow = null;
        }
        _searchWindow.Show();
        if (_searchWindow.WindowState == WindowState.Minimized) _searchWindow.WindowState = WindowState.Normal;
        _searchWindow.Activate();
        _searchWindow.FocusSearch();
    }

    /// <summary>Opens the notes window on a given note.</summary>
    public void ShowNote(Guid id)
    {
        ShowNotes();
        _notesWindow?.SelectNote(id);
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new Views.SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void ShowWorkLog()
    {
        if (_workLogWindow is null)
        {
            _workLogWindow = new Views.WorkLogWindow();
            _workLogWindow.Closed += (_, _) => _workLogWindow = null;
        }
        _workLogWindow.Show();
        if (_workLogWindow.WindowState == WindowState.Minimized) _workLogWindow.WindowState = WindowState.Normal;
        _workLogWindow.Activate();
    }

    /// <summary>Pop-up for a timer that just fired: its message, plus the output of a command / AI prompt.</summary>
    private void NotifyTimer(IntervalTimerItem timer, string output)
    {
        if (!timer.Notify || IsExiting) return;
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(timer.Message)) parts.Add(timer.Message.Trim());
        if (timer.Action != "beep" && !string.IsNullOrWhiteSpace(output)) parts.Add(output.Trim());
        if (parts.Count == 0) parts.Add(timer.Action == "beep" ? "Time's up." : "Done.");
        var title = string.IsNullOrWhiteSpace(timer.Name) ? "Timer" : timer.Name;
        // AI answers take longer to read than a reminder
        var seconds = timer.Action == "ai" ? 30 : 12;
        Views.ToastWindow.ShowToast("⏱ " + title, string.Join("\n\n", parts), ShowTimers, seconds);
    }

    /// <summary>Tray "Copy time": copies the current time and confirms with a short pop-up (whose clipboard is used,
    /// so it works even when no window was ever opened).</summary>
    private async void CopyTimeFromTray()
    {
        var text = TimestampService.Now(Config.TimestampFormat);
        var toast = Views.ToastWindow.ShowToast("Copied to clipboard", text, seconds: 3);
        try
        {
            if (toast.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch
        {
            // clipboard busy (another app holds it) — nothing useful to do from the tray
        }
    }

    public void ExitApp()
    {
        WorkLogService.Stop();
        TimerService.Stop();
        IsExiting = true;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void SetupTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://ScriptAppDashboard/Assets/icon.png"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "Script App Dashboard",
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        RefreshTrayMenu();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    /// <summary>Rebuilds the tray menu so scripts can be launched without opening the window.</summary>
    public void RefreshTrayMenu()
    {
        if (_trayIcon is null) return;

        var menu = new NativeMenu();

        var open = new NativeMenuItem("Open Dashboard");
        open.Click += (_, _) => ShowMainWindow();
        menu.Add(open);

        var newNote = new NativeMenuItem("New Note…");
        newNote.Click += (_, _) => ShowNotes(newNote: true);
        menu.Add(newNote);
        if (Config.TimestampButtonEnabled)
        {
            var copyTime = new NativeMenuItem("Copy Time");
            copyTime.Click += (_, _) => CopyTimeFromTray();
            menu.Add(copyTime);
        }
        var timers = new NativeMenuItem("Interval Timers…");
        timers.Click += (_, _) => ShowTimers();
        menu.Add(timers);
        if (Config.Timers.Any(t => t.Enabled))
        {
            if (TimerService.IsPaused)
            {
                var resume = new NativeMenuItem($"Resume Timers ({PauseText(TimerService.PausedUntil!.Value).ToLowerInvariant()})");
                resume.Click += (_, _) => TimerService.Resume();
                menu.Add(resume);
            }
            else
            {
                var pauseMenu = new NativeMenu();
                // durations are computed when clicked: the menu can sit unchanged for hours
                foreach (var (label, duration) in new (string, Func<TimeSpan?>)[]
                {
                    ("For 30 minutes", () => TimeSpan.FromMinutes(30)),
                    ("For 1 hour", () => TimeSpan.FromHours(1)),
                    ("For 2 hours", () => TimeSpan.FromHours(2)),
                    ("Until tomorrow morning", () => DateTime.Today.AddDays(1).AddHours(6) - DateTime.Now),
                    ("Until I resume", () => null),
                })
                {
                    var item = new NativeMenuItem(label);
                    item.Click += (_, _) => TimerService.Pause(duration());
                    pauseMenu.Add(item);
                }
                menu.Add(new NativeMenuItem("Pause Timers") { Menu = pauseMenu });
            }
        }
        var search = new NativeMenuItem("Search Notes and Work Log…");
        search.Click += (_, _) => ShowSearch();
        menu.Add(search);
        var workLog = new NativeMenuItem("Work Log…");
        workLog.Click += (_, _) => ShowWorkLog();
        menu.Add(workLog);
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();
        menu.Add(settings);

        if (Config.Scripts.Count > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
            foreach (var script in Config.Scripts.Take(15))
            {
                var captured = script;
                var entry = new NativeMenuItem(script.Name);
                entry.Click += (_, _) =>
                {
                    try { LauncherService.Launch(captured); }
                    catch { /* errors are surfaced when launching from the window */ }
                };
                menu.Add(entry);
            }
        }

        menu.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit Script App Dashboard");
        quit.Click += (_, _) => ExitApp();
        menu.Add(quit);

        _trayIcon.Menu = menu;
    }
}
