using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

public partial class SettingsWindow : Window
{
    private App AppRef => (App)Application.Current!;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading = true;
    private bool _startupInitial;

    public SettingsWindow()
    {
        InitializeComponent();
        var config = AppRef.Config;

        ConfigPathBox.Text = ConfigService.ConfigDir;
        TsEnabledCheck.IsChecked = config.TimestampButtonEnabled;
        FormatBox.Text = string.IsNullOrEmpty(config.TimestampFormat) ? TimestampService.DefaultFormat : config.TimestampFormat;
        BulletFormatBox.Text = string.IsNullOrEmpty(config.TimeBulletFormat) ? "yyyy-MM-dd HH:mm" : config.TimeBulletFormat;
        LogEnabledCheck.IsChecked = config.WorkLogEnabled;
        SampleBox.Value = config.WorkLogSampleSeconds;
        IdleBox.Value = config.WorkLogIdleMinutes;
        WorkStartBox.Text = config.WorkHoursStart;
        WorkEndBox.Text = config.WorkHoursEnd;
        FillWorkDays(config);
        FillPresets();
        foreach (var (id, label) in AiService.ClaudeModels)
            AiModelCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        AiModelCombo.SelectedIndex = Math.Max(0, Array.FindIndex(AiService.ClaudeModels, m => m.Id == config.AiModel));
        AiProviderCombo.SelectedIndex = config.AiProvider == "custom" ? 2 : 0;
        AiCommandBox.Text = config.AiCommand;
        AiCommandBox.TextChanged += (_, _) => ApplyAiProvider();
        ApplyAiProvider();
        AiLanguageBox.Text = config.AiLanguage;
        AiHotkeyBox.Text = config.AiHotkey;
        CaptureHotkeyBox.Text = config.CaptureHotkey;
        CaptureOcrCheck.IsChecked = config.CaptureOcr;
        AiAutoTitleCheck.IsChecked = config.AiAutoTitle;
        AiDaySummaryCheck.IsChecked = config.AiDaySummary;
        ApplyAiHints();
        _dataFolderDraft = config.DataFolder;
        SyncTimersCheck.IsChecked = config.SyncTimers;
        ApplyDataFolder();

        _previewTimer.Tick += (_, _) => UpdatePreview();
        _previewTimer.Start();
        Closed += (_, _) => _previewTimer.Stop();
        Loaded += async (_, _) => await LoadStartupAsync();

        _loading = false;
        UpdatePreview();
        UpdateWorkHoursPreview();
    }

    private readonly CheckBox[] _dayChecks = new CheckBox[7];

    private void FillWorkDays(Models.AppConfig config)
    {
        string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        foreach (var d in new[] { 1, 2, 3, 4, 5, 6, 0 }) // Monday first
        {
            var check = new CheckBox { Content = names[d], IsChecked = config.WorkDays.Contains(d), Margin = new Thickness(0, 0, 12, 0) };
            check.IsCheckedChanged += (_, _) => UpdateWorkHoursPreview();
            _dayChecks[d] = check;
            WorkDaysPanel.Children.Add(check);
        }
    }

    private void WorkHours_Changed(object? sender, TextChangedEventArgs e) => UpdateWorkHoursPreview();

    /// <summary>Reads the working-hours controls; null (with a message) when a time is not HH:mm.</summary>
    private Models.AppConfig? ReadWorkHours(out string error)
    {
        error = "";
        if (!WorkHours.TryParseTime(WorkStartBox.Text, out _) || !WorkHours.TryParseTime(WorkEndBox.Text, out _))
        {
            error = "Working hours must be times like 09:00 or 17:30.";
            return null;
        }
        return new Models.AppConfig
        {
            WorkHoursStart = WorkStartBox.Text!.Trim(),
            WorkHoursEnd = WorkEndBox.Text!.Trim(),
            WorkDays = Enumerable.Range(0, 7).Where(d => _dayChecks[d]?.IsChecked == true).ToList(),
        };
    }

