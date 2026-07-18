using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Views;

public partial class EditScriptWindow : Window
{
    private static readonly string[] InterpreterCodes =
    { "auto", "python", "pwsh", "bash", "node", "cmd", "direct", "custom" };

    private readonly ScriptItem? _existing;

    public EditScriptWindow() : this(null) { }

    public EditScriptWindow(ScriptItem? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            Title = "Edit Script";
            NameBox.Text = existing.Name;
            PathBox.Text = existing.ScriptPath;
            ArgsBox.Text = existing.Arguments;
            WorkDirBox.Text = existing.WorkingDirectory;
            CustomBox.Text = existing.CustomCommand;
            IconBox.Text = existing.IconPath;
            KeepOpenCheck.IsChecked = existing.KeepConsoleOpen;
            var idx = Array.IndexOf(InterpreterCodes, existing.Interpreter);
            InterpCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else
        {
            Title = "Add Script";
        }

        UpdateCustomEnabled();
        UpdateIconPreview();
    }

    private void InterpCombo_Changed(object? sender, SelectionChangedEventArgs e) => UpdateCustomEnabled();

    private void UpdateCustomEnabled()
    {
        if (CustomPanel is null) return; // fires during XAML load
        CustomPanel.IsEnabled = InterpCombo.SelectedIndex == Array.IndexOf(InterpreterCodes, "custom");
    }

    private void IconBox_Changed(object? sender, TextChangedEventArgs e) => UpdateIconPreview();

    private void UpdateIconPreview()
    {
        if (IconPreview is null) return; // fires during XAML load
        IconPreview.Source = UiHelpers.TryLoadBitmap(IconBox.Text);
    }

    private async void BrowseScript_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a script or program",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Scripts & programs")
                {
                    Patterns = new[] { "*.py", "*.ps1", "*.sh", "*.js", "*.bat", "*.cmd", "*.exe" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count > 0)
        {
            PathBox.Text = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileNameWithoutExtension(PathBox.Text);
        }
    }

    private async void BrowseWorkDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the working directory",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            WorkDirBox.Text = folders[0].Path.LocalPath;
    }

    private async void BrowseCustom_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the interpreter executable",
            AllowMultiple = false,
        });
        if (files.Count > 0)
            CustomBox.Text = files[0].Path.LocalPath;
    }

    private async void BrowseIcon_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an icon image",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        if (files.Count > 0)
            IconBox.Text = files[0].Path.LocalPath;
    }

    private void ClearIcon_Click(object? sender, RoutedEventArgs e) => IconBox.Text = "";

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0)
        {
            ErrorText.Text = "The script / program path is required.";
            ErrorText.IsVisible = true;
            return;
        }

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileNameWithoutExtension(path);

        Close(new ScriptItem
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = name!,
            ScriptPath = path,
            Arguments = ArgsBox.Text?.Trim() ?? "",
            WorkingDirectory = WorkDirBox.Text?.Trim() ?? "",
            Interpreter = InterpreterCodes[Math.Max(0, InterpCombo.SelectedIndex)],
            CustomCommand = CustomBox.Text?.Trim() ?? "",
            IconPath = IconBox.Text?.Trim() ?? "",
            KeepConsoleOpen = KeepOpenCheck.IsChecked == true,
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
