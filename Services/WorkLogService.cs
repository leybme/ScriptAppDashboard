using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

public sealed record OpenWindow(string App, string Title);

/// <summary>
/// Records what the user works on to daily JSONL files in %APPDATA%\ScriptAppDashboard\worklog.
/// Line kinds: "s" = foreground sample, "o" = open windows + running scripts snapshot,
/// "l" = dashboard launch, "t" = interval timer run, "n" = note saved.
/// </summary>
public static class WorkLogService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static AppConfig? _config;
    private static Timer? _timer;
    private static DateTime _lastSampleUtc;
    private static string _lastOpenHash = "";
    private static DateTime _lastOpenWrite = DateTime.MinValue;
    private static DateTime _lastScriptScan = DateTime.MinValue;
    private static List<string> _lastScripts = new();
    private static int _sampling;

    public static string LogDir => Path.Combine(ConfigService.ConfigDir, "worklog");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static void Start(AppConfig config)
    {
        _config = config;
        if (!IsSupported) return;
        _lastSampleUtc = DateTime.UtcNow;
        var period = TimeSpan.FromSeconds(Math.Max(5, config.WorkLogSampleSeconds));
        _timer = new Timer(_ => Sample(), null, period, period);
    }

    public static void Stop() => _timer?.Dispose();

    /// <summary>Re-applies the sampling interval after the settings changed.</summary>
    public static void Restart(AppConfig config)
    {
        Stop();
        Start(config);
    }

    public static void LogLaunch(ScriptItem item)
        => Append(new { t = Now(), k = "l", name = item.Name, path = item.ScriptPath, args = item.Arguments });

    public static void LogTimerRun(IntervalTimerItem timer, string output)
        => Append(new { t = Now(), k = "t", name = timer.Name, action = timer.Action, output = Truncate(output, 2000) });

    public static void LogNote(NoteItem note)
        => Append(new { t = Now(), k = "n", title = note.Title, tags = note.Tags });

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static void Append(object entry)
    {
        if (_config is { WorkLogEnabled: false }) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                var file = Path.Combine(LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
                File.AppendAllText(file, JsonSerializer.Serialize(entry, Json) + "\n", new UTF8Encoding(false));
            }
        }
        catch
        {
            // logging must never disturb the app
        }
    }

    private static void Sample()
    {
        if (_config is null || !_config.WorkLogEnabled) return;
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var nowUtc = DateTime.UtcNow;
            // cap the elapsed time so sleeping/hibernating is not counted as work
            var elapsed = Math.Min((nowUtc - _lastSampleUtc).TotalSeconds, _config.WorkLogSampleSeconds * 2.0);
            _lastSampleUtc = nowUtc;

            var idleSeconds = NativeWin.GetIdleSeconds();
            if (idleSeconds >= _config.WorkLogIdleMinutes * 60) return;

            var fg = NativeWin.GetForeground();
            // the Windows lock screen is not work, even before the idle threshold is reached
            if (fg is { App: "LockApp" }) return;
            if (fg is not null)
            {
                var sec = (int)Math.Round(elapsed);
                Append(new { t = Now(), k = "s", app = fg.App, title = fg.Title, sec });
                lock (Gate)
                    if (_todayDate == DateTime.Today) _todaySeconds += sec;
            }

            WriteOpenSnapshotIfChanged();
        }
        catch (Exception ex)
        {
            // sampling is best effort, but leave a trace so a silent failure can be diagnosed
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(Path.Combine(LogDir, "errors.log"), $"{Now()} {ex}\n");
            }
            catch { }
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private static void WriteOpenSnapshotIfChanged()
    {
        var windows = NativeWin.GetOpenWindows();

        // scanning for scripts spawns a helper process, so do it at most every 2 minutes
        if (DateTime.UtcNow - _lastScriptScan > TimeSpan.FromMinutes(2))
        {
            _lastScriptScan = DateTime.UtcNow;
            try { _lastScripts = ProcessScanner.Scan().Select(s => s.ScriptPath).OrderBy(p => p).ToList(); }
            catch { /* keep the previous list */ }
        }

        var hash = string.Join('|', windows.Select(w => w.App + ":" + w.Title).OrderBy(x => x)) + "#" + string.Join('|', _lastScripts);
        var due = DateTime.Now - _lastOpenWrite > TimeSpan.FromMinutes(10);
        if (hash == _lastOpenHash && !due) return;

        _lastOpenHash = hash;
        _lastOpenWrite = DateTime.Now;
        Append(new
        {
            t = Now(),
            k = "o",
            wins = windows.Select(w => new { app = w.App, title = w.Title }).ToList(),
            scripts = _lastScripts,
        });
    }

    // ---- analysis ----------------------------------------------------------

    private static DateTime _todayDate;
    private static double _todaySeconds;

    /// <summary>Active seconds recorded today. Reads today's file once per day, then counts new samples in memory.</summary>
    public static double TodayActiveSeconds()
    {
        var today = DateTime.Today;
        lock (Gate)
            if (_todayDate == today) return _todaySeconds;

        var total = Summarize(today, today).ActiveSeconds;
        lock (Gate)
        {
            _todayDate = today;
            _todaySeconds = total;
            return total;
        }
    }

    public sealed record Summary(
        DateTime From, DateTime To, double ActiveSeconds,
        List<(string App, double Seconds)> Apps,
        List<(string App, string Title, double Seconds)> Titles,
        List<(DateTime Day, double Seconds)> Days,
        List<(string Name, int Count)> Launches,
        List<(string Name, int Count)> TimerRuns,
        List<string> Notes,
        double[] Hours,
        double OutsideWorkSeconds,
        string? WorkHoursFilter);

    /// <summary>Every parsed log line between the two days (inclusive), with its timestamp. Malformed lines are skipped.</summary>
    private static IEnumerable<(DateTime Day, DateTime When, JsonElement Line)> ReadLines(DateTime fromDay, DateTime toDay)
    {
        for (var day = fromDay.Date; day <= toDay.Date; day = day.AddDays(1))
        {
            var file = Path.Combine(LogDir, day.ToString("yyyy-MM-dd") + ".jsonl");
            if (!File.Exists(file)) continue;

            string[] lines;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch { continue; }

            foreach (var line in lines)
            {
                JsonElement root;
                DateTime when;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                    when = DateTime.TryParse(root.GetProperty("t").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var w) ? w : day;
                }
                catch
                {
                    continue; // partially written line
                }
                yield return (day, when, root);
            }
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <param name="workHours">When given, active time outside these hours is left out of the totals
    /// (and reported separately as <see cref="Summary.OutsideWorkSeconds"/>).</param>
    public static Summary Summarize(DateTime fromDay, DateTime toDay, WorkHours? workHours = null)
    {
        var apps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var titles = new Dictionary<(string, string), double>();
        var days = new Dictionary<DateTime, double>();
        var launches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var timers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var hours = new double[24];
        double total = 0, outside = 0;

        foreach (var (day, when, root) in ReadLines(fromDay, toDay))
        {
            try
            {
                switch (Str(root, "k"))
                {
                    case "s":
                        var sec = root.GetProperty("sec").GetDouble();
                        if (workHours is not null && !workHours.Contains(when))
                        {
                            outside += sec;
                            break;
                        }
                        var app = Str(root, "app");
                        var title = Str(root, "title");
                        total += sec;
                        hours[when.Hour] += sec;
                        apps[app] = apps.GetValueOrDefault(app) + sec;
                        titles[(app, title)] = titles.GetValueOrDefault((app, title)) + sec;
                        days[day] = days.GetValueOrDefault(day) + sec;
                        break;
                    case "l":
                        var name = Str(root, "name");
                        launches[name] = launches.GetValueOrDefault(name) + 1;
                        break;
                    case "t":
                        var tname = Str(root, "name");
                        timers[tname] = timers.GetValueOrDefault(tname) + 1;
                        break;
                    case "n":
                        notes.Add($"{Str(root, "t")} {Str(root, "title")}");
                        break;
                }
            }
            catch
            {
                // a line with missing fields
            }
        }

        return new Summary(
            fromDay, toDay, total,
            apps.OrderByDescending(a => a.Value).Select(a => (a.Key, a.Value)).ToList(),
            titles.OrderByDescending(t => t.Value).Take(25).Select(t => (t.Key.Item1, t.Key.Item2, t.Value)).ToList(),
            days.OrderBy(d => d.Key).Select(d => (d.Key, d.Value)).ToList(),
            launches.OrderByDescending(l => l.Value).Select(l => (l.Key, l.Value)).ToList(),
            timers.OrderByDescending(l => l.Value).Select(l => (l.Key, l.Value)).ToList(),
            notes, hours, outside, workHours?.Describe());
    }

    /// <summary>A work-log match: time spent in a window, a launch, or a timer run.</summary>
    public sealed record LogHit(DateTime Day, DateTime First, DateTime Last, string Kind, string App, string Text, double Seconds, int Count);

    /// <summary>Finds window titles / apps, launches and timer runs whose text contains every term (case-insensitive).
    /// Window samples are merged per day + app + title.</summary>
    public static List<LogHit> Search(string query, DateTime fromDay, DateTime toDay, int max = 200)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return new();
        bool Matches(string text) => terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

        var hits = new Dictionary<(DateTime, string, string, string), LogHit>();
        foreach (var (day, when, root) in ReadLines(fromDay, toDay))
        {
            string kind, app, text;
            double sec = 0;
            switch (Str(root, "k"))
            {
                case "s":
                    kind = "window"; app = Str(root, "app"); text = Str(root, "title");
                    sec = root.TryGetProperty("sec", out var s) ? s.GetDouble() : 0;
                    if (!Matches(app + " " + text)) continue;
                    break;
                case "l":
                    kind = "launch"; app = "dashboard"; text = Str(root, "name");
                    if (!Matches(text + " " + Str(root, "path") + " " + Str(root, "args"))) continue;
                    break;
                case "t":
                    kind = "timer"; app = Str(root, "action"); text = Str(root, "name");
                    var output = Str(root, "output");
                    if (!Matches(text + " " + output)) continue;
                    if (output.Length > 0) text += " — " + (output.Length > 120 ? output[..120] + "…" : output).ReplaceLineEndings(" ");
                    break;
                default:
                    continue; // notes are searched in notes.json; "o" snapshots repeat the window titles
            }

            var key = (day, kind, app, kind == "timer" ? Str(root, "name") : text);
            hits[key] = hits.TryGetValue(key, out var h)
                ? h with { Last = when, Seconds = h.Seconds + sec, Count = h.Count + 1 }
                : new LogHit(day, when, when, kind, app, text, sec, 1);
        }

        return hits.Values
            .OrderByDescending(h => h.Day)
            .ThenByDescending(h => h.Seconds)
            .ThenByDescending(h => h.Last)
            .Take(max)
            .ToList();
    }

    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes:00}m" : $"{ts.Minutes}m {ts.Seconds:00}s";
    }

    public static string ToMarkdown(Summary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Work log {s.From:yyyy-MM-dd} → {s.To:yyyy-MM-dd}");
        sb.AppendLine($"Active time: {FormatDuration(s.ActiveSeconds)}");
        if (s.WorkHoursFilter is not null)
            sb.AppendLine($"Working hours only ({s.WorkHoursFilter}); {FormatDuration(s.OutsideWorkSeconds)} outside them not counted");
        sb.AppendLine();
        if (s.ActiveSeconds > 0)
        {
            sb.AppendLine("## By hour of day");
            for (var h = 0; h < 24; h++)
                if (s.Hours[h] > 0) sb.AppendLine($"- {h:00}:00–{h + 1:00}:00: {FormatDuration(s.Hours[h])}");
            sb.AppendLine();
        }
        sb.AppendLine("## Time per app");
        foreach (var (app, sec) in s.Apps) sb.AppendLine($"- {app}: {FormatDuration(sec)}");
        sb.AppendLine();
        sb.AppendLine("## Top windows / tabs");
        foreach (var (app, title, sec) in s.Titles) sb.AppendLine($"- [{app}] {title}: {FormatDuration(sec)}");
        sb.AppendLine();
        sb.AppendLine("## Per day");
        foreach (var (day, sec) in s.Days) sb.AppendLine($"- {day:yyyy-MM-dd}: {FormatDuration(sec)}");
        if (s.Launches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Scripts / apps launched from the dashboard");
            foreach (var (name, count) in s.Launches) sb.AppendLine($"- {name}: {count}×");
        }
        if (s.TimerRuns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Interval timers fired");
            foreach (var (name, count) in s.TimerRuns) sb.AppendLine($"- {name}: {count}×");
        }
        if (s.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes saved");
            foreach (var n in s.Notes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }

    // ---- Win32 -------------------------------------------------------------

    private static class NativeWin
    {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("kernel32.dll")] private static extern uint GetTickCount();

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_CLOAKED = 14;

        public static double GetIdleSeconds()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? (GetTickCount() - info.dwTime) / 1000.0 : 0;
        }

        public static OpenWindow? GetForeground()
        {
            var hwnd = GetForegroundWindow();
            return hwnd == IntPtr.Zero ? null : Describe(hwnd);
        }

        public static List<OpenWindow> GetOpenWindows()
        {
            var list = new List<OpenWindow>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var w = Describe(hwnd);
                if (w is not null) list.Add(w);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static OpenWindow? Describe(IntPtr hwnd)
        {
            var len = GetWindowTextLength(hwnd);
            if (len <= 0) return null;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            string app;
            try { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; }
            catch { app = "?"; }
            return new OpenWindow(app, title);
        }
    }
}
