using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>Describe a script, let the AI write it, review it, then save it as a file with a dashboard tile.</summary>
public partial class AiScriptWindow : Window
{
    private static readonly (string Label, string Language, string Extension)[] Languages = OperatingSystem.IsWindows()
        ? new[] { ("PowerShell", "PowerShell 5.1", ".ps1"), ("Python", "Python 3", ".py"), ("Batch (cmd)", "Windows batch (cmd)", ".bat"), ("Node.js", "Node.js", ".js"), ("Bash", "Bash", ".sh") }
        : new[] { ("Bash", "Bash", ".sh"), ("Python", "Python 3", ".py"), ("Node.js", "Node.js", ".js"), ("PowerShell", "PowerShell 7", ".ps1") };

    private CancellationTokenSource? _cancel;

    public AiScriptWindow()
    {
        InitializeComponent();
        foreach (var (label, _, extension) in Languages) LanguageCombo.Items.Add(new ComboBoxItem { Content = $"{label}  ({extension})" });
        LanguageCombo.SelectedIndex = 0;
        FolderBox.Text = Path.Combine(ConfigService.ConfigDir, "scripts");
        Opened += (_, _) => DescriptionBox.Focus();
        Closed += (_, _) => _cancel?.Cancel();
    }

    private async void Generate_Click(object? sender, RoutedEventArgs e)
    {
        if (_cancel is not null)
        {
            _cancel.Cancel();
            return;
        }
        var description = (DescriptionBox.Text ?? "").Trim();
        if (description.Length == 0)
        {
            StatusText.Text = "Describe what the script should do first.";
            DescriptionBox.Focus();
            return;
        }
        var (_, language, extension) = Languages[Math.Max(0, LanguageCombo.SelectedIndex)];

        _cancel = new CancellationTokenSource();
        GenerateButton.Content = "Stop";
        SaveButton.IsEnabled = false;
        StatusText.Text = AiService.Asking("write the script");
        var result = await AiService.AskAsync(AiPrompts.GenerateScript(description, language), _cancel.Token);
        _cancel.Dispose();
        _cancel = null;
        if (!IsVisible) return;
        GenerateButton.Content = "✨ Write it again";

        if (!result.Ok)
        {
            StatusText.Text = AiService.Failed(result);
            return;
        }
        CodeBox.Text = AiService.ExtractCode(result.Text).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        var name = Regex.Match(result.Text, @"FILENAME:\s*`?([^\s`]+)`?").Groups[1].Value;
        name = NotesService.SafeFileName(Path.GetFileNameWithoutExtension(name), "") is { Length: > 0 } n ? n : Slug(description);
        FileNameBox.Text = name + extension;
        SaveButton.IsEnabled = true;
        StatusText.Text = $"Written in {result.Elapsed.TotalSeconds:0} s. Read it, change anything you like, then save.";
    }

    private static string Slug(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Take(4);
        var slug = string.Join("-", words);
        return slug.Length > 0 ? slug : "ai-script";
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where to save the script" });
        if (folders.Count > 0) FolderBox.Text = folders[0].Path.LocalPath;
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text ?? "";
        var fileName = (FileNameBox.Text ?? "").Trim();
        var folder = (FolderBox.Text ?? "").Trim();
        if (code.Trim().Length == 0 || fileName.Length == 0 || folder.Length == 0)
        {
            StatusText.Text = "The script, file name and folder are all needed.";
            return;
        }
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = "The file name contains characters that aren't allowed.";
            FileNameBox.Focus();
            return;
        }

        var path = Path.Combine(folder, fileName);
        if (File.Exists(path) && !await Dialogs.Confirm(this, "Replace file", $"{path} already exists. Replace it?", "Replace"))
            return;
        try
        {
            Directory.CreateDirectory(folder);
            // Windows PowerShell 5.1 reads a .ps1 without a BOM as ANSI, which breaks non-ASCII text
            var bom = OperatingSystem.IsWindows() && path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(path, code, new UTF8Encoding(bom));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
            return;
        }

        Close(new ScriptItem
        {
            Name = Path.GetFileNameWithoutExtension(fileName).Replace('-', ' ').Replace('_', ' '),
            ScriptPath = path,
            KeepConsoleOpen = true, // a new script's output (and errors) should be readable
            IconKind = "CreationOutline",
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