    private void UpdateWorkHoursPreview()
    {
        if (WorkHoursPreview is null || _dayChecks[0] is null) return;
        var draft = ReadWorkHours(out var error);
        WorkHoursPreview.Text = draft is null ? error : "Counts as working time: " + new WorkHours(draft).Describe();
    }

    private void ApplyAiHints()
    {
        AiHotkeyText.Text = !OperatingSystem.IsWindows()
            ? "Global hotkeys work on Windows only — use the tray menu’s “Ask AI…”."
            : AppRef.HotkeyError ?? "Opens “Ask AI” from any app. Keys: Ctrl, Alt, Shift, Win + a letter, digit, F1–F24 or Space.";
        CaptureHotkeyText.Text = !OperatingSystem.IsWindows()
            ? "Global hotkeys work on Windows only — use the tray menu’s “Capture Screenshot”."
            : AppRef.CaptureHotkeyError ?? "Takes a screenshot from any app. Keys: Ctrl, Alt, Shift, Win + a letter, digit, F1–F24 or Space.";
        AiDaySummaryText.Text = $"Working hours: {new WorkHours(AppRef.Config).Describe()} (Work log tab). " +
                                "Needs the work log; skipped on days with under 15 minutes of activity.";
    }

    private string SelectedProvider => AiProviderCombo.SelectedItem is ComboBoxItem { Tag: string p } ? p : "claude";

    private string SelectedModel => AiModelCombo.SelectedItem is ComboBoxItem { Tag: string m } ? m : AiService.DefaultModel;

    /// <summary>The command the chosen provider / model / custom text will run.</summary>
    private string DraftAiCommand => AiService.CommandFor(SelectedProvider, SelectedModel, AiCommandBox.Text);

    private void AiProvider_Changed(object? sender, SelectionChangedEventArgs e) => ApplyAiProvider();

    private void ApplyAiProvider()
    {
        // SelectionChanged fires while the XAML is still loading
        if (AiModelPanel is null || AiCustomPanel is null || AiCommandPreview is null || AiCommandBox is null) return;
        var custom = SelectedProvider == "custom";
        AiModelPanel.IsVisible = !custom;
        AiCustomPanel.IsVisible = custom;
        AiCommandPreview.Text = "Runs: " + DraftAiCommand;
    }

    private async void TestAi_Click(object? sender, RoutedEventArgs e)
    {
        var command = DraftAiCommand;
        TestAiButton.IsEnabled = false;
        AiTestText.IsVisible = true;
        AiTestText.Text = $"Asking {command}…";
        var result = await AiService.AskAsync("Reply with one short friendly sentence confirming you received this test.",
            command: command, timeout: TimeSpan.FromMinutes(2));
        TestAiButton.IsEnabled = true;
        AiTestText.Text = result.Ok ? $"✔ Answered in {result.Elapsed.TotalSeconds:0} s: {result.Text}" : "✖ " + result.Text;
    }

    private void FillPresets()
    {
        var now = DateTime.Now;
        foreach (var (description, format) in TimestampService.Presets)
        {
            TimestampService.TryFormat(format, now, out var example);
            PresetCombo.Items.Add(new ComboBoxItem { Content = $"{example}    —    {description}", Tag = format });
        }
    }

    private async Task LoadStartupAsync()
    {
        try
        {
            _startupInitial = await Task.Run(StartupService.IsEnabled);
            StartupCheck.IsChecked = _startupInitial;
        }
        catch
        {
            StartupCheck.IsEnabled = false;
            StartupCheck.Content += " (not available on this system)";
        }
    }

