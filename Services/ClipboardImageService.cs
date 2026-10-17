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

    /// <summary>Puts a PNG picture on the clipboard (as "PNG" and as a bitmap, so any app can paste it). Returns false on failure.</summary>
    public static bool TrySetImage(byte[] png)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Native.SetImage(png);
            if (OperatingSystem.IsMacOS()) return SetMacPng(png);
        }
        catch
        {
            // clipboard busy: the caller says so
        }
        return false;
    }

    private static bool SetMacPng(byte[] png)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sad-clip-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(file, png);
            var psi = new ProcessStartInfo { FileName = "osascript", UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"set the clipboard to (read (POSIX file \"{file}\") as «class PNGf»)");
            using var proc = Process.Start(psi);
            return proc is not null && proc.WaitForExit(5000) && proc.ExitCode == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp folder */ }
        }
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

        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

        private const uint GMEM_MOVEABLE = 0x2;

        public static bool SetImage(byte[] png)
        {
            var dib = PngToDib(png);
            var pngFormat = RegisterClipboardFormat("PNG");
            var opened = false;
            for (var i = 0; i < 10 && !(opened = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(30);
            if (!opened) return false;
            try
            {
                EmptyClipboard();
                var ok = Put(pngFormat, png);
                if (dib is not null) ok |= Put(CF_DIB, dib);
                return ok;
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>Copies the bytes into global memory and hands it to the clipboard (which then owns it).</summary>
        private static bool Put(uint format, byte[] data)
        {
            var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (mem == IntPtr.Zero) return false;
            var ptr = GlobalLock(mem);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(mem);
                return false;
            }
            Marshal.Copy(data, 0, ptr, data.Length);
            GlobalUnlock(mem);
            if (SetClipboardData(format, mem) != IntPtr.Zero) return true;
            GlobalFree(mem);
            return false;
        }

        /// <summary>A 32-bit bottom-up DIB (BITMAPINFOHEADER + pixels) for apps that only understand bitmaps.</summary>
        private static byte[]? PngToDib(byte[] png)
        {
            try
            {
                using var ms = new MemoryStream(png);
                var px = Pixels.FromImage(ms);
                var stride = px.Width * 4;
                var dib = new byte[40 + stride * px.Height];
                BitConverter.GetBytes(40).CopyTo(dib, 0);
                BitConverter.GetBytes(px.Width).CopyTo(dib, 4);
                BitConverter.GetBytes(px.Height).CopyTo(dib, 8);
                BitConverter.GetBytes((short)1).CopyTo(dib, 12);
                BitConverter.GetBytes((short)32).CopyTo(dib, 14);
                BitConverter.GetBytes(stride * px.Height).CopyTo(dib, 20);
                for (var y = 0; y < px.Height; y++)
                    Buffer.BlockCopy(px.Bgra, y * stride, dib, 40 + (px.Height - 1 - y) * stride, stride);
                return dib;
            }
            catch
            {
                return null;
            }
        }

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
