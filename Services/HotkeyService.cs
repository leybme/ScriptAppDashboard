using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScriptAppDashboard.Services;

/// <summary>
/// System-wide hotkeys (Windows), e.g. "Ctrl+Alt+Space". Each one (a numbered slot) is registered on its own thread with a
/// message loop, so it works while every window is hidden in the tray. The callback runs on that thread.
/// </summary>
public static class HotkeyService
{
    private const int HotkeyIdBase = 0x5AD1;

    private sealed class Slot
    {
        public Thread? Thread;
        public uint ThreadId;
    }

    private static readonly Dictionary<int, Slot> Slots = new();

    /// <summary>Registers the hotkey for a slot (replacing that slot's previous one). Returns an error message, or null
    /// when it worked or there is nothing to register.</summary>
    /// <param name="settingsPlace">Where the user changes it, for the error message ("Settings → AI").</param>
    public static string? Start(int slot, string? spec, Action pressed, string settingsPlace = "Settings → AI")
    {
        Stop(slot);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(spec)) return null;
        if (!TryParse(spec, out var modifiers, out var key))
            return $"“{spec}” is not a hotkey this app understands — use e.g. Ctrl+Alt+Space or Ctrl+Shift+F9.";

        var state = new Slot();
        var id = HotkeyIdBase + slot;
        string? error = null;
        var ready = new ManualResetEventSlim(); // not disposed: the thread may set it after a timed-out wait
        state.Thread = new Thread(() =>
        {
            state.ThreadId = Native.GetCurrentThreadId();
            if (!Native.RegisterHotKey(IntPtr.Zero, id, modifiers | Native.MOD_NOREPEAT, key))
            {
                error = $"{spec} is already used by another program — choose a different hotkey in {settingsPlace}.";
                state.ThreadId = 0;
                ready.Set();
                return;
            }
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != Native.WM_HOTKEY) continue;
                try { pressed(); } catch { /* never let the hotkey thread die */ }
            }
            Native.UnregisterHotKey(IntPtr.Zero, id);
        })
        {
            IsBackground = true,
            Name = "Global hotkey " + slot,
        };
        lock (Slots) Slots[slot] = state;
        state.Thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return error;
    }

    public static string? Start(string? spec, Action pressed) => Start(0, spec, pressed);

    /// <summary>Unregisters one slot's hotkey.</summary>
    public static void Stop(int slot)
    {
        if (!OperatingSystem.IsWindows()) return;
        Slot? state;
        lock (Slots)
        {
            if (!Slots.Remove(slot, out state)) return;
        }
        if (state.ThreadId != 0) Native.PostThreadMessage(state.ThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        state.Thread?.Join(TimeSpan.FromSeconds(1));
    }

    /// <summary>Unregisters every hotkey.</summary>
    public static void Stop()
    {
        int[] slots;
        lock (Slots) slots = Slots.Keys.ToArray();
        foreach (var slot in slots) Stop(slot);
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
