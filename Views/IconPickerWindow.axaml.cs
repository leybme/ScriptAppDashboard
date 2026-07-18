using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;

namespace ScriptAppDashboard.Views;

/// <summary>Searchable picker over the Material Design Icons set. Closes with the chosen kind name, or null.</summary>
public partial class IconPickerWindow : Window
{
    private const int MaxResults = 240;

    private static readonly string[] AllNames = Enum.GetNames<MaterialIconKind>();

    // shown before the user types anything — common icons for dev tools
    private static readonly string[] Suggested =
    {
        "Console", "ConsoleLine", "Powershell", "Bash", "LanguagePython", "Snake",
        "NodeJs", "LanguageJavascript", "CodeBraces", "CodeTags", "Script", "ScriptText",
        "Application", "ApplicationBrackets", "Apps", "Robot", "RobotOutline",
        "Database", "DatabaseSearch", "Server", "ServerNetwork", "Lan", "Wifi", "Web",
        "Cloud", "CloudUpload", "CloudDownload", "Download", "Upload",
        "Folder", "FolderOpen", "FileDocument", "FileExcel", "FilePdfBox", "NotebookOutline",
        "ChartLine", "ChartBar", "ChartPie", "Calculator",
        "Cog", "Cogs", "Wrench", "Hammer", "Tools", "Bug", "TestTube", "Flask",
        "Rocket", "RocketLaunch", "LightningBolt", "Flash", "Play", "PlayCircle",
        "Sync", "Refresh", "Update", "Timer", "ClockOutline", "Alarm", "CalendarMonth",
        "Email", "Bell", "Home", "Magnify", "Key", "Lock", "ShieldCheck",
        "Camera", "Video", "Music", "Printer", "Monitor", "Laptop", "Cellphone",
        "Memory", "Chip", "Harddisk", "Usb", "Power", "Git", "Github", "Docker",
    };

    public IconPickerWindow()
    {
        InitializeComponent();
        ShowSuggested();
    }

    private void SearchBox_Changed(object? sender, TextChangedEventArgs e)
    {
        if (IconHost is null) return; // fires during XAML load
        var query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0)
        {
            ShowSuggested();
            return;
        }

        var matches = AllNames
            .Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(n => n.Length)
            .ToList();
        ShowIcons(matches.Take(MaxResults));
        ResultInfo.Text = matches.Count == 0
            ? "No icons match your search."
            : matches.Count > MaxResults
                ? $"{matches.Count} matches — showing the first {MaxResults}. Type more to narrow down."
                : $"{matches.Count} match(es).";
    }

    private void ShowSuggested()
    {
        ShowIcons(Suggested.Where(n => Enum.TryParse<MaterialIconKind>(n, out _)));
        ResultInfo.Text = "Suggestions — type above to search the full Material Design Icons set (Apache 2.0).";
    }

    private void ShowIcons(IEnumerable<string> names)
    {
        IconHost.Children.Clear();
        foreach (var name in names)
        {
            if (!Enum.TryParse<MaterialIconKind>(name, out var kind)) continue;
            var button = new Button
            {
                Width = 56,
                Height = 56,
                Margin = new Thickness(3),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new MaterialIcon { Kind = kind, Width = 28, Height = 28 },
            };
            ToolTip.SetTip(button, name);
            var chosen = name;
            button.Click += (_, _) => Close(chosen);
            IconHost.Children.Add(button);
        }
    }
}
