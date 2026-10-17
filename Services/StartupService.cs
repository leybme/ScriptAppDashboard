using System;
using System.Diagnostics;
using System.IO;

namespace ScriptAppDashboard.Services;

/// <summary>Registers the app to start when the user logs in (hidden in the tray).</summary>
public static class StartupService
{
    private const string AppName = "Toolshed360";
    private const string LegacyAppName = "ScriptAppDashboard";
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Toolshed360.exe");

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.toolshed360.app.plist");

    private static string LegacyPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.scriptappdashboard.app.plist");

    /// <summary>If auto-start was set up under the old name, replaces it with an entry that starts this exe.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            if (OperatingSystem.IsWindows() && RunReg("query", RunKey, "/v", LegacyAppName) == 0)
            {
                RunReg("delete", RunKey, "/v", LegacyAppName, "/f");
                SetEnabled(true);
            }
            else if (OperatingSystem.IsMacOS() && File.Exists(LegacyPlistPath))
            {
                File.Delete(LegacyPlistPath);
                SetEnabled(true);
            }
        }
        catch
        {
            // best effort — the user can toggle auto-start in Settings
        }
    }

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
            return RunReg("query", RunKey, "/v", AppName) == 0;
        if (OperatingSystem.IsMacOS())
            return File.Exists(PlistPath);
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            if (enabled)
                RunReg("add", RunKey, "/v", AppName, "/t", "REG_SZ", "/d", $"\"{ExePath}\" --minimized", "/f");
            else
                RunReg("delete", RunKey, "/v", AppName, "/f");
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
                File.WriteAllText(PlistPath, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                        <key>Label</key>
                        <string>com.toolshed360.app</string>
                        <key>ProgramArguments</key>
                        <array>
                            <string>{ExePath}</string>
                            <string>--minimized</string>
                        </array>
                        <key>RunAtLoad</key>
                        <true/>
                    </dict>
                    </plist>
                    """);
            }
            else if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }
        }
        else
        {
            throw new PlatformNotSupportedException("Auto-start is supported on Windows and macOS only.");
        }
    }

    private static int RunReg(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "reg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
