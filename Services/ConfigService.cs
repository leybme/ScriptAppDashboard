using System;
using System.IO;
using System.Text.Json;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where config, notes and the work log live. SCRIPTAPPDASHBOARD_DATA overrides it (tests, demos, portable use).</summary>
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("SCRIPTAPPDASHBOARD_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptAppDashboard");

    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile)) ?? new AppConfig();
        }
        catch
        {
            // corrupt config — start fresh rather than crash on launch
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, JsonOptions));
    }
}
