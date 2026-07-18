using System;
using System.Collections.Generic;
using System.IO;
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

    public ScanWindow() : this(new HashSet<string>(StringComparer.OrdinalIgnoreCase), _ => { }) { }

    public ScanWindow(HashSet<string> existingPaths, Action<ScriptItem> onAdded)
    {
        InitializeComponent();
        _existingPaths = existingPaths;
        _onAdded = onAdded;
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
            try
            {
                found = await Task.Run(ProcessScanner.Scan);
            }
            catch (Exception ex)
            {
                ScanStatus.Text = "Scan failed: " + ex.Message;
                return;
            }

            ScanStatus.Text = found.Count == 0
                ? "No running script processes found."
                : $"Found {found.Count} running script(s).";
            foreach (var script in found)
                RowsHost.Children.Add(BuildRow(script));
        }
        finally
        {
            _scanning = false;
        }
    }

    private Control BuildRow(ScannedScript scanned)
    {
        var alreadyAdded = _existingPaths.Contains(scanned.ScriptPath);

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
            Text = $"{Path.GetFileName(scanned.ScriptPath)}   ({scanned.Interpreter}, pid {scanned.Pid})",
            FontWeight = FontWeight.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text = scanned.CommandLine,
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
            var prefill = new ScriptItem
            {
                Name = Path.GetFileNameWithoutExtension(scanned.ScriptPath),
                ScriptPath = scanned.ScriptPath,
                Arguments = scanned.Arguments,
            };
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