    private void Preset_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedItem is not ComboBoxItem { Tag: string format }) return;
        FormatBox.Text = format;
    }

    private void Format_Changed(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null || BulletPreviewText is null || BulletFormatBox is null) return;
        BulletPreviewText.Text = TimestampService.TryFormat(BulletFormatBox.Text, DateTime.Now, out var bullet)
            ? $"◷ {bullet} · your text"
            : "Invalid format — check the letters and quotes.";
        if (TimestampService.TryFormat(FormatBox.Text, DateTime.Now, out var text))
        {
            PreviewText.Text = text.Length == 0 ? "(empty)" : text;
            PreviewText.Opacity = 1;
        }
        else
        {
            PreviewText.Text = "Invalid format — check the letters and quotes.";
            PreviewText.Opacity = 0.6;
        }
    }

    private void OpenConfigFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.ConfigDir);
            Process.Start(new ProcessStartInfo { FileName = ConfigService.ConfigDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open the folder: " + ex.Message;
        }
    }

    // ---- data folder ---------------------------------------------------------------------

    /// <summary>The data folder chosen on screen (applied on Save); "" = this PC's settings folder.</summary>
    private string _dataFolderDraft = "";

    private static bool SameFolder(string a, string b)
    {
        static string Norm(string p) => p.Length == 0 ? "" : Path.GetFullPath(p).TrimEnd('\\', '/');
        try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
        catch { return a == b; }
    }

    /// <summary>The OneDrive folder on this PC (personal first), or null when OneDrive isn't set up.</summary>
    private static string? OneDriveRoot()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "OneDriveConsumer", "OneDriveCommercial", "OneDrive" }
                .Select(Environment.GetEnvironmentVariable)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));
        var cloud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "CloudStorage");
        return Directory.Exists(cloud) ? Directory.GetDirectories(cloud, "OneDrive*").FirstOrDefault() : null;
    }

    private void ApplyDataFolder()
    {
        var local = _dataFolderDraft.Length == 0;
        DataPathBox.Text = local ? ConfigService.ConfigDir + "   (this PC only)" : _dataFolderDraft;
        SyncTimersCheck.IsEnabled = !local;
        OneDriveButton.IsEnabled = OneDriveRoot() is not null;
        if (!OneDriveButton.IsEnabled) ToolTip.SetTip(OneDriveButton, "OneDrive isn't set up on this PC");
        var pending = !SameFolder(_dataFolderDraft, AppRef.Config.DataFolder);
        DataFolderText.Text = pending
            ? "Press Save to copy your notes, pictures and work log there and restart the app."
            : ConfigService.DataDirError
              ?? (local ? "Not synced." : $"Synced. This PC writes its work log as “{Environment.MachineName}”.");
    }

    private void UseOneDrive_Click(object? sender, RoutedEventArgs e)
    {
        if (OneDriveRoot() is not { } root) return;
        _dataFolderDraft = Path.Combine(root, "Toolshed360");
        ApplyDataFolder();
    }

    private async void ChooseDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Folder for notes, pictures and the work log",
        });
        if (folders.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]) is not { } path) return;
        _dataFolderDraft = path;
        ApplyDataFolder();
    }

    private void LocalDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        _dataFolderDraft = "";
        ApplyDataFolder();
    }

    private void OpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.DataDir);
            Process.Start(new ProcessStartInfo { FileName = ConfigService.DataDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open the folder: " + ex.Message;
        }
    }

    /// <summary>Copies the data to a newly chosen folder and switches to it; true when the app should restart.</summary>
    private async Task<bool> ApplyDataFolderChangeAsync(Models.AppConfig config)
    {
        var syncTimers = SyncTimersCheck.IsChecked == true && _dataFolderDraft.Length > 0;
        var folderChanged = !SameFolder(_dataFolderDraft, config.DataFolder);
        if (!folderChanged && syncTimers == config.SyncTimers) return false;

        var target = _dataFolderDraft.Length == 0 ? ConfigService.ConfigDir : _dataFolderDraft;
        if (folderChanged)
        {
            StatusText.Text = "Copying your notes, pictures and work log…";
            await Task.Run(() =>
            {
                Directory.CreateDirectory(target);
                NotesService.CopyTo(target);
                WorkLogService.CopyTo(target);
            });
            ConfigService.ApplyDataFolder(_dataFolderDraft);
            if (ConfigService.DataDirError is { } error) throw new IOException(error);
        }
        if (syncTimers && ConfigService.LoadTimers() is { } shared)
        {
            // another PC already shares timers: keep theirs and add the ones only this PC has
            config.Timers = shared.Concat(config.Timers.Where(t => shared.All(s => s.Id != t.Id))).ToList();
        }
        config.DataFolder = _dataFolderDraft;
        config.SyncTimers = syncTimers;
        return true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!TimestampService.TryFormat(FormatBox.Text, DateTime.Now, out _))
        {
            StatusText.Text = "The timestamp format is invalid — fix it before saving.";
            FormatBox.Focus();
            return;
        }
        if (!TimestampService.TryFormat(BulletFormatBox.Text, DateTime.Now, out _))
        {
            StatusText.Text = "The time-bullet format is invalid — fix it before saving.";
            BulletFormatBox.Focus();
            return;
        }

        var hours = ReadWorkHours(out var hoursError);
        if (hours is null)
        {
            StatusText.Text = hoursError;
            WorkStartBox.Focus();
            return;
        }

        var config = AppRef.Config;
        config.WorkHoursStart = hours.WorkHoursStart;
        config.WorkHoursEnd = hours.WorkHoursEnd;
        config.WorkDays = hours.WorkDays;
        config.TimestampButtonEnabled = TsEnabledCheck.IsChecked == true;
        config.TimestampFormat = FormatBox.Text ?? TimestampService.DefaultFormat;
        config.TimeBulletFormat = BulletFormatBox.Text ?? "yyyy-MM-dd HH:mm";
        config.WorkLogEnabled = LogEnabledCheck.IsChecked == true;
        config.WorkLogSampleSeconds = (int)(SampleBox.Value ?? 30m);
        config.WorkLogIdleMinutes = (int)(IdleBox.Value ?? 5m);
        config.AiProvider = SelectedProvider;
        config.AiModel = SelectedModel;
        config.AiCommand = (AiCommandBox.Text ?? "").Trim();
        config.AiLanguage = (AiLanguageBox.Text ?? "").Trim();
        config.AiHotkey = (AiHotkeyBox.Text ?? "").Trim();
        config.CaptureHotkey = (CaptureHotkeyBox.Text ?? "").Trim();
        config.CaptureOcr = CaptureOcrCheck.IsChecked == true;
        config.AiAutoTitle = AiAutoTitleCheck.IsChecked == true;
        config.AiDaySummary = AiDaySummaryCheck.IsChecked == true;

        bool restart;
        try
        {
            restart = await ApplyDataFolderChangeAsync(config);
        }
        catch (Exception ex)
        {
            ConfigService.ApplyDataFolder(config.DataFolder); // stay on the folder that worked
            StatusText.Text = "Could not use that data folder: " + ex.Message;
            return;
        }

        try
        {
            AppRef.SaveConfig();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
            return;
        }

        WorkLogService.Restart(config);
        AppRef.NotifySettingsChanged();
        ApplyAiHints();

        var status = AppRef.HotkeyError is not null ? "Saved, but the hotkey could not be registered (see the AI tab)."
            : AppRef.CaptureHotkeyError is not null ? "Saved, but the screenshot hotkey could not be registered (see the Capture tab)."
            : "Saved.";
        var wantStartup = StartupCheck.IsChecked == true;
        if (StartupCheck.IsEnabled && wantStartup != _startupInitial)
        {
            try
            {
                await Task.Run(() => StartupService.SetEnabled(wantStartup));
                _startupInitial = wantStartup;
            }
            catch (Exception ex)
            {
                status = "Saved, but could not change auto-start: " + ex.Message;
            }
        }
        StatusText.Text = status;
        if (!restart) return;
        await Dialogs.Message(this, "Data folder changed",
            $"Notes, pictures and the work log are now kept in:\n{ConfigService.DataDir}\n\nThe app restarts to finish the switch.");
        AppRef.Restart();
    }
}
