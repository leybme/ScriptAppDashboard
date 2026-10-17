using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Full-screen overlay over a frozen screenshot of the desktop: drag to choose a region, click the outlined window to take
/// that window, press F for everything, Esc to cancel. Built in code only. <see cref="PickAsync"/> returns the cropped pixels.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private readonly Pixels _shot;
    private readonly Surface _surface;
    private readonly TaskCompletionSource<Pixels?> _result = new();

    public CaptureOverlayWindow(Pixels shot)
    {
        _shot = shot;
        _surface = new Surface(shot);
        Content = _surface;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Cursor = new Cursor(StandardCursorType.Cross);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(shot.Left, shot.Top);
        Background = Brushes.Black;
        Opened += (_, _) =>
        {
            // the size is in device-independent units; the screenshot is in pixels
            var scale = Screens.ScreenFromPoint(new PixelPoint(shot.Left, shot.Top))?.Scaling ?? RenderScaling;
            Width = shot.Width / scale;
            Height = shot.Height / scale;
            Position = new PixelPoint(shot.Left, shot.Top);
            Activate();
            _surface.Focus();
        };
        Closed += (_, _) => _result.TrySetResult(null);
        _surface.Finished += pixels =>
        {
            _result.TrySetResult(pixels);
            Close();
        };
    }

    public Task<Pixels?> PickAsync()
    {
        Show();
        return _result.Task;
    }

    private sealed class Surface : Control
    {
        private readonly Pixels _shot;
        private readonly Bitmap _bitmap;
        private bool _dragging;
        private Point _start, _now;
        private Point _hover;

        public event Action<Pixels?>? Finished;

        public Surface(Pixels shot)
        {
            _shot = shot;
            _bitmap = shot.ToBitmap();
            Focusable = true;
        }

        private double Scale => Bounds.Width > 0 ? _shot.Width / Bounds.Width : 1;

        private PixelRect ToPixels(Rect r)
        {
            var s = Scale;
            return new PixelRect((int)Math.Round(r.X * s), (int)Math.Round(r.Y * s),
                Math.Max(1, (int)Math.Round(r.Width * s)), Math.Max(1, (int)Math.Round(r.Height * s)));
        }

        private Rect? WindowInView()
        {
            if (_shot.WindowRect is not { } w) return null;
            var s = Scale;
            return new Rect(w.X / s, w.Y / s, w.Width / s, w.Height / s);
        }

        public override void Render(DrawingContext ctx)
        {
            var full = new Rect(Bounds.Size);
            ctx.DrawImage(_bitmap, full);
            var shade = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            Rect? selection = _dragging ? new Rect(_start, _now).Normalize() : null;
            var hovered = !_dragging && WindowInView() is { } w && w.Contains(_hover) ? w : (Rect?)null;
            var target = selection ?? hovered;

            if (target is not { } t)
            {
                ctx.DrawRectangle(shade, null, full);
            }
            else
            {
                ctx.DrawRectangle(shade, null, new Rect(0, 0, full.Width, t.Top));
                ctx.DrawRectangle(shade, null, new Rect(0, t.Bottom, full.Width, full.Height - t.Bottom));
                ctx.DrawRectangle(shade, null, new Rect(0, t.Top, t.Left, t.Height));
                ctx.DrawRectangle(shade, null, new Rect(t.Right, t.Top, full.Width - t.Right, t.Height));
                ctx.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 2), t);
                if (selection is not null)
                {
                    var px = ToPixels(t);
                    Label(ctx, $"{px.Width} × {px.Height}", new Point(t.X, Math.Max(0, t.Y - 26)));
                }
                else
                {
                    Label(ctx, "Click to capture this window", new Point(t.X + 6, t.Y + 6));
                }
            }
            Label(ctx, "Drag to select a region  ·  F = whole screen  ·  Esc = cancel", new Point(full.Width / 2 - 190, 12));
        }

        private static void Label(DrawingContext ctx, string text, Point at)
        {
            var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, Brushes.White);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), null,
                new Rect(at.X - 6, at.Y - 3, f.Width + 12, f.Height + 6), 4, 4);
            ctx.DrawText(f, at);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                Finished?.Invoke(null);
                return;
            }
            _dragging = true;
            _start = _now = e.GetPosition(this);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            _hover = e.GetPosition(this);
            if (_dragging) _now = _hover;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            _now = e.GetPosition(this);
            var r = new Rect(_start, _now).Normalize();
            if (r.Width < 5 || r.Height < 5)
            {
                // a click: take the outlined window
                if (WindowInView() is { } w && w.Contains(_now)) Finish(_shot.WindowRect!.Value);
                else InvalidateVisual();
                return;
            }
            Finish(ToPixels(r));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.Key)
            {
                case Key.Escape:
                    Finished?.Invoke(null);
                    break;
                case Key.F or Key.Enter:
                    Finish(new PixelRect(0, 0, _shot.Width, _shot.Height));
                    break;
                case Key.W or Key.Space when _shot.WindowRect is { } w:
                    Finish(w);
                    break;
            }
        }

        private void Finish(PixelRect area) => Finished?.Invoke(_shot.Crop(area));
    }
}
