using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// The one window of the app: the AI chat on the left, a strip of tabs (apps, notes, captures, timers, work log, search,
/// settings) next to it, and the tab content on the right (collapsed until a tab is clicked). Closing it hides the app to the tray.
/// </summary>
public partial class MainWindow : Window
{
    private sealed class TabInfo
    {
        public string Id = "", Title = "";
        public Func<ShellTab> Create = null!;
        public ShellTab? View;
        public ToggleButton Button = null!;
    }

    private readonly List<TabInfo> _tabs = new();
    private TabInfo? _current;
    private readonly AiPanelDock _dock;
    private readonly DispatcherTimer _todayTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private App AppRef => (App)Application.Current!;
    private AppConfig Config => AppRef.Config;

    /// <summary>The AI chat shared by all tabs.</summary>
    public AiChatPanel Chat => ChatPanel;

    public MainWindow()
    {
        InitializeComponent();
        AddTab("apps", "Apps", MaterialIconKind.ViewGridOutline, "Your scripts and programs as tiles", () => new AppsView());
        AddTab("notes", "Notes", MaterialIconKind.NoteEditOutline, "Quick notes — rich text, searchable later", () => new NotesView());
        AddTab("captures", "Captures", MaterialIconKind.CameraOutline, "Screenshots with annotations — searchable by the words on them", () => new CapturesView());
        AddTab("timers", "Timers", MaterialIconKind.TimerOutline, "Interval and cron timers: beep, run a command or ask an AI CLI", () => new TimersView());
        AddTab("worklog", "Work Log", MaterialIconKind.ChartBar, "See where your time went (apps, tabs, scripts)", () => new WorkLogView());
        AddTab("search", "Search", MaterialIconKind.Magnify, "Search notes and the work log together (Ctrl+F)", () => new SearchView());
        AddTab("settings", "Settings", MaterialIconKind.CogOutline, "Settings", () => new SettingsView());

        _contentRoom = Math.Clamp(Config.ContentWidth, 400, 4000); // the width the content had when it was last closed
        ApplyColumns();
        _dock = new AiPanelDock(ChatPanel, startVisible: true,
            resize: (was, now) => ResizeFor(chatWas: was, chatNow: now, contentWas: _contentOpen, contentNow: _contentOpen));
        MaxWidth = WidthFor(_dock.IsVisible, false); // the window starts as chat + tab list and can't be widened until a tab is opened
        _dock.Changed += () => { ApplyColumns(); ApplyCompact(); ApplyChatButton(); };
        ApplyChatButton();
        Topmost = Config.PinToTop;
        ApplyPinButton();
        ApplyAskAiTip();
        AppRef.SettingsChanged += ApplyAskAiTip;
        AppRef.SettingsChanged += UpdateToday;
        _todayTimer.Tick += (_, _) => UpdateToday();
        _todayTimer.Start();
        Opened += (_, _) => UpdateToday();
        Activated += (_, _) => UpdateToday();
        ShowTab("apps", open: false); // the window starts compact: tab list and chat; a click on a tab opens its content
    }

    // ---- tabs -------------------------------------------------------------------------------

