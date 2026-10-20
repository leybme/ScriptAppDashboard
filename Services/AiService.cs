using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

public sealed record AiResult(bool Ok, string Text, TimeSpan Elapsed);

/// <summary>
/// Sends prompts to the AI command-line tool from Settings (default "claude -p"): the prompt goes to its standard
/// input and its text output is the answer. Never throws; failures come back as <see cref="AiResult.Ok"/> = false.
/// </summary>
public static class AiService
{
    private static AppConfig? _config;

    public static void Start(AppConfig config) => _config = config;

    /// <summary>Models offered for the Claude provider (ID passed to --model, label shown in Settings).</summary>
    public static readonly (string Id, string Label)[] ClaudeModels =
    {
        ("claude-haiku-5-5", "Haiku 5.5 — fastest, cheapest"),
        ("claude-sonnet-5-5", "Sonnet 5.5 — balanced"),
        ("claude-opus-5-5", "Opus 5.5 — most capable"),
        ("claude-fable-5-1", "Fable 5.1"),
    };

    public const string DefaultModel = "claude-haiku-5-5";

    public static string Provider => _config?.AiProvider is "custom" or "kiro" ? _config.AiProvider : "claude";

    /// <summary>The command line every AI feature runs.</summary>
    public static string Command => CommandFor(Provider, _config?.AiModel, _config?.AiCommand);

    /// <summary>Flags for the built-in Claude command. The prompts are plain text, so the model needs no tools and none of
    /// the user's MCP servers; leaving them out of the request cuts a call from about 7 s to 3 s. (--bare is faster
    /// still, but it ignores the saved login.)</summary>
    private const string FastFlags = " --strict-mcp-config --no-session-persistence --tools \"\"";

    public static string CommandFor(string provider, string? model, string? custom) => provider switch
    {
        "custom" => custom?.Trim() is { Length: > 0 } c ? c : $"claude -p --model {DefaultModel}{FastFlags}",
        _ => $"claude -p --model {(string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim())}{FastFlags}",
    };

    /// <summary>"Claude Haiku 5.5" or the custom command, for status lines.</summary>
    public static string DisplayName => Provider == "custom"
        ? Command
        : "Claude " + (ClaudeModels.FirstOrDefault(m => m.Id == (_config?.AiModel ?? DefaultModel)).Label?.Split(" —")[0] ?? _config?.AiModel);

    /// <summary>The status line every AI feature shows while it waits: "✨ Asking Claude Haiku 5.5…" or "… to set up the timer…".</summary>
    public static string Asking(string? task = null) =>
        task is null ? $"✨ Asking {DisplayName}…" : $"✨ Asking {DisplayName} to {task}…";

    /// <summary>The message every AI feature shows when a request failed.</summary>
    public static string Failed(AiResult result) => "AI failed: " + result.Text;

    /// <summary>
    /// A note made from an AI answer (Markdown). The outer code fence some models wrap around the answer is removed,
    /// and <paramref name="intro"/> (e.g. the question or the script path) goes in front. Save it with <c>App.AddNote</c>.
    /// </summary>
    public static NoteItem AnswerToNote(string title, string tags, string answer, string? intro = null) => new()
    {
        Title = title,
        Tags = tags,
        Html = NoteDocument.ToHtml(NoteDocument.FromMarkdown(
            (intro is { Length: > 0 } ? intro + "\n\n" : "") + StripOuterFence(answer))),
    };

    /// <summary>The answer language chosen in Settings; empty when the AI should follow the content.</summary>
    public static string Language => _config?.AiLanguage?.Trim() ?? "";

    /// <summary>The language instruction added to prompts whose answer is shown to the user.</summary>
    public static string LanguageRule => _config?.AiLanguage?.Trim() is { Length: > 0 } lang
        ? $"Write your answer in {lang}."
        : "Write your answer in the same language as the content you are given.";

