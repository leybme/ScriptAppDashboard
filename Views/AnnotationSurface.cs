using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// The picture with its annotations: draws them (via <see cref="AnnotationRenderer"/>) and lets the user create, move,
/// resize and select them with the mouse. Editing the text, colours and undo are done by the window around it.
/// </summary>
public sealed class AnnotationSurface : Control
{
    public const string SelectTool = "select", CropTool = "crop", TextTool = "ocr";

    private Pixels? _source;
    private Bitmap? _bitmap;
    private double _zoom = 1;
    private enum Drag { None, Create, Move, Handle, Crop, Grab }
    private Drag _drag;
    private int _handle;
    private Point _last;
    private Point _start;
    private Rect _crop;

    public List<Annotation> Items { get; set; } = new();

    /// <summary>The text lines found in the picture (OCR); the "Grab text" tool selects among them.</summary>
    public List<OcrLine> OcrLines { get; set; } = new();

    private readonly HashSet<int> _grabbed = new();
    public Annotation? Selected { get; private set; }

    /// <summary>select | crop | an <see cref="Annotation"/> kind.</summary>
    public string Tool { get; set; } = SelectTool;

    public string DrawColor { get; set; } = "#E53935";
    public double StrokeWidth { get; set; } = 4;
    public double FontSize { get; set; } = 28;

    /// <summary>Raised before the first change of a gesture, so the window can remember the state for undo.</summary>
    public event Action? BeforeChange;

    /// <summary>Raised after the annotations changed.</summary>
    public event Action? Changed;

    public event Action? SelectionChanged;

    /// <summary>Raised when a drawing tool finished one shape (the window switches back to Select).</summary>
    public event Action? ToolDone;

    /// <summary>Raised with the area to keep when the crop tool finished.</summary>
    public event Action<PixelRect>? CropRequested;

    /// <summary>The text of the OCR lines the user selected with the "Grab text" tool, and how many lines it has.</summary>
    public event Action<string, int>? TextGrabbed;

    /// <summary>A text-like annotation was created or double-clicked: the window focuses its text box.</summary>
    public event Action<Annotation>? TextRequested;

