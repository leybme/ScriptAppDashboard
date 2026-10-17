using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ScriptAppDashboard.Services;

/// <summary>Raw pixels (B, G, R, A per pixel, rows top to bottom) with where they sit on the screen.</summary>
public sealed class Pixels
{
    public Pixels(byte[] bgra, int width, int height, int left = 0, int top = 0)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Position of the top-left corner on the virtual screen (physical pixels; can be negative).</summary>
    public int Left { get; }
    public int Top { get; }

    /// <summary>Rectangle (in this picture's pixels) of the window that was in front, or null.</summary>
    public PixelRect? WindowRect { get; set; }

    public Pixels Crop(PixelRect r)
    {
        r = r.Intersect(new PixelRect(0, 0, Width, Height));
        var data = new byte[r.Width * r.Height * 4];
        for (var y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Bgra, ((r.Y + y) * Width + r.X) * 4, data, y * r.Width * 4, r.Width * 4);
        return new Pixels(data, r.Width, r.Height, Left + r.X, Top + r.Y);
    }

    /// <summary>The colour of pixel (x, y) clamped to the picture, as B, G, R.</summary>
    public (int B, int G, int R) At(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var i = (y * Width + x) * 4;
        return (Bgra[i], Bgra[i + 1], Bgra[i + 2]);
    }

    public Bitmap ToBitmap()
    {
        var handle = GCHandle.Alloc(Bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(Width, Height), new Vector(96, 96), Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    public byte[] ToPng()
    {
        using var bitmap = ToBitmap();
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Decodes a PNG / BMP / JPEG into pixels.</summary>
    public static Pixels FromImage(Stream stream)
    {
        using var bitmap = WriteableBitmap.Decode(stream);
        using var fb = bitmap.Lock();
        var w = fb.Size.Width;
        var h = fb.Size.Height;
        var data = new byte[w * h * 4];
        var swap = fb.Format == PixelFormat.Rgba8888;
        var row = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, w * 4);
            if (swap)
                for (var x = 0; x < w * 4; x += 4) (row[x], row[x + 2]) = (row[x + 2], row[x]);
            Buffer.BlockCopy(row, 0, data, y * w * 4, w * 4);
        }
        return new Pixels(data, w, h);
    }
}

/// <summary>Takes the screenshot: the whole virtual screen through GDI on Windows (no packages), "screencapture" on macOS.</summary>
public static class ScreenCaptureService
{
    /// <summary>Windows: the whole desktop (all monitors) as pixels, plus the rectangle of the window that was in front.
    /// Null on other systems or when the screen cannot be read (e.g. a locked session).</summary>
    public static Pixels? GrabScreen()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Native.Grab();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>macOS: the user picks a region with the system tool; returns the PNG, or null when cancelled.</summary>
    public static Pixels? GrabInteractiveMac()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var file = Path.Combine(Path.GetTempPath(), $"sad-shot-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo { FileName = "screencapture", UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add(file);
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            if (!File.Exists(file)) return null;
            using var stream = File.OpenRead(file);
            return Pixels.FromImage(stream);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp folder */ }
        }
    }

    private static class Native
    {
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        public static Pixels? Grab()
        {
            var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width <= 0 || height <= 0) return null;

            // the window in front, before our own overlay takes the focus
            PixelRect? windowRect = null;
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsIconic(fg))
            {
                if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
                    GetWindowRect(fg, out r);
                if (r.Right > r.Left && r.Bottom > r.Top)
                    windowRect = new PixelRect(r.Left - left, r.Top - top, r.Right - r.Left, r.Bottom - r.Top)
                        .Intersect(new PixelRect(0, 0, width, height));
            }

            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            var mem = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // negative: top-down rows
                biPlanes = 1, biBitCount = 32,
            };
            var dib = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            try
            {
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
                var old = SelectObject(mem, dib);
                var ok = BitBlt(mem, 0, 0, width, height, screen, left, top, SRCCOPY | CAPTUREBLT);
                SelectObject(mem, old);
                if (!ok) return null;
                var data = new byte[width * height * 4];
                Marshal.Copy(bits, data, 0, data.Length);
                for (var i = 3; i < data.Length; i += 4) data[i] = 255; // GDI leaves alpha at 0
                return new Pixels(data, width, height, left, top) { WindowRect = windowRect };
            }
            finally
            {
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