    /// <summary>Started in an empty folder so the CLI doesn't pick up a project (CLAUDE.md, files) from the app's folder.</summary>
    private static string WorkDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "Toolshed360-ai");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>How to start a command line through the platform shell, with redirected pipes, in the empty work folder.</summary>
    private static ProcessStartInfo StartInfoFor(string commandLine)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // without this, Vietnamese / CJK prompts reach the CLI in the console code page and come out garbled
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = WorkDir,
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
        return psi;
    }

    // ---- streaming with a warm standby ------------------------------------------------

    /// <summary>The built-in command switched to streaming JSON: one user message in, text deltas out as they are written.</summary>
    private const string StreamFlags = " --input-format stream-json --output-format stream-json --verbose --include-partial-messages";

    /// <summary>An idle standby is thrown away after this long, so nothing keeps running while the app isn't used for AI.</summary>
    private static readonly TimeSpan StandbyLife = TimeSpan.FromMinutes(4);

    /// <summary>A CLI process that has already started and waits for its one question on stdin (starting it is most of the delay).</summary>
    private sealed class Standby
    {
        public Process Proc = null!;
        public string CommandLine = "";
        public Task<string> Stderr = Task.FromResult("");
        public Timer? Expiry;
    }

    private static Standby? _standby;
    private static readonly object StandbyGate = new();

    private static Standby? StartStandby(string commandLine)
    {
        try
        {
            var proc = Process.Start(StartInfoFor(commandLine));
            return proc is null ? null : new Standby { Proc = proc, CommandLine = commandLine, Stderr = proc.StandardError.ReadToEndAsync() };
        }
        catch
        {
            return null;
        }
    }

    private static void Discard(Standby s)
    {
        lock (StandbyGate)
            if (ReferenceEquals(_standby, s)) _standby = null;
        s.Expiry?.Dispose();
        try { s.Proc.Kill(entireProcessTree: true); } catch { }
        try { s.Proc.Dispose(); } catch { }
    }

    /// <summary>
    /// Starts the AI command in the background so the next question doesn't wait for it to boot (about 2 s saved). Call it
    /// when the user opens a chat or starts typing; it does nothing if a standby is ready, and the standby expires by itself.
    /// </summary>
    public static void Warm()
    {
        if (Provider != "claude") return;
        var line = Command + StreamFlags;
        Task.Run(() =>
        {
            lock (StandbyGate)
            {
                if (_standby is { } s && s.CommandLine == line && !s.Proc.HasExited) return;
                if (_standby is { } old) Discard(old);
                var fresh = StartStandby(line);
                if (fresh is null) return;
                _standby = fresh;
                fresh.Expiry = new Timer(_ => Discard(fresh), null, StandbyLife, Timeout.InfiniteTimeSpan);
            }
        });
    }

    private static Standby? Take(string commandLine)
    {
        lock (StandbyGate)
        {
            if (_standby is { } s && s.CommandLine == commandLine && !s.Proc.HasExited)
            {
                _standby = null;
                s.Expiry?.Dispose();
                return s;
            }
        }
        return StartStandby(commandLine);
    }

    /// <summary>
    /// Like <see cref="AskAsync"/>, but <paramref name="onText"/> gets the answer so far each time more of it arrives (from a worker thread).
    /// Streams only with the built-in Claude command; a custom command runs as before and delivers the whole answer at the end.
    /// </summary>
    public static async Task<AiResult> AskStreamAsync(string prompt, Action<string>? onText, CancellationToken cancel = default,
        string? command = null, TimeSpan? timeout = null)
    {
        if (!string.IsNullOrWhiteSpace(command) || Provider != "claude")
        {
            var whole = await AskAsync(prompt, cancel, command, timeout);
            if (whole.Ok) onText?.Invoke(whole.Text);
            return whole;
        }

        var watch = Stopwatch.StartNew();
        var commandLine = Command + StreamFlags;
        var standby = Take(commandLine);
        if (standby is null) return new AiResult(false, $"Could not start “{Command}”.", watch.Elapsed);

        using var proc = standby.Proc;
        try
        {
            var message = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = prompt } });
            await proc.StandardInput.WriteLineAsync(message);
            await proc.StandardInput.FlushAsync();
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // the command exited without reading its input; its error output says why
        }

        var max = timeout ?? TimeSpan.FromMinutes(5);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(max);
        using var killer = limit.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

        var text = new StringBuilder();
        string? final = null;
        var failed = false;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(limit.Token)) is not null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = Str(root, "type");
                    if (type == "stream_event" && root.TryGetProperty("event", out var ev) && Str(ev, "type") == "content_block_delta"
                        && ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta")
                    {
                        text.Append(Str(delta, "text"));
                        onText?.Invoke(text.ToString());
                    }
                    else if (type == "result")
                    {
                        final = Str(root, "result");
                        failed = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                    }
                }
                catch (JsonException)
                {
                    // not an event line
                }
            }
        }
        catch (OperationCanceledException)
        {
            // handled below
        }
        if (limit.IsCancellationRequested)
            return new AiResult(false, cancel.IsCancellationRequested ? "Stopped." : $"No answer after {max.TotalMinutes:0} min — gave up.", watch.Elapsed);

        try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        var error = (await standby.Stderr.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "")).Trim();
        var answer = (string.IsNullOrEmpty(final) ? text.ToString() : final).Trim();
        var exitCode = proc.HasExited ? proc.ExitCode : 0;
        if (exitCode == 9009 || exitCode == 127 || error.Contains("is not recognized as an internal or external command"))
            return new AiResult(false,
                $"“{Command}” was not found. Install Claude Code (npm install -g @anthropic-ai/claude-code), " +
                "sign in once by running “claude” in a terminal, or set another AI command in Settings → AI.", watch.Elapsed);
        if (failed || (exitCode != 0 && answer.Length == 0))
            return new AiResult(false, (answer.Length > 0 ? answer : error.Length > 0 ? error : $"(exit code {exitCode})"), watch.Elapsed);
        if (answer.Length == 0)
            return new AiResult(false, error.Length > 0 ? error : "The AI command printed nothing.", watch.Elapsed);
        return new AiResult(true, answer, watch.Elapsed);
    }

    /// <summary>Runs the AI command with the prompt on stdin. <paramref name="command"/> overrides the one in Settings.</summary>
    public static async Task<AiResult> AskAsync(string prompt, CancellationToken cancel = default,
        string? command = null, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        if (Provider == "kiro" && string.IsNullOrWhiteSpace(command))
            return new AiResult(false, "Kiro isn't supported yet — choose Claude in Settings → AI.", watch.Elapsed);
        var commandLine = string.IsNullOrWhiteSpace(command) ? Command : command.Trim();

        Process? proc;
        try
        {
            proc = Process.Start(StartInfoFor(commandLine));
        }
        catch (Exception ex)
        {
            return new AiResult(false, $"Could not start “{commandLine}”: {ex.Message}", watch.Elapsed);
        }
        if (proc is null) return new AiResult(false, $"Could not start “{commandLine}”.", watch.Elapsed);

        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            try
            {
                await proc.StandardInput.WriteAsync(prompt);
                proc.StandardInput.Close();
            }
            catch (IOException)
            {
                // the command exited without reading its input; its error output says why
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var max = timeout ?? TimeSpan.FromMinutes(5);
            limit.CancelAfter(max);
            try
            {
                await proc.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new AiResult(false, cancel.IsCancellationRequested ? "Stopped." : $"No answer after {max.TotalMinutes:0} min — gave up.", watch.Elapsed);
            }

            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (proc.ExitCode == 9009 || error.Contains("is not recognized as an internal or external command") || proc.ExitCode == 127)
                return new AiResult(false,
                    $"“{commandLine}” was not found. Install Claude Code (npm install -g @anthropic-ai/claude-code), " +
                    "sign in once by running “claude” in a terminal, or set another AI command in Settings → AI.", watch.Elapsed);
            if (proc.ExitCode != 0)
                return new AiResult(false, $"{(output + "\n" + error).Trim()}\n(exit code {proc.ExitCode})".Trim(), watch.Elapsed);
            if (output.Length == 0)
                return new AiResult(false, error.Length > 0 ? error : "The AI command printed nothing.", watch.Elapsed);
            return new AiResult(true, output, watch.Elapsed);
        }
    }

    // ---- placeholders ---------------------------------------------------------------

    public static readonly (string Name, string Description)[] Placeholders =
    {
        ("{today_summary}", "today's work log (apps, windows, timeline)"),
        ("{week_summary}", "the work log of the last 7 days"),
        ("{current_app}", "the window in front"),
        ("{clipboard}", "the text on the clipboard"),
        ("{today_notes}", "notes written or edited today"),
        ("{date}", "today's date"),
        ("{time}", "the time now"),
    };

    private static readonly Regex PlaceholderPattern =
        new(@"\{(today_summary|week_summary|current_app|clipboard|today_notes|date|time)\}", RegexOptions.Compiled);

    /// <summary>Fills in the {placeholders}. Reads files and the clipboard, so call it off the UI thread.</summary>
    public static string ExpandPlaceholders(string prompt)
    {
        if (!prompt.Contains('{')) return prompt;
        return PlaceholderPattern.Replace(prompt, m =>
        {
            try
            {
                return m.Groups[1].Value switch
                {
                    "today_summary" => WorkLogContext(DateTime.Today, DateTime.Today),
                    "week_summary" => WorkLogContext(DateTime.Today.AddDays(-6), DateTime.Today),
                    "current_app" => WorkLogService.Foreground() is { } w ? $"{w.App} — {w.Title}" : "(unknown)",
                    "clipboard" => ReadClipboard(),
                    "today_notes" => TodayNotes(),
                    "date" => DateTime.Now.ToString("dddd yyyy-MM-dd"),
                    _ => DateTime.Now.ToString("HH:mm"),
                };
            }
            catch (Exception ex)
            {
                return $"({m.Value} unavailable: {ex.Message})";
            }
        });
    }

    private static string WorkLogContext(DateTime from, DateTime to)
    {
        var summary = WorkLogService.Summarize(from, to);
        return summary.ActiveSeconds == 0 && summary.Launches.Count == 0
            ? "(nothing recorded in the work log)"
            : WorkLogService.ToAiContext(summary, null);
    }

    private static string TodayNotes()
    {
        var notes = NotesService.Load().Where(n => n.Updated.Date == DateTime.Today || n.Created.Date == DateTime.Today).ToList();
        if (notes.Count == 0) return "(no notes today)";
        var text = NotesService.ToMarkdown(notes);
        return text.Length > 20000 ? text[..20000] + "…" : text;
    }

    /// <summary>Clipboard text through the platform tool, so it also works from a timer when no window is open.</summary>
    private static string ReadClipboard()
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "powershell";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-Clipboard -Raw");
        }
        else if (OperatingSystem.IsMacOS())
        {
            psi.FileName = "pbpaste";
        }
        else
        {
            return "(clipboard unavailable)";
        }
        using var proc = Process.Start(psi);
        if (proc is null) return "(clipboard unavailable)";
        var text = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(10000))
        {
            try { proc.Kill(); } catch { }
            return "(clipboard unavailable)";
        }
        var value = text.Result.Trim();
        return value.Length == 0 ? "(the clipboard is empty)" : value.Length > 20000 ? value[..20000] + "…" : value;
    }

    // ---- answers --------------------------------------------------------------------

    /// <summary>The first {...} object in an answer (AIs sometimes wrap JSON in ``` fences or add a sentence).</summary>
    public static JsonElement? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text inside the first ``` fence, or the whole answer when there is none.</summary>
    public static string ExtractCode(string text)
    {
        var m = Regex.Match(text, @"```[^\n]*\n(.*?)\n?```", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : text.Trim();
    }

    /// <summary>Removes a ``` fence the AI put around an answer that should be plain text or Markdown.</summary>
    public static string StripOuterFence(string text)
    {
        var t = text.Trim();
        var m = Regex.Match(t, @"^```(?:markdown|md|text)?\s*\n(.*)\n```$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : t;
    }

    public static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