    public Pixels? Source => _source;

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.05, 8);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public AnnotationSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public void SetImage(Pixels pixels)
    {
        _source = pixels;
        _bitmap?.Dispose();
        _bitmap = pixels.ToBitmap();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Select(Annotation? a)
    {
        if (ReferenceEquals(Selected, a)) return;
        Selected = a;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Redraws after the window changed an annotation's colour, text or size.</summary>
    public void Refresh() => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
        => _source is null ? new Size(0, 0) : new Size(_source.Width * _zoom, _source.Height * _zoom);

    public override void Render(DrawingContext ctx)
    {
        if (_source is null || _bitmap is null) return;
        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom)))
        {
            ctx.DrawImage(_bitmap, new Rect(0, 0, _source.Width, _source.Height));
            foreach (var a in Items) AnnotationRenderer.Draw(ctx, a, _source);
            if (Selected is not null && _drag != Drag.Crop) DrawSelection(ctx, Selected);
            if (_drag == Drag.Crop || (Tool == CropTool && _crop.Width > 0)) DrawCrop(ctx);
            if (Tool == TextTool) DrawOcr(ctx);
        }
    }

    private void DrawSelection(DrawingContext ctx, Annotation a)
    {
        var accent = new Pen(Brushes.DodgerBlue, 1.5 / _zoom, new DashStyle(new[] { 4.0, 3.0 }, 0));
        if (a.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
            ctx.DrawLine(accent, new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
        else
            ctx.DrawRectangle(null, accent, AnnotationRenderer.Bounds(a).Inflate(3 / _zoom));
        var size = 9 / _zoom;
        foreach (var p in Handles(a))
            ctx.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, 1.5 / _zoom), new Rect(p.X - size / 2, p.Y - size / 2, size, size));
    }

    /// <summary>Outlines every line of text that can be grabbed; the selected ones are filled.</summary>
    private void DrawOcr(DrawingContext ctx)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(150, 30, 136, 229)), 1 / _zoom);
        var fill = new SolidColorBrush(Color.FromArgb(110, 30, 136, 229));
        for (var i = 0; i < OcrLines.Count; i++)
        {
            var l = OcrLines[i];
            if (CaptureService.IsHidden(l, Items)) continue;
            var r = new Rect(l.X - 2, l.Y - 2, l.W + 4, l.H + 4);
            ctx.DrawRectangle(_grabbed.Contains(i) ? fill : null, outline, r);
        }
        if (_drag == Drag.Grab)
            ctx.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1.5 / _zoom, new DashStyle(new[] { 4.0, 3.0 }, 0)), _crop);
    }

    /// <summary>Selects the lines touching <paramref name="area"/>; returns their text, top to bottom.</summary>
    private string GrabLines(Rect area)
    {
        _grabbed.Clear();
        for (var i = 0; i < OcrLines.Count; i++)
        {
            var l = OcrLines[i];
            if (CaptureService.IsHidden(l, Items)) continue; // covered by Redact / Blur
            if (new Rect(l.X, l.Y, l.W, l.H).Intersects(area)) _grabbed.Add(i);
        }
        return string.Join("\n", _grabbed.OrderBy(i => i).Select(i => OcrLines[i].Text));
    }

    public void ClearGrab()
    {
        if (_grabbed.Count == 0) return;
        _grabbed.Clear();
        InvalidateVisual();
    }

    private void DrawCrop(DrawingContext ctx)
    {
        if (_source is null) return;
        var shade = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        var w = _source.Width;
        var h = _source.Height;
        ctx.DrawRectangle(shade, null, new Rect(0, 0, w, _crop.Top));
        ctx.DrawRectangle(shade, null, new Rect(0, _crop.Bottom, w, h - _crop.Bottom));
        ctx.DrawRectangle(shade, null, new Rect(0, _crop.Top, _crop.Left, _crop.Height));
        ctx.DrawRectangle(shade, null, new Rect(_crop.Right, _crop.Top, w - _crop.Right, _crop.Height));
        ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5 / _zoom, new DashStyle(new[] { 4.0, 3.0 }, 0)), _crop);
    }

    // ---- handles and hit testing ------------------------------------------------------------

    private static bool IsRect(Annotation a)
        => a.Kind is AnnotationKind.Box or AnnotationKind.Ellipse or AnnotationKind.Highlight or AnnotationKind.Redact or AnnotationKind.Blur;

    /// <summary>Points the user can drag to reshape the annotation.</summary>
    private static List<Point> Handles(Annotation a)
    {
        var b = AnnotationRenderer.Bounds(a);
        return a.Kind switch
        {
            AnnotationKind.Line or AnnotationKind.Arrow => new() { new Point(a.X1, a.Y1), new Point(a.X2, a.Y2) },
            AnnotationKind.Callout => new() { AnnotationRenderer.Anchor(a) },
            AnnotationKind.Text => new() { new Point(b.Right, b.Center.Y) },
            AnnotationKind.Step => new(),
            _ => new() { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft },
        };
    }

    private void MoveHandle(Annotation a, int index, Point p)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Line or AnnotationKind.Arrow:
                if (index == 0) (a.X1, a.Y1) = (p.X, p.Y); else (a.X2, a.Y2) = (p.X, p.Y);
                break;
            case AnnotationKind.Callout:
                (a.X2, a.Y2) = (p.X, p.Y);
                break;
            case AnnotationKind.Text:
                a.X2 = Math.Max(a.Left + 40, p.X);
                break;
            default:
                // the corner opposite to the dragged one stays; normalised so X1/Y1 are always the top-left
                var b = AnnotationRenderer.Bounds(a);
                var fixedPoint = index switch { 0 => b.BottomRight, 1 => b.BottomLeft, 2 => b.TopLeft, _ => b.TopRight };
                (a.X1, a.Y1, a.X2, a.Y2) = (Math.Min(fixedPoint.X, p.X), Math.Min(fixedPoint.Y, p.Y),
                    Math.Max(fixedPoint.X, p.X), Math.Max(fixedPoint.Y, p.Y));
                break;
        }
    }

    private int HandleAt(Annotation a, Point p)
    {
        var reach = 8 / _zoom;
        var handles = Handles(a);
        for (var i = 0; i < handles.Count; i++)
            if (Math.Abs(handles[i].X - p.X) <= reach && Math.Abs(handles[i].Y - p.Y) <= reach) return i;
        return -1;
    }

    private Annotation? HitTest(Point p)
    {
        var slack = 6 / _zoom;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var a = Items[i];
            var b = AnnotationRenderer.Bounds(a);
            bool hit;
            switch (a.Kind)
            {
                case AnnotationKind.Line or AnnotationKind.Arrow:
                    hit = DistanceToSegment(p, new Point(a.X1, a.Y1), new Point(a.X2, a.Y2)) <= Math.Max(slack, a.Width / 2 + 3);
                    break;
                case AnnotationKind.Box:
                    hit = b.Inflate(slack + a.Width / 2).Contains(p) && !b.Deflate(slack + a.Width / 2).Contains(p);
                    break;
                case AnnotationKind.Ellipse:
                {
                    // near the outline: the point's relative distance from the centre is close to 1
                    var rx = Math.Max(b.Width / 2, 1);
                    var ry = Math.Max(b.Height / 2, 1);
                    var d = Math.Sqrt(Math.Pow((p.X - b.Center.X) / rx, 2) + Math.Pow((p.Y - b.Center.Y) / ry, 2));
                    var tolerance = (slack + a.Width / 2) / Math.Min(rx, ry);
                    hit = Math.Abs(d - 1) <= tolerance;
                    break;
                }
                default:
                    hit = b.Inflate(slack).Contains(p);
                    break;
            }
            if (hit) return a;
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 0.0001 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        var cx = a.X + t * dx;
        var cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }

    // ---- mouse ------------------------------------------------------------------------------

    private Point ToImage(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        return new Point(Math.Clamp(p.X / _zoom, 0, _source?.Width ?? 0), Math.Clamp(p.Y / _zoom, 0, _source?.Height ?? 0));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var p = ToImage(e);
        _start = _last = p;

        if (Tool == TextTool)
        {
            _drag = Drag.Grab;
            _crop = new Rect(p, p);
            e.Pointer.Capture(this);
            return;
        }

        if (Tool == CropTool)
        {
            _drag = Drag.Crop;
            _crop = new Rect(p, p);
            e.Pointer.Capture(this);
            return;
        }

        if (Tool == SelectTool)
        {
            if (Selected is not null && HandleAt(Selected, p) is var h && h >= 0)
            {
                BeforeChange?.Invoke();
                _drag = Drag.Handle;
                _handle = h;
                e.Pointer.Capture(this);
                return;
            }
            var hit = HitTest(p);
            if (hit is not null && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl + drag: the copy is what moves, the original stays
                BeforeChange?.Invoke();
                hit = Duplicate(hit, 0);
            }
            Select(hit);
            if (hit is not null)
            {
                if (e.ClickCount == 2 && hit.Kind is AnnotationKind.Text or AnnotationKind.Callout or AnnotationKind.Step or AnnotationKind.Box or AnnotationKind.Ellipse or AnnotationKind.Arrow)
                    TextRequested?.Invoke(hit);
                BeforeChange?.Invoke();
                _drag = Drag.Move;
                e.Pointer.Capture(this);
            }
            return;
        }

        // a drawing tool: start a new shape
        BeforeChange?.Invoke();
        var a = new Annotation
        {
            Kind = Tool,
            X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y,
            Color = Tool == AnnotationKind.Redact ? "#000000" : DrawColor,
            Width = StrokeWidth,
            FontSize = FontSize,
            Number = Tool == AnnotationKind.Step ? Items.Where(i => i.Kind == AnnotationKind.Step).Select(i => i.Number).DefaultIfEmpty(0).Max() + 1 : 0,
        };
        Items.Add(a);
        Selected = a;
        SelectionChanged?.Invoke();
        _drag = Drag.Create;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_source is null) return;
        var p = ToImage(e);
        switch (_drag)
        {
            case Drag.Crop:
                _crop = new Rect(_start, p).Normalize();
                InvalidateVisual();
                break;
            case Drag.Grab:
                _crop = new Rect(_start, p).Normalize();
                GrabLines(_crop);
                InvalidateVisual();
                break;
            case Drag.Create when Selected is { } a:
                if (a.Kind == AnnotationKind.Step) { a.X1 = p.X; a.Y1 = p.Y; }
                else if (a.Kind == AnnotationKind.Callout) { /* the box stays, the pointer follows */ (a.X2, a.Y2) = (p.X, p.Y); }
                else
                {
                    var q = Constrain(a, p, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    (a.X2, a.Y2) = (q.X, q.Y);
                }
                InvalidateVisual();
                break;
            case Drag.Move when Selected is { } a:
            {
                var dx = p.X - _last.X;
                var dy = p.Y - _last.Y;
                a.X1 += dx; a.Y1 += dy;
                if (a.Kind != AnnotationKind.Callout) { a.X2 += dx; a.Y2 += dy; }
                _last = p;
                InvalidateVisual();
                break;
            }
            case Drag.Handle when Selected is { } a:
                MoveHandle(a, _handle, Constrain(a, p, e.KeyModifiers.HasFlag(KeyModifiers.Shift), _handle));
                InvalidateVisual();
                break;
            default:
                Cursor = new Cursor(Tool == SelectTool
                    ? (Selected is { } s && HandleAt(s, p) >= 0 ? StandardCursorType.SizeAll
                        : HitTest(p) is not null ? StandardCursorType.SizeAll : StandardCursorType.Arrow)
                    : StandardCursorType.Cross);
                break;
        }
    }

    /// <summary>Shift keeps a line at 45° steps and a box / ellipse square.</summary>
    private static Point Constrain(Annotation a, Point p, bool shift, int handle = 1)
    {
        if (!shift) return p;
        var origin = a.Kind is AnnotationKind.Line or AnnotationKind.Arrow
            ? (handle == 0 ? new Point(a.X2, a.Y2) : new Point(a.X1, a.Y1))
            : new Point(a.X1, a.Y1);
        var dx = p.X - origin.X;
        var dy = p.Y - origin.Y;
        if (a.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            var len = Math.Sqrt(dx * dx + dy * dy);
            return new Point(origin.X + Math.Cos(angle) * len, origin.Y + Math.Sin(angle) * len);
        }
        if (IsRect(a))
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(origin.X + Math.Sign(dx == 0 ? 1 : dx) * side, origin.Y + Math.Sign(dy == 0 ? 1 : dy) * side);
        }
        return p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);

        if (drag == Drag.Grab)
        {
            var area = _crop;
            // a click picks the line under the pointer
            var text = GrabLines(area.Width < 4 && area.Height < 4 ? area.Inflate(3 / _zoom) : area);
            _crop = default;
            InvalidateVisual();
            TextGrabbed?.Invoke(text, _grabbed.Count);
            return;
        }

        if (drag == Drag.Crop)
        {
            var area = _crop;
            _crop = default;
            InvalidateVisual();
            if (area.Width >= 8 && area.Height >= 8)
                CropRequested?.Invoke(new PixelRect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height));
            return;
        }

        if (Selected is { } a)
        {
            if (drag == Drag.Create)
            {
                var tiny = Math.Abs(a.X2 - a.X1) < 4 && Math.Abs(a.Y2 - a.Y1) < 4;
                if (a.Kind == AnnotationKind.Callout && tiny) (a.X2, a.Y2) = (a.X1 - 30, a.Y1 + 80); // a click: the pointer points down-left
                else if (tiny && a.Kind is not (AnnotationKind.Text or AnnotationKind.Step))
                {
                    Items.Remove(a);
                    Selected = null;
                    SelectionChanged?.Invoke();
                    InvalidateVisual();
                    return;
                }
                if (a.Kind == AnnotationKind.Text && tiny) a.X2 = a.X1;
            }
            if (IsRect(a) || a.Kind == AnnotationKind.Text)
            {
                (a.X1, a.X2) = (Math.Min(a.X1, a.X2), Math.Max(a.X1, a.X2));
                (a.Y1, a.Y2) = (Math.Min(a.Y1, a.Y2), Math.Max(a.Y1, a.Y2));
            }
            Changed?.Invoke();
            if (drag == Drag.Create)
            {
                ToolDone?.Invoke();
                if (a.Kind is AnnotationKind.Text or AnnotationKind.Callout or AnnotationKind.Step) TextRequested?.Invoke(a);
            }
        }
        InvalidateVisual();
    }

    /// <summary>Adds a copy of <paramref name="a"/> (moved by <paramref name="offset"/> pixels) just above it.</summary>
    private Annotation Duplicate(Annotation a, double offset)
    {
        var copy = a.Clone();
        copy.Id = Guid.NewGuid();
        copy.X1 += offset; copy.X2 += offset; copy.Y1 += offset; copy.Y2 += offset;
        if (copy.Kind == AnnotationKind.Step)
            copy.Number = Items.Where(i => i.Kind == AnnotationKind.Step).Select(i => i.Number).DefaultIfEmpty(0).Max() + 1;
        Items.Add(copy);
        return copy;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Selected is not { } a) return;
        if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            BeforeChange?.Invoke();
            Select(Duplicate(a, 20));
            Changed?.Invoke();
            e.Handled = true;
            return;
        }
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        double dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                BeforeChange?.Invoke();
                Items.Remove(a);
                Select(null);
                Changed?.Invoke();
                e.Handled = true;
                return;
            case Key.Left: dx = -step; break;
            case Key.Right: dx = step; break;
            case Key.Up: dy = -step; break;
            case Key.Down: dy = step; break;
            default: return;
        }
        BeforeChange?.Invoke();
        a.X1 += dx; a.X2 += dx; a.Y1 += dy; a.Y2 += dy;
        Changed?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }
}
