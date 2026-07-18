using System;
using System.Diagnostics;
using System.IO;

namespace ScriptAppDashboard.Services;

/// <summary>Registers the app to start when the user logs in (hidden in the tray).</summary>
public static class StartupService
{
    private const string AppName = "ScriptAppDashboard";
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "ScriptAppDashboard.exe");

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.scriptappdashboard.app.plist");

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
                        <string>com.scriptappdashboard.app</string>
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
