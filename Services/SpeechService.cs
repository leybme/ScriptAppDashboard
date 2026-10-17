using System;
using System.Diagnostics;
using System.Text;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Reads text aloud with the system voice: System.Speech through Windows PowerShell, <c>say</c> on macOS (no package
/// needed). Text in the Latin alphabet is read with an English voice, so it suits learning English; anything else
/// uses the system's default voice. Never throws.
/// </summary>
public static class SpeechService
{
    private const int MaxChars = 5000;
    private static readonly object Gate = new();
    private static Process? _current;

    /// <summary>True while a text is being read.</summary>
    public static bool IsSpeaking
    {
        get
        {
            lock (Gate)
            {
                try { return _current is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    /// <summary>Starts reading <paramref name="text"/>, replacing anything already being read. Returns false when the
    /// system has no speech support.</summary>
    public static bool Speak(string text, bool slow = false)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        if (text.Length > MaxChars) text = text[..MaxChars];
        Stop();
        try
        {
            var english = IsEnglishLike(text);
            var psi = OperatingSystem.IsWindows() ? WindowsStart(text, english, slow)
                : OperatingSystem.IsMacOS() ? MacStart(english, slow)
                : null;
            if (psi is null) return false;
            var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!OperatingSystem.IsWindows())
            {
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
            }
            lock (Gate) _current = proc;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stops the text being read, if any.</summary>
    /// <summary>Speaks Markdown text; the marks (*, `, #, |, list dashes) would be read as symbols, so they are dropped.</summary>
    public static bool SpeakMarkdown(string markdown) => Speak(System.Text.RegularExpressions.Regex.Replace(
        markdown, @"[*`#|]|^\s*-\s+|^\s*-{3,}\s*$", " ", System.Text.RegularExpressions.RegexOptions.Multiline));

    public static void Stop()
    {
        Process? proc;
        lock (Gate)
        {
            proc = _current;
            _current = null;
        }
        if (proc is null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // already finished
        }
        proc.Dispose();
    }

    /// <summary>False when the text has letters outside the Latin alphabet or Vietnamese-style accents.</summary>
    internal static bool IsEnglishLike(string text)
    {
        foreach (var c in text)
        {
            if (c < 0x80 || !char.IsLetter(c)) continue;
            if (c > 0x24F) return false; // Greek, Cyrillic, CJK, Vietnamese stacked accents (U+1EA0…), …
            if (c is >= 'À' and <= 'ÿ' && c != '×' && c != '÷') return false; // accented letters: a language other than English
        }
        return true;
    }

    private static ProcessStartInfo WindowsStart(string text, bool english, bool slow)
    {
        // the text travels base64-encoded in an environment variable: no quoting or code-page problems
        var script = $$"""
            Add-Type -AssemblyName System.Speech
            $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
            {{(english ? "try { $s.SelectVoiceByHints('NotSet', 'NotSet', 0, [Globalization.CultureInfo]::GetCultureInfo('en-US')) } catch { }" : "")}}
            $s.Rate = {{(slow ? -3 : 0)}}
            $s.Speak([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:SAD_SPEAK_TEXT)))
            """;
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);
        psi.Environment["SAD_SPEAK_TEXT"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return psi;
    }

    private static ProcessStartInfo MacStart(bool english, bool slow)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "say",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (english)
        {
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("Samantha");
        }
        if (slow)
        {
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("120");
        }
        return psi;
    }
}
