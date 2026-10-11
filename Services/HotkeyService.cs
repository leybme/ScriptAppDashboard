using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScriptAppDashboard.Services;

/// <summary>
/// One system-wide hotkey (Windows), e.g. "Ctrl+Alt+Space". It is registered on its own thread with a message loop,
/// so it works while every window is hidden in the tray. The callback runs on that thread.
/// </summary>
public static class HotkeyService
{
    private const int HotkeyId = 0x5AD1;
    private static Thread? _thread;
    private static uint _threadId;

    /// <summary>Registers the hotkey (replacing the previous one). Returns an error message, or null when it worked
    /// or there is nothing to register.</summary>
    public static string? Start(string? spec, Action pressed)
    {
        Stop();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(spec)) return null;
        if (!TryParse(spec, out var modifiers, out var key))
            return $"“{spec}” is not a hotkey this app understands — use e.g. Ctrl+Alt+Space or Ctrl+Shift+F9.";

        string? error = null;
        var ready = new ManualResetEventSlim(); // not disposed: the thread may set it after a timed-out wait
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            if (!Native.RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers | Native.MOD_NOREPEAT, key))
            {
                error = $"{spec} is already used by another program — choose a different hotkey in Settings → AI.";
                _threadId = 0;
                ready.Set();
                return;
            }
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != Native.WM_HOTKEY) continue;
                try { pressed(); } catch { /* never let the hotkey thread die */ }
            }
            Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
        })
        {
            IsBackground = true,
            Name = "Global hotkey",
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return error;
    }

    public static void Stop()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        _threadId = 0;
    }

    /// <summary>"Ctrl+Alt+Space" → MOD_CONTROL | MOD_ALT and VK_SPACE. Keys: A–Z, 0–9, F1–F24, Space; at least one modifier.</summary>
    public static bool TryParse(string spec, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= Native.MOD_CONTROL; continue;
                case "ALT": modifiers |= Native.MOD_ALT; continue;
                case "SHIFT": modifiers |= Native.MOD_SHIFT; continue;
                case "WIN" or "WINDOWS": modifiers |= Native.MOD_WIN; continue;
            }
            if (key != 0) return false;
            if (part == "SPACE") key = 0x20;
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) key = part[0];
            else if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) key = (uint)(0x70 + f - 1);
            else return false;
        }
        return key != 0 && modifiers != 0;
    }

    private static class Native
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        public const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x, y;
            public uint lPrivate;
        }

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    }
}
