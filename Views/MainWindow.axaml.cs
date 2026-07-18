using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

public partial class MainWindow : Window
{
    private static readonly Color[] TileColors =
    {
        Color.Parse("#3A6EFF"), Color.Parse("#7A4FD8"), Color.Parse("#0E9F6E"),
        Color.Parse("#D8543F"), Color.Parse("#C7842B"), Color.Parse("#12867F"),
        Color.Parse("#B84C86"),
    };

    private App AppRef => (App)Application.Current!;
    private AppConfig Config => AppRef.Config;
    private bool _suppressStartupEvent;

    public MainWindow()
    {
        InitializeComponent();
        RefreshTiles();
        StartupCheck.IsCheckedChanged += StartupCheck_Changed;
        Loaded += MainWindow_Loaded;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = await Task.Run(StartupService.IsEnabled);
            _suppressStartupEvent = true;
            StartupCheck.IsChecked = enabled;
            _suppressStartupEvent = false;
        }
        catch
        {
            // leave the checkbox unchecked if the state can't be read
        }
    }

    private async void StartupCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressStartupEvent) return;
        var enable = StartupCheck.IsChecked == true;
        try
        {
            await Task.Run(() => StartupService.SetEnabled(enable));
            SetStatus(enable
                ? "The dashboard will start with the system (hidden in the tray)."
                : "Auto-start disabled.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not change the auto-start setting: " + ex.Message);
        }
    }

    private void RefreshTiles()
    {
        TileHost.Children.Clear();
        EmptyHint.IsVisible = Config.Scripts.Count == 0;
        foreach (var item in Config.Scripts)
            TileHost.Children.Add(BuildTile(item));
    }

    private Control BuildTile(ScriptItem item)
    {
        Control iconControl;
        var bmp = UiHelpers.TryLoadBitmap(item.IconPath);
        if (bmp is not null)
        {
            iconControl = new Image { Source = bmp, Width = 56, Height = 56, Stretch = Stretch.Uniform };
        }
        else if (item.IconKind.Length > 0 && Enum.TryParse<MaterialIconKind>(item.IconKind, out var kind))
        {
            iconControl = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(ColorForName(item.Name)),
                Child = new MaterialIcon
                {
                    Kind = kind,
                    Width = 34,
                    Height = 34,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        else
        {
            iconControl = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(ColorForName(item.Name)),
                Child = new TextBlock
                {
                    Text = GetInitials(item.Name),
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        var label = new TextBlock
        {
            Text = item.Name,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            MaxWidth = 108,
        };

        var tile = new Button
        {
            Width = 124,
            Height = 118,
            Margin = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { iconControl, label },
            },
        };
        ToolTip.SetTip(tile, $"{item.ScriptPath} {item.Arguments}".Trim());
        tile.Click += (_, _) => RunItem(item);

        var menu = new ContextMenu();
        var run = new MenuItem { Header = "Run" };
        run.Click += (_, _) => RunItem(item);
        var edit = new MenuItem { Header = "Edit…" };
        edit.Click += async (_, _) => await EditItem(item);
        var openFolder = new MenuItem { Header = "Open Containing Folder" };
        openFolder.Click += (_, _) => OpenFolder(item);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += async (_, _) => await RemoveItem(item);
        menu.Items.Add(run);
        menu.Items.Add(edit);
        menu.Items.Add(openFolder);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        tile.ContextMenu = menu;

        return tile;
    }

    private async void RunItem(ScriptItem item)
    {
        try
        {
            LauncherService.Launch(item);
            SetStatus($"Launched “{item.Name}”.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to launch “{item.Name}”.");
            await Dialogs.Message(this, "Launch failed",
                $"Could not launch “{item.Name}”:\n\n{ex.Message}\n\n" +
                "Check the script path, interpreter and parameters (right-click the tile → Edit).");
        }
    }

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new EditScriptWindow(null);
        var result = await dialog.ShowDialog<ScriptItem?>(this);
        if (result is not null)
        {
            Config.Scripts.Add(result);
            SaveAndRefresh($"Added “{result.Name}”.");
        }
    }

    private async Task EditItem(ScriptItem item)
    {
        var dialog = new EditScriptWindow(item);
        var result = await dialog.ShowDialog<ScriptItem?>(this);
        if (result is not null)
        {
            var index = Config.Scripts.FindIndex(s => s.Id == item.Id);
            if (index >= 0) Config.Scripts[index] = result;
            SaveAndRefresh($"Saved “{result.Name}”.");
        }
    }

    private async Task RemoveItem(ScriptItem item)
    {
        var confirmed = await Dialogs.Confirm(this, "Remove script",
            $"Remove “{item.Name}” from the dashboard?\n(The script file itself is not deleted.)");
        if (confirmed)
        {
            Config.Scripts.RemoveAll(s => s.Id == item.Id);
            SaveAndRefresh($"Removed “{item.Name}”.");
        }
    }

    private async void ScanButton_Click(object? sender, RoutedEventArgs e)
    {
        var existing = new HashSet<string>(Config.Scripts.Select(s => s.ScriptPath), StringComparer.OrdinalIgnoreCase);
        var scan = new ScanWindow(existing, added =>
        {
            Config.Scripts.Add(added);
            SaveAndRefresh($"Added “{added.Name}” from running processes.");
        });
        await scan.ShowDialog(this);
    }

    private void HideButton_Click(object? sender, RoutedEventArgs e) => Hide();

    private void OpenFolder(ScriptItem item)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(item.ScriptPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.ScriptPath}\"",
                    UseShellExecute = false,
                });
            }
            else if (OperatingSystem.IsMacOS() && File.Exists(item.ScriptPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "-R", item.ScriptPath },
                    UseShellExecute = false,
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(item.ScriptPath);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not open folder: " + ex.Message);
        }
    }

    private void SaveAndRefresh(string status)
    {
        try
        {
            AppRef.SaveConfig();
        }
        catch (Exception ex)
        {
            SetStatus("Could not save the configuration: " + ex.Message);
            return;
        }
        RefreshTiles();
        SetStatus(status);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string GetInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
            return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
    }

    private static Color ColorForName(string name)
    {
        var hash = 0;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return TileColors[hash % TileColors.Length];
    }
}
