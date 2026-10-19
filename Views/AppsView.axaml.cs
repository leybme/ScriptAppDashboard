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

/// <summary>The apps tab: the user's scripts and programs as tiles.</summary>
public partial class AppsView : ShellTab
{
    private static readonly Color[] TileColors =
    {
        Color.Parse("#3A6EFF"), Color.Parse("#7A4FD8"), Color.Parse("#0E9F6E"),
        Color.Parse("#D8543F"), Color.Parse("#C7842B"), Color.Parse("#12867F"),
        Color.Parse("#B84C86"),
    };

    private App AppRef => (App)Application.Current!;
    private AppConfig Config => AppRef.Config;

    private readonly AiChatProfile _chatProfile;

    public override AiChatProfile? ChatProfile => _chatProfile;

    public AppsView()
    {
        InitializeComponent();
        _chatProfile = new AiChatProfile
        {
            Watermark = "Ask about your scripts, or for a new one… (Enter sends, Shift+Enter is a new line)",
            EmptyHint = "Ask about your scripts:\n\n• “Which of my scripts touch the network?”\n• “Write a PowerShell script that cleans my Downloads folder”\n\n" +
                        "To see what one script does, right-click its tile → Explain with AI.",
            ShareText = "Send my script list",
            ShareTip = "The AI sees each tile's name, file, interpreter and arguments (not the script's code). Untick to chat without sharing it.",
            GetContext = () => new AiChatContext(ScriptsText(), "the script dashboard", null, null, AiPrompts.AppGuide),
            AnswerActions = new[] { ("note", "Save as note") },
            OnAnswerAction = (_, text) => Task.FromResult(SaveAnswerAsNote("Ask AI", "#ai", text, null)),
            Quick = new (string, string, Action)[]
            {
                ("Write a script…", "Describe a script and let the AI write it", () => Chat.Prefill("Write a script that ")),
                ("Summarise my scripts", "A short overview of what the tiles do", () => _ = Chat.Ask("Give me a short overview of my scripts, grouped by purpose.")),
            },
        };
        RefreshTiles();
        ApplyTimestampButton();
        AppRef.SettingsChanged += ApplyTimestampButton;
    }

    /// <summary>What the chat is told about the dashboard: one line per tile.</summary>
    private string ScriptsText()
    {
        var lines = Config.Scripts.Take(200).Select(s =>
            $"- {s.Name}: {s.ScriptPath}" + (s.Arguments.Length > 0 ? " " + s.Arguments : "") +
            $" ({(s.Interpreter == "custom" ? s.CustomCommand : s.Interpreter)})");
        return string.Join("\n", lines);
    }

    /// <summary>Saves an AI answer as a note; returns the message for the chat.</summary>
    private string SaveAnswerAsNote(string title, string tags, string text, string? intro)
    {
        var note = AiService.AnswerToNote(title, tags, text, intro);
        try
        {
            AppRef.AddNote(note);
            return $"Saved as the note “{note.Title}”.";
        }
        catch (Exception ex)
        {
            return "Could not save the note: " + ex.Message;
        }
    }

    private void ApplyTimestampButton()
    {
        CopyTimeButton.IsVisible = Config.TimestampButtonEnabled;
        ToolTip.SetTip(CopyTimeButton,
            $"Copy the current time to the clipboard — now: {TimestampService.Now(Config.TimestampFormat)}\n(format is set in Settings)");
    }

    private async void CopyTimeButton_Click(object? sender, RoutedEventArgs e)
    {
        var text = TimestampService.Now(Config.TimestampFormat);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SetStatus("The clipboard is not available.");
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            SetStatus($"Copied “{text}” — paste it where you need it.");
            ApplyTimestampButton(); // refresh the tooltip's "now"
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy to the clipboard: " + ex.Message);
        }
    }

    private void RefreshTiles()
    {
        TileHost.Children.Clear();
        EmptyHint.IsVisible = Config.Scripts.Count == 0;
        _chatProfile?.SetLabel($"Apps: {Config.Scripts.Count} " + (Config.Scripts.Count == 1 ? "script" : "scripts"));
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
        var explain = new MenuItem { Header = "Explain with AI…" };
        explain.Click += async (_, _) => await ExplainItem(item);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += async (_, _) => await RemoveItem(item);
        menu.Items.Add(run);
        menu.Items.Add(edit);
        menu.Items.Add(openFolder);
        menu.Items.Add(explain);
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
        var result = await dialog.ShowDialog<ScriptItem?>(Host);
        if (result is not null)
        {
            Config.Scripts.Add(result);
            SaveAndRefresh($"Added “{result.Name}”.");
        }
    }

    private async Task EditItem(ScriptItem item)
    {
        var dialog = new EditScriptWindow(item);
        var result = await dialog.ShowDialog<ScriptItem?>(Host);
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
        await scan.ShowDialog(Host);
    }

    private async void AiScriptButton_Click(object? sender, RoutedEventArgs e)
    {
        var result = await new AiScriptWindow().ShowDialog<ScriptItem?>(Host);
        if (result is null) return;
        Config.Scripts.Add(result);
        SaveAndRefresh($"Added “{result.Name}” ({result.ScriptPath}).");
    }

    /// <summary>Shows what a script does (and anything risky) before it is run, by sending its source to the AI.</summary>
    private async Task ExplainItem(ScriptItem item)
    {
        const int maxChars = 60000;
        string content;
        bool truncated;
        try
        {
            if (!File.Exists(item.ScriptPath))
            {
                await Dialogs.Message(this, "Explain with AI", $"The file “{item.ScriptPath}” doesn't exist.");
                return;
            }
            var bytes = await File.ReadAllBytesAsync(item.ScriptPath);
            if (bytes.Take(8000).Contains((byte)0))
            {
                await Dialogs.Message(this, "Explain with AI",
                    $"“{Path.GetFileName(item.ScriptPath)}” is a program, not a text script, so the AI can't read what it does.");
                return;
            }
            content = System.Text.Encoding.UTF8.GetString(bytes);
            truncated = content.Length > maxChars;
            if (truncated) content = content[..maxChars];
        }
        catch (Exception ex)
        {
            await Dialogs.Message(this, "Explain with AI", "Could not read the script: " + ex.Message);
            return;
        }

        var interpreter = item.Interpreter == "custom" ? item.CustomCommand : item.Interpreter;
        var prompt = AiPrompts.ExplainScript(item.ScriptPath, interpreter, item.Arguments, content, truncated);
        Shell.ShowChat(true);
        if (!Chat.RunRequest($"What “{item.Name}” does", (ct, partial) => AiService.AskStreamAsync(prompt, partial, ct),
                new[] { ("note", "Save as note") },
                (_, text) => Task.FromResult(SaveAnswerAsNote($"Script: {item.Name}", "#script #ai", text, $"`{item.ScriptPath}`"))))
            SetStatus("The AI chat is still waiting for an answer — try again in a moment.");
    }

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
