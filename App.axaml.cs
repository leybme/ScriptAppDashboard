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

    public void ExitApp()
    {
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
