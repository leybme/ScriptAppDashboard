using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Settings live in <see cref="ConfigDir"/> on this PC. Notes, images and the work log live in <see cref="DataDir"/>,
/// which can be a OneDrive (or other synced) folder shared by several PCs.
/// </summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>This PC's settings folder. SCRIPTAPPDASHBOARD_DATA overrides it (tests, demos, portable use).</summary>
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("SCRIPTAPPDASHBOARD_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptAppDashboard");

    private static string? _dataDir;

    /// <summary>Where notes, images and the work log are kept: the configured data folder, or <see cref="ConfigDir"/>.</summary>
    public static string DataDir => _dataDir ?? ConfigDir;

    /// <summary>True when a separate (usually synced) data folder is in use.</summary>
    public static bool IsSynced => _dataDir is not null;

    /// <summary>Why the configured data folder could not be used (the local folder is used instead), or null.</summary>
    public static string? DataDirError { get; private set; }

    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    private static string TimersFile => Path.Combine(DataDir, "timers.json");

    public static AppConfig Load()
    {
        var config = new AppConfig();
        try
        {
            if (File.Exists(ConfigFile))
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile)) ?? new AppConfig();
        }
        catch
        {
            // corrupt config — start fresh rather than crash on launch
        }
        ApplyDataFolder(config.DataFolder);
        if (config.SyncTimers && IsSynced && LoadTimers() is { } timers) config.Timers = timers;
        return config;
    }

    /// <summary>Points <see cref="DataDir"/> at the folder (created if needed); an unusable folder falls back to local.</summary>
    public static void ApplyDataFolder(string? folder)
    {
        _dataDir = null;
        DataDirError = null;
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            var full = Path.GetFullPath(folder.Trim());
            if (string.Equals(full.TrimEnd('\\', '/'), Path.GetFullPath(ConfigDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(full);
            _dataDir = full;
        }
        catch (Exception ex)
        {
            DataDirError = $"The data folder “{folder}” can't be used ({ex.Message}). Notes and the work log are kept on this PC for now.";
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, JsonOptions));
        if (config.SyncTimers && IsSynced) SaveTimers(config.Timers);
    }

    /// <summary>The timers shared through the data folder, or null when there is no (readable) timers.json.</summary>
    public static List<IntervalTimerItem>? LoadTimers()
    {
        try
        {
            if (!File.Exists(TimersFile)) return null;
            var json = File.ReadAllText(TimersFile);
            var timers = JsonSerializer.Deserialize<List<IntervalTimerItem>>(json);
            if (timers is not null) _lastTimersJson = json;
            return timers;
        }
        catch
        {
            return null; // half-synced or corrupt: keep the timers we have
        }
    }

    /// <summary>The shared timers when another PC changed timers.json since this PC last read or wrote it, else null.</summary>
    public static List<IntervalTimerItem>? LoadTimersIfChanged()
    {
        try
        {
            return File.Exists(TimersFile) && File.ReadAllText(TimersFile) != _lastTimersJson ? LoadTimers() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string _lastTimersJson = "";

    private static void SaveTimers(List<IntervalTimerItem> timers)
    {
        var json = JsonSerializer.Serialize(timers, JsonOptions);
        if (json == _lastTimersJson && File.Exists(TimersFile)) return; // unchanged: don't wake the sync client
        AtomicWrite(TimersFile, json);
        _lastTimersJson = json;
    }

    /// <summary>Writes through a temp file and a rename, so a sync client or reader never sees half a file.</summary>
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
