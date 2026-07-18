using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ScriptAppDashboard.Services;

public sealed record ScannedScript(int Pid, string Interpreter, string ScriptPath, string Arguments, string CommandLine);

/// <summary>Finds script interpreter processes (python, node, powershell, …) that are currently running.</summary>
public static class ProcessScanner
{
    private static readonly HashSet<string> InterpreterNames = new(StringComparer.OrdinalIgnoreCase)
    { "python", "python3", "pythonw", "py", "node", "pwsh", "powershell", "bash", "sh", "zsh", "cmd" };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".py", ".ps1", ".sh", ".js", ".bat", ".cmd" };

    public static List<ScannedScript> Scan()
    {
        var results = new List<ScannedScript>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pid, cmdline) in GetCommandLines())
        {
            if (pid == Environment.ProcessId) continue;
            if (cmdline.Contains("Win32_Process", StringComparison.OrdinalIgnoreCase)) continue; // our own scan helper

            var tokens = Tokenize(cmdline);
            if (tokens.Count < 2) continue;

            var exeName = Path.GetFileNameWithoutExtension(tokens[0]);
            if (!InterpreterNames.Contains(exeName)) continue;

            var scriptIdx = -1;
            for (var i = 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.StartsWith('-')) continue;
                if (OperatingSystem.IsWindows() && t.StartsWith('/')) continue;
                if (ScriptExtensions.Contains(Path.GetExtension(t)))
                {
                    scriptIdx = i;
                    break;
                }
            }
            if (scriptIdx < 0) continue;

            var script = tokens[scriptIdx];
            if (!seen.Add(script)) continue;

            var args = string.Join(' ', tokens.Skip(scriptIdx + 1).Select(QuoteIfNeeded));
            results.Add(new ScannedScript(pid, exeName.ToLowerInvariant(), script, args, cmdline.Trim()));
        }
        return results;
    }

    private static List<(int Pid, string CommandLine)> GetCommandLines()
        => OperatingSystem.IsWindows() ? GetWindowsCommandLines() : GetUnixCommandLines();

    private static List<(int, string)> GetWindowsCommandLines()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine } | Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress");

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);

        var results = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(output)) return results;

        using var doc = JsonDocument.Parse(output);

        void AddElement(JsonElement el)
        {
            if (el.TryGetProperty("ProcessId", out var pidEl) &&
                el.TryGetProperty("CommandLine", out var cmdEl) &&
                cmdEl.ValueKind == JsonValueKind.String)
            {
                results.Add((pidEl.GetInt32(), cmdEl.GetString() ?? ""));
            }
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var el in doc.RootElement.EnumerateArray()) AddElement(el);
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            AddElement(doc.RootElement);

        return results;
    }

    private static List<(int, string)> GetUnixCommandLines()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/ps",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-axo");
        psi.ArgumentList.Add("pid=,args=");

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);

        var results = new List<(int, string)>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var space = trimmed.IndexOf(' ');
            if (space <= 0) continue;
            if (int.TryParse(trimmed[..space], out var pid))
                results.Add((pid, trimmed[(space + 1)..].Trim()));
        }
        return results;
    }

    private static List<string> Tokenize(string cmdline)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in cmdline)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    private static string QuoteIfNeeded(string token)
        => token.Contains(' ') ? $"\"{token}\"" : token;
}
