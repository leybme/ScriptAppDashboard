using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

public partial class ScanWindow : Window
{
    private readonly HashSet<string> _existingPaths;
    private readonly Action<ScriptItem> _onAdded;
    private bool _scanning;
    private List<ScannedScript> _found = new();
    private List<ScannedApp> _apps = new();

    public ScanWindow() : this(new HashSet<string>(StringComparer.OrdinalIgnoreCase), _ => { }) { }

    public ScanWindow(HashSet<string> existingPaths, Action<ScriptItem> onAdded)
    {
        InitializeComponent();
        _existingPaths = existingPaths;
        _onAdded = onAdded;
        FilterBox.TextChanged += (_, _) => { if (!_scanning) Render(); };
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync()
    {
        if (_scanning) return;
        _scanning = true;
        RowsHost.Children.Clear();
        ScanStatus.Text = "Scanning…";
        try
        {
            List<ScannedScript> found;
            List<ScannedApp> apps;
            try
            {
                found = await Task.Run(ProcessScanner.Scan);
                apps = await Task.Run(ProcessScanner.ScanApps);
            }
            catch (Exception ex)
            {
                ScanStatus.Text = "Scan failed: " + ex.Message;
                return;
            }

            _found = found;
            _apps = apps;
            Render();
        }
        finally
        {
            _scanning = false;
        }
    }

    private void Render()
    {
        RowsHost.Children.Clear();
        var filter = (FilterBox.Text ?? "").Trim();
        bool Match(params string[] fields) =>
            filter.Length == 0 || fields.Any(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var scripts = _found.Where(s => Match(s.ScriptPath, s.CommandLine, s.Interpreter)).ToList();
        var apps = _apps.Where(a => Match(a.Name, a.ExePath, a.WindowTitle)).ToList();

        if (scripts.Count > 0) RowsHost.Children.Add(BuildHeader($"Scripts ({scripts.Count})"));
        foreach (var script in scripts)
            RowsHost.Children.Add(BuildRow(script));

        if (apps.Count > 0) RowsHost.Children.Add(BuildHeader($"Open apps ({apps.Count})"));
        foreach (var app in apps)
            RowsHost.Children.Add(BuildAppRow(app));

        if (scripts.Count + apps.Count == 0)
        {
            RowsHost.Children.Add(new TextBlock
            {
                Text = _found.Count + _apps.Count == 0
                    ? "No running scripts or apps found."
                    : "Nothing matches the filter.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(2, 12),
            });
        }

        ScanStatus.Text = filter.Length == 0
            ? $"Found {_found.Count} running script(s) and {_apps.Count} open app(s)."
            : $"Showing {scripts.Count + apps.Count} of {_found.Count + _apps.Count}.";
    }

    private static Control BuildHeader(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(2, 6, 0, 0),
    };

    private Control BuildRow(ScannedScript scanned) => BuildRow(
        title: $"{Path.GetFileName(scanned.ScriptPath)}   ({scanned.Interpreter}, pid {scanned.Pid})",
        detail: scanned.CommandLine,
        path: scanned.ScriptPath,
        prefill: new ScriptItem
        {
            Name = Path.GetFileNameWithoutExtension(scanned.ScriptPath),
            ScriptPath = scanned.ScriptPath,
            Arguments = scanned.Arguments,
        });

    private Control BuildAppRow(ScannedApp app) => BuildRow(
        title: $"{app.Name}   (app, pid {app.Pid})",
        detail: $"{app.WindowTitle} — {app.ExePath}",
        path: app.ExePath,
        prefill: new ScriptItem
        {
            Name = app.Name,
            ScriptPath = app.ExePath,
            Interpreter = "direct",
        });

    private Control BuildRow(string title, string detail, string path, ScriptItem prefill)
    {
        var alreadyAdded = _existingPaths.Contains(path);

        var addButton = new Button
        {
            Content = alreadyAdded ? "Added" : "Add…",
            IsEnabled = !alreadyAdded,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 76,
        };

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(info);
        Grid.SetColumn(addButton, 1);
        grid.Children.Add(addButton);

        addButton.Click += async (_, _) =>
        {
            var dialog = new EditScriptWindow(prefill);
            var result = await dialog.ShowDialog<ScriptItem?>(this);
            if (result is not null)
            {
                _onAdded(result);
                _existingPaths.Add(result.ScriptPath);
                addButton.Content = "Added";
                addButton.IsEnabled = false;
            }
        };

        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#14888888")),
            Child = grid,
        };
    }
}
