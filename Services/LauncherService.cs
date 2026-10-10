using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

public static class LauncherService
{
    public static void Launch(ScriptItem item)
    {
        WorkLogService.LogLaunch(item);

        var scriptPath = item.ScriptPath;
        var workDir = !string.IsNullOrWhiteSpace(item.WorkingDirectory)
            ? item.WorkingDirectory
            : (Path.GetDirectoryName(scriptPath) is { Length: > 0 } dir ? dir : Environment.CurrentDirectory);

        var mode = string.IsNullOrEmpty(item.Interpreter) || item.Interpreter == "auto"
            ? DetectMode(Path.GetExtension(scriptPath))
            : item.Interpreter;

        if (mode == "shell")
        {
            // no known interpreter — let the OS open it with the default handler
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                WorkingDirectory = workDir,
            });
            return;
        }

        string exe, prefixArgs;
        switch (mode)
        {
            case "python": (exe, prefixArgs) = (OperatingSystem.IsWindows() ? "python" : "python3", ""); break;
            case "pwsh": (exe, prefixArgs) = OperatingSystem.IsWindows()
                    ? ("powershell", "-NoProfile -ExecutionPolicy Bypass -File ")
                    : ("pwsh", "-File "); break;
            case "bash": (exe, prefixArgs) = ("bash", ""); break;
            case "node": (exe, prefixArgs) = ("node", ""); break;
            case "cmd": (exe, prefixArgs) = ("cmd.exe", "/c "); break;
            case "direct": (exe, prefixArgs) = ("", ""); break;
            case "custom":
                SplitCommand(item.CustomCommand, out exe, out prefixArgs);
                if (exe.Length == 0)
                    throw new InvalidOperationException("Custom command is empty — set it in the script's settings.");
                break;
            default: (exe, prefixArgs) = ("", ""); break;
        }

        if (item.KeepConsoleOpen && OperatingSystem.IsWindows())
        {
            var inner = exe.Length == 0
                ? $"\"{scriptPath}\" {item.Arguments}".Trim()
                : $"\"{exe}\" {prefixArgs}\"{scriptPath}\" {item.Arguments}".Trim();
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/s /k \"{inner}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
            });
            return;
        }

        if (item.KeepConsoleOpen && OperatingSystem.IsMacOS())
        {
            LaunchInMacTerminal(exe, prefixArgs, scriptPath, item.Arguments, workDir);
            return;
        }

        var psi = new ProcessStartInfo { WorkingDirectory = workDir, UseShellExecute = false };
        if (exe.Length == 0)
        {
            psi.FileName = scriptPath;
            psi.Arguments = item.Arguments;
        }
        else
        {
            psi.FileName = exe;
            psi.Arguments = $"{prefixArgs}\"{scriptPath}\" {item.Arguments}".Trim();
        }
        Process.Start(psi);
    }

    private static string DetectMode(string extension) => extension.ToLowerInvariant() switch
    {
        ".py" => "python",
        ".ps1" => "pwsh",
        ".sh" => "bash",
        ".js" => "node",
        ".bat" or ".cmd" => "cmd",
        ".exe" => "direct",
        _ => "shell",
    };

    [SupportedOSPlatform("macos")]
    private static void LaunchInMacTerminal(string exe, string prefixArgs, string script, string args, string workDir)
    {
        var cmd = exe.Length == 0
            ? $"\"{script}\" {args}".Trim()
            : $"\"{exe}\" {prefixArgs}\"{script}\" {args}".Trim();
        var file = Path.Combine(Path.GetTempPath(), $"sad-{Guid.NewGuid():N}.command");
        File.WriteAllText(file, $"#!/bin/bash\ncd \"{workDir}\"\n{cmd}\n");
        File.SetUnixFileMode(file,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { file }, UseShellExecute = false });
    }

    /// <summary>Splits "C:\venv\python.exe -u" into the executable and its leading arguments.</summary>
    private static void SplitCommand(string command, out string exe, out string prefixArgs)
    {
        command = command?.Trim() ?? "";
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
            {
                exe = command[1..end];
                prefixArgs = command[(end + 1)..].Trim();
            }
            else
            {
                exe = command.Trim('"');
                prefixArgs = "";
            }
        }
        else
        {
            var space = command.IndexOf(' ');
            if (space > 0)
            {
                exe = command[..space];
                prefixArgs = command[(space + 1)..].Trim();
            }
            else
            {
                exe = command;
                prefixArgs = "";
            }
        }
        if (prefixArgs.Length > 0) prefixArgs += " ";
    }
}
