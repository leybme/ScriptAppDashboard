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
/// The one window of the app: a strip of tabs on the left (apps, notes, captures, timers, work log, search, settings),
/// the tab in front in the middle, and the AI chat on the right. Closing it hides the app to the tray.
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

        _dock = new AiPanelDock(this, ChatPanel, startVisible: true);
        _dock.Changed += ApplyChatButton;
        ApplyChatButton();
        ApplyAskAiTip();
        AppRef.SettingsChanged += ApplyAskAiTip;
        AppRef.SettingsChanged += UpdateToday;
        _todayTimer.Tick += (_, _) => UpdateToday();
        _todayTimer.Start();
        Opened += (_, _) => UpdateToday();
        Activated += (_, _) => UpdateToday();
        ShowTab("apps");
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
        button.Click += (_, _) => ShowTab(id);
        TabStrip.Children.Add(button);
        _tabs.Add(new TabInfo { Id = id, Title = title, Create = create, Button = button });
    }

    /// <summary>Brings a tab to the front (creating it the first time) and points the AI chat at it.</summary>
    public ShellTab ShowTab(string id)
    {
        var tab = _tabs.First(t => t.Id == id);
        if (tab.View is null)
        {
            tab.View = tab.Create();
            tab.View.Shell = this;
            tab.View.IsVisible = false;
            TabHost.Children.Add(tab.View);
        }
        if (_current is not null && !ReferenceEquals(_current, tab))
        {
            _current.View!.OnHidden();
            _current.View.IsVisible = false;
        }
        _current = tab;
        foreach (var t in _tabs) t.Button.IsChecked = ReferenceEquals(t, tab);
        tab.View.IsVisible = true;
        ChatPanel.Use(tab.View.ChatProfile ?? new AiChatProfile());
        tab.View.OnShown();
        return tab.View;
    }

    /// <summary>The tab with this id, created if it was never opened (without bringing it to the front).</summary>
    public T Tab<T>(string id) where T : ShellTab
    {
        var tab = _tabs.First(t => t.Id == id);
        if (tab.View is null)
        {
            var front = _current;
            ShowTab(id);
            if (front is not null) ShowTab(front.Id);
        }
        return (T)tab.View!;
    }

    /// <summary>The tab with this id if it was ever opened, else null.</summary>
    public T? ExistingTab<T>(string id) where T : ShellTab => _tabs.First(t => t.Id == id).View as T;

    /// <summary>The window was shown again: the tab in front refreshes.</summary>
    public void Revealed() => _current?.View?.OnShown();

    /// <summary>Saves what the tabs hold unsaved (a note being edited); used before the window hides and when the app quits.</summary>
    public void FlushTabs()
    {
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
