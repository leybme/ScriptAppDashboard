using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>Runs the user's interval timers: beep, shell command, or an AI CLI prompt.</summary>
public static class TimerService
{
    private static readonly Dictionary<Guid, DateTime> NextRun = new();
    private static readonly HashSet<Guid> Running = new();
    private static readonly Dictionary<Guid, string> LastOutput = new();
    private static readonly object Gate = new();

    private static AppConfig? _config;
    private static Timer? _ticker;

    /// <summary>Raised from a background thread after a timer finished (timer, output).</summary>
    public static event Action<IntervalTimerItem, string>? Ran;

    public static void Start(AppConfig config)
    {
        _config = config;
        _ticker = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static void Stop() => _ticker?.Dispose();

    public static string GetLastOutput(Guid id)
    {
        lock (Gate) return LastOutput.GetValueOrDefault(id, "");
    }

    /// <summary>Restarts the countdown of one timer after it was edited or deleted (the others keep theirs).</summary>
    public static void Reschedule(Guid id)
    {
        lock (Gate) NextRun.Remove(id);
    }

    /// <summary>Time left until the timer next fires, or null when it is off / not scheduled yet.</summary>
    public static TimeSpan? TimeUntilNext(IntervalTimerItem t)
    {
        if (IsPaused) return null;
        lock (Gate)
            return t.Enabled && NextRun.TryGetValue(t.Id, out var next) ? next - DateTime.Now : null;
    }

    /// <summary>Raised (from any thread) when timers are paused or resumed, including the automatic resume.</summary>
    public static event Action? PauseChanged;

    /// <summary>Paused until this time (DateTime.MaxValue = until resumed), or null when running.</summary>
    public static DateTime? PausedUntil => _config?.TimersPausedUntil;

    public static bool IsPaused => PausedUntil is { } until && until > DateTime.Now;

    /// <summary>Pauses every timer; pass null to pause until <see cref="Resume"/> is called.</summary>
    public static void Pause(TimeSpan? duration)
    {
        if (_config is null) return;
        _config.TimersPausedUntil = duration is { } d ? DateTime.Now + d : DateTime.MaxValue;
        PauseChanged?.Invoke();
    }

    /// <summary>Resumes the timers; every countdown starts again from now, so nothing fires all at once.</summary>
    public static void Resume()
    {
        if (_config is null) return;
        _config.TimersPausedUntil = null;
        lock (Gate) NextRun.Clear();
        PauseChanged?.Invoke();
    }

    private static void Tick()
    {
        if (_config is null) return;
        if (_config.TimersPausedUntil is { } until)
        {
            if (until > DateTime.Now) return;
            Resume(); // the pause ran out
        }
        List<IntervalTimerItem> due = new();
        try
        {
            lock (Gate)
            {
                var now = DateTime.Now;
                foreach (var t in _config.Timers.ToArray())
                {
                    if (!t.Enabled || t.IntervalMinutes <= 0) continue;
                    if (!NextRun.TryGetValue(t.Id, out var next))
                    {
                        NextRun[t.Id] = now.AddMinutes(t.IntervalMinutes);
                        continue;
                    }
                    if (now >= next)
                    {
                        NextRun[t.Id] = now.AddMinutes(t.IntervalMinutes);
                        due.Add(t);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            return; // the list was edited on the UI thread mid-copy; try again next second
        }
        foreach (var t in due) RunNow(t);
    }

    /// <summary>Runs a timer immediately on a worker thread (skipped if it is still running from last time).</summary>
    public static void RunNow(IntervalTimerItem timer)
    {
        lock (Gate)
        {
            if (!Running.Add(timer.Id)) return;
        }
        Task.Run(() =>
        {
            string output;
            try
            {
                output = timer.Action switch
                {
                    "command" => RunShell(timer.Command, stdin: null, TimeSpan.FromMinutes(5)),
                    "ai" => RunShell(timer.AiCommand, stdin: timer.Prompt, TimeSpan.FromMinutes(10)),
                    _ => Beep(),
                };
            }
            catch (Exception ex)
            {
                output = "Error: " + ex.Message;
            }

            lock (Gate)
            {
                LastOutput[timer.Id] = $"[{DateTime.Now:HH:mm:ss}] {output}";
                Running.Remove(timer.Id);
            }
            WorkLogService.LogTimerRun(timer, output);
            Ran?.Invoke(timer, output);
        });
    }

    private static string Beep()
    {
        try
        {
            if (OperatingSystem.IsWindows()) NativeBeep.Beep(880, 350);
            else Console.Write('\a');
            return "Beep";
        }
        catch (Exception ex)
        {
            return "Beep failed: " + ex.Message;
        }
    }

    /// <summary>Runs a command line through the platform shell; stdin (if any) is fed to the process.</summary>
    private static string RunShell(string commandLine, string? stdin, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return "Nothing to run — the command is empty.";

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"" + commandLine + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(commandLine);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the process.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!string.IsNullOrEmpty(stdin)) proc.StandardInput.Write(stdin);
        proc.StandardInput.Close();

        if (!proc.WaitForExit(timeout))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return $"Timed out after {timeout.TotalMinutes:0} min.";
        }

        var text = (stdout.Result + (stderr.Result.Length > 0 ? "\n" + stderr.Result : "")).Trim();
        if (text.Length == 0) text = $"(no output, exit code {proc.ExitCode})";
        else if (proc.ExitCode != 0) text += $"\n(exit code {proc.ExitCode})";
        return text.Length > 8000 ? text[..8000] + "…" : text;
    }

    private static class NativeBeep
    {
        [DllImport("kernel32.dll")] public static extern bool Beep(uint frequency, uint duration);
    }
}