    private void AddTab(string id, string title, MaterialIconKind icon, string tip, Func<ShellTab> create)
    {
        var button = new ToggleButton
        {
            Width = 80,
            Padding = new Thickness(4, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new MaterialIcon { Kind = icon, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = title, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => TabClicked(id);
        TabStrip.Children.Add(button);
        _tabs.Add(new TabInfo { Id = id, Title = title, Create = create, Button = button });
    }

    /// <summary>A click on a tab button: opens the tab's content; a click on the tab that is open collapses the content again.</summary>
    private void TabClicked(string id)
    {
        if (_contentOpen && _current?.Id == id) ShowContent(false);
        else ShowTab(id);
    }

    /// <summary>Creates the view of a tab the first time it is needed.</summary>
    private ShellTab EnsureView(TabInfo tab)
    {
        if (tab.View is null)
        {
            tab.View = tab.Create();
            tab.View.Shell = this;
            tab.View.IsVisible = false;
            TabHost.Children.Add(tab.View);
        }
        return tab.View;
    }

    /// <summary>
    /// Makes a tab the one the chat is about and, unless <paramref name="open"/> is false, opens its content (the window widens
    /// when the content was collapsed).
    /// </summary>
    public ShellTab ShowTab(string id, bool open = true)
    {
        var tab = _tabs.First(t => t.Id == id);
        var view = EnsureView(tab);
        if (_current is not null && !ReferenceEquals(_current, tab))
        {
            _current.View!.OnHidden();
            _current.View.IsVisible = false;
        }
        _current = tab;
        view.IsVisible = true;
        ChatPanel.Use(view.ChatProfile ?? new AiChatProfile());
        var wasOpen = _contentOpen;
        if (open) ShowContent(true);
        if (open && wasOpen) view.OnShown(); // opening the content already refreshed the tab
        UpdateTabButtons();
        return view;
    }

    private bool _contentOpen;

    /// <summary>The width of the tab list: a 6 px margin, an 80 px button, a 6 px margin.</summary>
    private const double StripWidth = 92;

    /// <summary>The room the tab content needs next to the tab list and the chat.</summary>
    private double _contentRoom = 800;

    /// <summary>
    /// The room the content has now (the window's width minus the chat and the tab list) becomes the width the content opens
    /// with next time, and is saved, so a window dragged wider is not forgotten when the content is collapsed or the app restarts.
    /// </summary>
    private void RememberContentRoom()
    {
        if (!_contentOpen || WindowState != WindowState.Normal) return;
        var room = Width - WidthFor(_dock.IsVisible, false);
        if (room < 400 || Math.Abs(room - _contentRoom) < 2) return;
        _contentRoom = room;
        Config.ContentWidth = room;
        try { AppRef.SaveConfig(); } catch { /* remembered for this run */ }
    }

    public bool IsContentOpen => _contentOpen;

    /// <summary>The window width for a state: the tab list, plus the chat with its margins, plus the content.</summary>
    private double WidthFor(bool chat, bool content) => StripWidth + (chat ? _dock.Room : 0) + (content ? _contentRoom : 0);

    /// <summary>
    /// Sets the window width for a change of state. It is computed from the state, not added up: Windows keeps a window from
    /// getting narrower than about 120 px, so shrinking by exact amounts would leave a little more width every time. What
    /// the user added by dragging the window's edge is kept (there is none while only the tab list is showing).
    /// </summary>
    private void ResizeFor(bool chatWas, bool chatNow, bool contentWas, bool contentNow)
    {
        // With the content collapsed the window is exactly the chat and the tab list, and can't be made wider: the chat keeps its
        // width instead of stretching over whatever width the tab content had been given.
        MaxWidth = !contentNow && chatNow ? WidthFor(true, false) : double.PositiveInfinity;
        // a maximized window can't get that narrow: collapsing the content brings it back to its normal size first
        if (!contentNow && WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        if (WindowState != WindowState.Normal) return;
        // the width the user dragged on is kept only while the content is open
        var extra = contentNow && (chatWas || contentWas) ? Math.Max(0, Width - WidthFor(chatWas, contentWas)) : 0;
        Width = Math.Min(WidthFor(chatNow, contentNow) + extra, AiPanelDock.AvailableWidth(this) ?? double.MaxValue);
    }

    /// <summary>Opens or collapses the tab content; the window widens (as far as the screen allows) or narrows by its room.</summary>
    public void ShowContent(bool open)
    {
        if (_contentOpen == open) return;
        if (!open) RememberContentRoom(); // before the window shrinks: its width now is what the user chose
        ResizeFor(chatWas: _dock.IsVisible, chatNow: _dock.IsVisible, contentWas: _contentOpen, contentNow: open);
        _contentOpen = open;
        TabHost.IsVisible = open;
        ApplyColumns();
        ApplyCompact();
        if (open) _current?.View?.OnShown();
        else _current?.View?.OnHidden();
        UpdateTabButtons();
    }
    /// <summary>
    /// Chat on the left, tab list, content on the right. While the content is collapsed the chat takes the width (or, with the
    /// chat hidden too, nothing does and the tab list sits alone at the left); while it is open the chat has a fixed width and
    /// the content takes the rest.
    /// </summary>
    private void ApplyColumns()
    {
        Root.ColumnDefinitions[0].Width = !_contentOpen && ChatPanel.IsVisible ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Root.ColumnDefinitions[2].Width = _contentOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ChatPanel.Width = _contentOpen ? AiPanelDock.PanelWidth : double.NaN;
    }
    private bool _compact;
    private double _normalHeight;

    /// <summary>
    /// With the chat and the content both closed only the tab list is left. In 92 px a title bar doesn't fit (Windows drops
    /// buttons and keeps the window wider than the list), and the full height leaves a big empty gap. So the window becomes
    /// a small floating dock: no title bar (<see cref="Grip"/> moves it), exactly as big as its buttons. Opening a tab or the
    /// chat brings the normal window back at its old height.
    /// </summary>
    private void ApplyCompact()
    {
        var compact = !_contentOpen && !_dock.IsVisible;
        if (compact == _compact) return;
        _compact = compact;
        Grip.IsVisible = compact;
        if (compact)
        {
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _normalHeight = Height;
            MinHeight = 0;
            CanResize = false;
            SystemDecorations = SystemDecorations.BorderOnly;
        }
        else
        {
            SystemDecorations = SystemDecorations.Full;
            CanResize = true;
            MinHeight = 600; // the tab list and its bottom buttons need this much
        }
        // changing the frame makes Windows resize the window (seen as a user resize, which also turns SizeToContent off):
        // set the size once that is done
        Dispatcher.UIThread.Post(() =>
        {
            if (_compact != compact) return;
            if (compact)
            {
                Strip.Measure(new Size(StripWidth, double.PositiveInfinity));
                Width = StripWidth;
                Height = Math.Ceiling(Strip.DesiredSize.Height);
            }
            else
            {
                Height = Math.Max(MinHeight, _normalHeight);
                Width = Math.Min(WidthFor(_dock.IsVisible, _contentOpen), AiPanelDock.AvailableWidth(this) ?? double.MaxValue);
            }
        }, DispatcherPriority.Background);
    }

    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    /// <summary>The tab whose content is open looks pressed; with the content collapsed no tab does (the chat still follows the last one).</summary>
    private void UpdateTabButtons()
    {
        foreach (var t in _tabs) t.Button.IsChecked = _contentOpen && ReferenceEquals(t, _current);
    }

    /// <summary>The tab with this id, created if it was never opened (without bringing it to the front).</summary>
    public T Tab<T>(string id) where T : ShellTab => (T)EnsureView(_tabs.First(t => t.Id == id));
    /// <summary>The tab with this id if it was ever opened, else null.</summary>
    public T? ExistingTab<T>(string id) where T : ShellTab => _tabs.First(t => t.Id == id).View as T;

    /// <summary>The window was shown again: the tab in front refreshes.</summary>
    public void Revealed()
    {
        if (_contentOpen) _current?.View?.OnShown();
    }

    /// <summary>Saves what the tabs hold unsaved (a note being edited); used before the window hides and when the app quits.</summary>
    public void FlushTabs()
    {
        RememberContentRoom();
        foreach (var tab in _tabs) tab.View?.OnHidden();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        FlushTabs();
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---- the AI chat ------------------------------------------------------------------------

    public void ShowChat(bool show)
    {
        _dock.Show(show);
        if (show) ChatPanel.FocusInput();
    }

    public void ToggleChat() => ShowChat(!_dock.IsVisible);

    public bool IsChatVisible => _dock.IsVisible;

    private void ChatButton_Click(object? sender, RoutedEventArgs e) => ToggleChat();

    private void ApplyChatButton()
    {
        ChatButton.IsChecked = _dock.IsVisible;
        ChatButton.Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new MaterialIcon { Kind = MaterialIconKind.RobotOutline, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Ask AI", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
    }

    /// <summary>Keeps the window above all other windows, or lets it go behind them again; the choice is remembered.</summary>
    private void PinButton_Click(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        Config.PinToTop = Topmost;
        try { AppRef.SaveConfig(); } catch { /* the window stays pinned for this run */ }
        ApplyPinButton();
    }

    private void ApplyPinButton()
    {
        PinButton.IsChecked = Topmost;
        ToolTip.SetTip(PinButton, Topmost ? "Pinned: the window stays above other windows. Click to unpin." : "Keep this window above all other windows");
    }

    private void ApplyAskAiTip()
        => ToolTip.SetTip(ChatButton, "Chat with the AI beside this tab (Ctrl+J)" +
                                      (string.IsNullOrWhiteSpace(Config.AiHotkey) || AppRef.HotkeyError is not null
                                          ? "" : $" — from any app: {Config.AiHotkey}"));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.J)
        {
            ToggleChat();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F && _current?.Id != "notes")
        {
            ShowTab("search");
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ---- today's active time ----------------------------------------------------------------

    /// <summary>Shows "Today 2h 14m" under the tabs (reading the log off the UI thread).</summary>
    private async void UpdateToday()
    {
        if (!WorkLogService.IsSupported || !Config.WorkLogEnabled || !IsVisible)
        {
            TodayButton.IsVisible = WorkLogService.IsSupported && IsVisible && !Config.WorkLogEnabled;
            TodayButton.Content = "Work log off";
            return;
        }
        try
        {
            var seconds = await Task.Run(WorkLogService.TodayActiveSeconds);
            var minutes = (int)(seconds / 60);
            TodayButton.Content = "Today " + (minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m");
            TodayButton.IsVisible = true;
        }
        catch
        {
            TodayButton.IsVisible = false;
        }
    }

    private void TodayButton_Click(object? sender, RoutedEventArgs e) => ShowTab("worklog");

    private void HideButton_Click(object? sender, RoutedEventArgs e) => Hide();
}
