using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        StartupService.MigrateLegacy();
        ApplyTheme();
        AiService.Start(Config);
        WorkLogService.Start(Config);
        TimerService.Start(Config);
        TimerService.Ran += (timer, output) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => NotifyTimer(timer, output));
        SettingsChanged += RefreshTrayMenu;
        SettingsChanged += ApplyTheme;
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
            ApplyHotkey();
            SettingsChanged += ApplyHotkey;
            _daySummaryTimer.Tick += (_, _) => CheckDaySummary();
            _daySummaryTimer.Start();
            _imageIndexTimer.Tick += async (_, _) => await ImageIndexService.TickAsync();
            _imageIndexTimer.Start();
            StartDataFolder();

            var startMinimized = desktop.Args?.Contains("--minimized") == true;
            if (!startMinimized)
                ShowMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the theme from Settings: follow the system, or dark / light.</summary>
    public void ApplyTheme()
        => RequestedThemeVariant = Config.Theme switch
        {
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            "light" => Avalonia.Styling.ThemeVariant.Light,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };

    public void SaveConfig()
    {
        ConfigService.Save(Config);
        RefreshTrayMenu();
    }

    /// <summary>Shows the main window (on the given tab, or as it was) and brings it to the front.</summary>
    public void ShowMainWindow(string? tab = null)
    {
        if (_mainWindow is null)
        {
            _mainWindow = new Views.MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        var wasHidden = !_mainWindow.IsVisible;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        if (tab is not null) _mainWindow.ShowTab(tab);
        else if (wasHidden) _mainWindow.Revealed();
    }

    public void ShowNotes(bool newNote = false, bool openChat = false)
    {
        ShowMainWindow("notes");
        if (newNote) _mainWindow!.Tab<Views.NotesView>("notes").NewNote();
        if (openChat) _mainWindow!.ShowChat(true);
    }

    /// <summary>Opens the AI chat beside the tab in front (the "Ask AI" hotkey and tray entry).</summary>
    public void ShowAskAi()
    {
        ShowMainWindow();
        _mainWindow!.ShowChat(true);
    }

    public void ShowTimers() => ShowMainWindow("timers");
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

    public void ShowSearch() => ShowMainWindow("search");

    /// <summary>Opens the notes tab on a given note.</summary>
    public void ShowNote(Guid id)
    {
        ShowMainWindow("notes");
        _mainWindow!.Tab<Views.NotesView>("notes").SelectNote(id);
    }

    public void ShowSettings() => ShowMainWindow("settings");

    public void ShowWorkLog() => ShowMainWindow("worklog");
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

    // ---- data folder (OneDrive sync) ---------------------------------------------------

    /// <summary>Raised on the UI thread after another PC changed the shared timers.</summary>
    public event Action? TimersReloaded;

    private FileSystemWatcher? _timersWatcher;
    private readonly Avalonia.Threading.DispatcherTimer _timersReload = new() { Interval = TimeSpan.FromSeconds(1) };

    private void StartDataFolder()
    {
        if (ConfigService.DataDirError is { } error)
            Views.ToastWindow.ShowToast("Data folder unavailable", error, ShowSettings, 30);
        NotesService.Watch();
        // pictures removed from notes are deleted after a week (a newer one may be in a note not saved or synced yet)
        _ = Task.Run(() =>
        {
            try { NotesService.DeleteUnusedImages(NotesService.Load(), TimeSpan.FromDays(7)); }
            catch { /* next start */ }
        });

        if (!Config.SyncTimers || !ConfigService.IsSynced) return;
        _timersReload.Tick += (_, _) =>
        {
            _timersReload.Stop();
            ReloadSharedTimers();
        };
        try
        {
            _timersWatcher = new FileSystemWatcher(ConfigService.DataDir, "timers.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _timersReload.Stop();
                _timersReload.Start();
            });
            _timersWatcher.Changed += changed;
            _timersWatcher.Created += changed;
            _timersWatcher.Renamed += (s, e) => changed(s, e);
            _timersWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // shared timers are still read at the next start
        }
    }

    /// <summary>Takes over the timers another PC saved; only new or retimed timers restart their countdown.</summary>
    private void ReloadSharedTimers()
    {
        if (ConfigService.LoadTimersIfChanged() is not { } timers) return;
        var before = Config.Timers.ToDictionary(t => t.Id);
        Config.Timers = timers;
        foreach (var t in timers)
            if (!before.TryGetValue(t.Id, out var old) || old.IntervalMinutes != t.IntervalMinutes || old.Cron != t.Cron || old.Enabled != t.Enabled)
                TimerService.Reschedule(t.Id);
        try { SaveConfig(); } catch { RefreshTrayMenu(); }
        TimersReloaded?.Invoke();
    }

    /// <summary>Starts a new copy of the app and quits this one (after the data folder changed).</summary>
    public void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        }
        catch
        {
            return; // keep running rather than leave the user with no app
        }
        ExitApp();
    }

    // ---- captures ---------------------------------------------------------------------

    private readonly Dictionary<Guid, Views.CaptureEditorWindow> _captureEditors = new();
    private bool _capturing;

    public void ShowCaptures() => ShowMainWindow("captures");

    /// <summary>Opens a saved capture in the editor (one editor per capture).</summary>
    public void ShowCapture(Guid id)
    {
        if (_captureEditors.TryGetValue(id, out var open))
        {
            open.Show();
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        if (CaptureService.Load(id) is not { } item) return;
        try
        {
            var editor = new Views.CaptureEditorWindow(item);
            _captureEditors[id] = editor;
            editor.Closed += (_, _) => _captureEditors.Remove(id);
            editor.Show();
            editor.Activate();
        }
        catch (Exception ex)
        {
            _captureEditors.Remove(id);
            Views.ToastWindow.ShowToast("Could not open the capture", ex.Message, ShowCaptures, 10);
        }
    }

    /// <summary>Saves pixels (a screenshot or a picture file) as a new capture and opens the editor on it.</summary>
    public void OpenCaptureFrom(Pixels pixels, OpenWindow? source)
    {
        try
        {
            var item = CaptureService.Create(pixels.ToPng(), pixels.Width, pixels.Height, source);
            ShowCapture(item.Id);
        }
        catch (Exception ex)
        {
            Views.ToastWindow.ShowToast("Could not save the capture", ex.Message, null, 10);
        }
    }

    /// <summary>Takes a screenshot: the user drags a region on a frozen picture of the desktop (or the whole desktop is
    /// taken), then the editor opens. On macOS the system's own region tool is used.</summary>
    public async Task CaptureAsync(bool fullScreen = false, OpenWindow? foreground = null)
    {
        if (_capturing || IsExiting) return;
        _capturing = true;
        // hide our own windows so they are not in the picture (toasts stay: they are short-lived)
        var hidden = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .Where(w => w.IsVisible && w is not Views.ToastWindow && w is not Views.CaptureOverlayWindow).ToList() ?? new();
        foreach (var w in hidden) w.Hide();
        try
        {
            if (hidden.Count > 0) await Task.Delay(250); // let the windows disappear before the screen is read
            Pixels? picked;
            if (OperatingSystem.IsMacOS())
                picked = await Task.Run(ScreenCaptureService.GrabInteractiveMac);
            else
            {
                var shot = await Task.Run(ScreenCaptureService.GrabScreen);
                if (shot is null)
                {
                    Views.ToastWindow.ShowToast("Could not read the screen",
                        OperatingSystem.IsWindows() ? "Screenshots do not work while the session is locked." : "Screenshots are not supported on this system.", null, 8);
                    return;
                }
                picked = fullScreen ? shot : await new Views.CaptureOverlayWindow(shot).PickAsync();
            }
            // bring the windows back first, so the editor opens in front of them
            foreach (var w in hidden) w.Show();
            hidden.Clear();
            if (picked is not null) OpenCaptureFrom(picked, foreground ?? WorkLogService.Foreground());
        }
        finally
        {
            foreach (var w in hidden) w.Show();
            _capturing = false;
        }
    }

    // ---- AI ---------------------------------------------------------------------------

    private Views.QuickAskWindow? _quickAskWindow;

    /// <summary>Why the "Ask AI" hotkey could not be registered, or null.</summary>
    public string? HotkeyError { get; private set; }

    private string? _registeredHotkey;
    private string? _registeredCaptureHotkey;

    /// <summary>Why the capture hotkey could not be registered, or null.</summary>
    public string? CaptureHotkeyError { get; private set; }

    private void ApplyHotkey()
    {
        var changed = false;
        if (_registeredHotkey != Config.AiHotkey)
        {
            _registeredHotkey = Config.AiHotkey;
            HotkeyError = HotkeyService.Start(0, Config.AiHotkey, () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowAskAi()));
            changed = true;
        }
        if (_registeredCaptureHotkey != Config.CaptureHotkey)
        {
            _registeredCaptureHotkey = Config.CaptureHotkey;
            CaptureHotkeyError = HotkeyService.Start(1, Config.CaptureHotkey, () =>
            {
                var foreground = WorkLogService.Foreground();
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => await CaptureAsync(foreground: foreground));
            }, "Settings → Capture");
            changed = true;
        }
        if (changed) RefreshTrayMenu(); // the entries show a hotkey only when it works
    }

    /// <param name="foreground">The window that was in front when the hotkey was pressed.</param>
    public void ShowQuickAsk(OpenWindow? foreground = null)
    {
        if (_quickAskWindow is null)
        {
            _quickAskWindow = new Views.QuickAskWindow();
            _quickAskWindow.Closed += (_, _) => _quickAskWindow = null;
        }
        _quickAskWindow.Show();
        if (_quickAskWindow.WindowState == WindowState.Minimized) _quickAskWindow.WindowState = WindowState.Normal;
        _quickAskWindow.Activate();
        _quickAskWindow.Summon(foreground);
    }

    /// <summary>Saves a note made outside the notes window (AI answers, day summaries) and shows it in an open notes window.</summary>
    public void AddNote(NoteItem note)
    {
        NotesService.Save(note);
        _mainWindow?.ExistingTab<Views.NotesView>("notes")?.AddNote(note);
        WorkLogService.LogNote(note);
    }

    /// <summary>Gives a note the AI's title and tags, unless the user titled it in the meantime.</summary>
    /// <param name="untitled">The title the note was saved with (taken from its first words).</param>
    public void ApplyAiTitle(Guid id, string untitled, string title, IReadOnlyList<string> tags)
    {
        if (_mainWindow?.ExistingTab<Views.NotesView>("notes") is { } notesTab)
        {
            notesTab.ApplyAiTitle(id, untitled, title, tags);
            return;
        }
        try
        {
            var note = NotesService.Load().FirstOrDefault(n => n.Id == id);
            if (note is null || note.Title != untitled) return;
            note.Title = title;
            note.Tags = MergeTags(note.Tags, tags);
            NotesService.Save(note);
        }
        catch
        {
            // the note keeps the title made from its first words
        }
    }

    /// <summary>"#work" + ["work", "ideas"] → "#work #ideas".</summary>
    public static string MergeTags(string existing, IEnumerable<string> tags)
    {
        var all = existing.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var tag in tags)
            if (!all.Any(t => t.TrimStart('#').Equals(tag, StringComparison.OrdinalIgnoreCase)))
                all.Add("#" + tag);
        return string.Join(" ", all);
    }

    private readonly Avalonia.Threading.DispatcherTimer _daySummaryTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _daySummaryRunning;

    /// <summary>Reads the text of new note pictures and captures while the user is idle (see <see cref="ImageIndexService"/>).</summary>
    private readonly Avalonia.Threading.DispatcherTimer _imageIndexTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    /// <summary>Once per working day, when working hours end, writes an AI summary of the day into a note.</summary>
    private void CheckDaySummary()
    {
        if (!Config.AiDaySummary || !Config.WorkLogEnabled || !WorkLogService.IsSupported || _daySummaryRunning) return;
        var hours = new WorkHours(Config);
        var now = DateTime.Now;
        var end = DateTime.Today + hours.End;
        if (end > now) end = end.AddDays(-1);
        // a night shift (end before start) belongs to the day it started
        var shiftDay = hours.End > hours.Start ? end.Date : end.Date.AddDays(-1);
        if (!hours.Days[(int)shiftDay.DayOfWeek] || Config.AiDaySummaryLast >= end) return;

        Config.AiDaySummaryLast = end;
        try { ConfigService.Save(Config); } catch { return; }
        if (now - end > TimeSpan.FromHours(12)) return; // the app wasn't running then; don't summarise an old day

        _daySummaryRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var summary = WorkLogService.Summarize(shiftDay, end.Date, hours);
                if (summary.ActiveSeconds < 15 * 60) return; // a day off, or the PC was barely used
                var result = await AiService.AskAsync(AiPrompts.WorkReport(WorkLogService.ToAiContext(summary, hours)));
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!result.Ok)
                    {
                        Views.ToastWindow.ShowToast("Day summary failed", result.Text, ShowSettings, 20);
                        return;
                    }
                    var note = AiService.AnswerToNote($"Day summary {shiftDay:ddd yyyy-MM-dd}", "#summary #ai", result.Text,
                        $"Active time {WorkLogService.FormatDuration(summary.ActiveSeconds)} ({summary.WorkHoursFilter})");
                    try
                    {
                        AddNote(note);
                        Views.ToastWindow.ShowToast("Day summary saved", note.Title + " — click to open it.", () => ShowNote(note.Id), 20);
                    }
                    catch (Exception ex)
                    {
                        Views.ToastWindow.ShowToast("Day summary failed", "Could not save the note: " + ex.Message, seconds: 20);
                    }
                });
            }
            catch
            {
                // reading the log failed; try again tomorrow
            }
            finally
            {
                _daySummaryRunning = false;
            }
        });
    }

    public void ExitApp()
    {
        _mainWindow?.FlushTabs();
        WorkLogService.Stop();
        TimerService.Stop();
        HotkeyService.Stop();
        IsExiting = true;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void SetupTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Toolshed360/Assets/icon.png"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "Toolshed360",
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
        var ask = new NativeMenuItem(string.IsNullOrWhiteSpace(Config.AiHotkey) || HotkeyError is not null
            ? "Ask AI…" : $"Ask AI…  ({Config.AiHotkey})");
        ask.Click += (_, _) => ShowAskAi();
        menu.Add(ask);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var capture = new NativeMenuItem(string.IsNullOrWhiteSpace(Config.CaptureHotkey) || CaptureHotkeyError is not null
                ? "Capture Screenshot" : $"Capture Screenshot  ({Config.CaptureHotkey})");
            // a moment for the tray menu to close before the screen is read
            capture.Click += async (_, _) =>
            {
                await Task.Delay(300);
                await CaptureAsync();
            };
            menu.Add(capture);
        }
        var captures = new NativeMenuItem("Captures…");
        captures.Click += (_, _) => ShowCaptures();
        menu.Add(captures);
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
        var quit = new NativeMenuItem("Quit Toolshed360");
        quit.Click += (_, _) => ExitApp();
        menu.Add(quit);

        _trayIcon.Menu = menu;
    }
}
