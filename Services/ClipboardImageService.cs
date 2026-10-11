using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Reads a picture from the clipboard (a screenshot, an image copied in a browser). Avalonia 11's clipboard only
/// offers text and files, so this goes to the platform: the Win32 clipboard on Windows, osascript on macOS.
/// </summary>
public static class ClipboardImageService
{
    /// <summary>The picture on the clipboard as PNG (or BMP) bytes with its file extension, or null.</summary>
    public static (byte[] Data, string Extension)? TryGetImage()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Native.GetImage();
            if (OperatingSystem.IsMacOS()) return GetMacPng() is { } png ? (png, ".png") : null;
        }
        catch
        {
            // clipboard busy or an unusual format: nothing to paste
        }
        return null;
    }

    private static byte[]? GetMacPng()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sad-clip-{Guid.NewGuid():N}.png");
        var psi = new ProcessStartInfo { FileName = "osascript", UseShellExecute = false, CreateNoWindow = true };
        foreach (var line in new[]
                 {
                     "try",
                     "set d to (the clipboard as «class PNGf»)",
                     $"set f to open for access POSIX file \"{file}\" with write permission",
                     "write d to f",
                     "close access f",
                     "end try",
                 })
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(line);
        }
        using var proc = Process.Start(psi);
        if (proc is null || !proc.WaitForExit(5000)) return null;
        if (!File.Exists(file)) return null;
        var data = File.ReadAllBytes(file);
        File.Delete(file);
        return data.Length > 0 ? data : null;
    }

    private static class Native
    {
        private const uint CF_DIB = 8, CF_DIBV5 = 17;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr mem);

        public static (byte[], string)? GetImage()
        {
            var png = RegisterClipboardFormat("PNG");
            var hasPng = IsClipboardFormatAvailable(png);
            var dibFormat = IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : IsClipboardFormatAvailable(CF_DIB) ? CF_DIB : 0;
            if (!hasPng && dibFormat == 0) return null;

            // another app may hold the clipboard for a moment
            var opened = false;
            for (var i = 0; i < 10 && !(opened = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(30);
            if (!opened) return null;
            try
            {
                if (hasPng && Read(png) is { Length: > 0 } pngBytes) return (pngBytes, ".png");
                return dibFormat != 0 && Read(dibFormat) is { Length: > 40 } dib ? (DibToBmp(dib), ".bmp") : null;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static byte[]? Read(uint format)
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[(int)GlobalSize(handle).ToUInt64()];
                Marshal.Copy(ptr, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        /// <summary>A clipboard DIB is a .bmp file without its 14-byte file header; add it.</summary>
        private static byte[] DibToBmp(byte[] dib)
        {
            var headerSize = BitConverter.ToInt32(dib, 0);
            var bitCount = BitConverter.ToInt16(dib, 14);
            var compression = BitConverter.ToInt32(dib, 16);
            var colorsUsed = BitConverter.ToInt32(dib, 32);
            var palette = bitCount <= 8 ? (colorsUsed != 0 ? colorsUsed : 1 << bitCount) * 4 : 0;
            var masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS colour masks follow a v1 header
            var offset = 14 + headerSize + palette + masks;

            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(offset).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            return bmp;
        }
    }
}
