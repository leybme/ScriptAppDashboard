using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Draws annotations in picture pixels. The editor (<see cref="AnnotationSurface"/>) and the export to PNG use the same
/// code, so what you see is what is saved.
/// </summary>
public static class AnnotationRenderer
{
    public static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public static Color ColorOf(Annotation a)
    {
        try { return Color.Parse(a.Color); }
        catch { return Colors.Red; }
    }

    /// <summary>White text on dark fills, black on light ones.</summary>
    public static Color TextOn(Color fill)
        => (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) > 160 ? Colors.Black : Colors.White;

    private static FormattedText Format(string text, double size, Color color, double maxWidth = double.PositiveInfinity)
    {
        var f = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, Math.Max(6, size), new SolidColorBrush(color));
        if (!double.IsInfinity(maxWidth)) f.MaxTextWidth = Math.Max(20, maxWidth);
        return f;
    }

    private const double Pad = 8;

    /// <summary>The rectangle the user selects and drags: where the shape is, in picture pixels.</summary>
    public static Rect Bounds(Annotation a)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Text:
            {
                var f = Format(a.Text.Length > 0 ? a.Text : " ", a.FontSize, Colors.Black, TextWidth(a));
                return new Rect(a.Left, a.Top, Math.Max(f.Width, 10), Math.Max(f.Height, a.FontSize));
            }
            case AnnotationKind.Callout:
            {
                var f = Format(a.Text.Length > 0 ? a.Text : " ", a.FontSize, Colors.Black, TextWidth(a));
                return new Rect(a.X1, a.Y1, f.Width + 2 * Pad, f.Height + 2 * Pad);
            }
            case AnnotationKind.Step:
            {
                var r = StepRadius(a);
                return new Rect(a.X1 - r, a.Y1 - r, 2 * r, 2 * r);
            }
            default:
                return new Rect(a.Left, a.Top, a.Right - a.Left, a.Bottom - a.Top);
        }
    }

    /// <summary>Wrap width of a text / callout: the dragged width, or none for a click.</summary>
    private static double TextWidth(Annotation a)
    {
        var w = a.Kind == AnnotationKind.Callout ? Math.Abs(a.X2 - a.X1) : Math.Abs(a.X2 - a.X1);
        return a.Kind == AnnotationKind.Text && w > 40 ? w : double.PositiveInfinity;
    }

    private static double StepRadius(Annotation a) => Math.Max(10, a.FontSize * 0.8);

    /// <summary>Where the pointer of a callout ends / an arrow points: the "anchor".</summary>
    public static Point Anchor(Annotation a) => new(a.X2, a.Y2);

    public static void Draw(DrawingContext ctx, Annotation a, Pixels source)
    {
        var color = ColorOf(a);
        var pen = new Pen(new SolidColorBrush(color), Math.Max(1, a.Width), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var r = new Rect(a.Left, a.Top, a.Right - a.Left, a.Bottom - a.Top);
        switch (a.Kind)
        {
            case AnnotationKind.Box:
                ctx.DrawRectangle(null, pen, r, 3, 3);
                Tag(ctx, a, color, new Point(r.X + a.Width, r.Y + a.Width), inside: true);
                break;
            case AnnotationKind.Ellipse:
                ctx.DrawEllipse(null, pen, r.Center, r.Width / 2, r.Height / 2);
                Tag(ctx, a, color, new Point(r.Center.X, r.Y + r.Height * 0.15), centered: true);
                break;
            case AnnotationKind.Highlight:
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B)), null, r);
                break;
            case AnnotationKind.Redact:
                ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), null, r);
                break;
            case AnnotationKind.Blur:
                Pixelate(ctx, r, source);
                break;
            case AnnotationKind.Line:
                ctx.DrawLine(pen, new Point(a.X1, a.Y1), new Point(a.X2, a.Y2));
                Tag(ctx, a, color, new Point(a.X1, a.Y1), centered: true);
                break;
            case AnnotationKind.Arrow:
                DrawArrow(ctx, a, color, pen);
                Tag(ctx, a, color, new Point(a.X1, a.Y1), centered: true);
                break;
            case AnnotationKind.Text:
            {
                var f = Format(a.Text, a.FontSize, color, TextWidth(a));
                ctx.DrawText(f, new Point(a.Left, a.Top));
                break;
            }
            case AnnotationKind.Callout:
                DrawCallout(ctx, a, color, pen);
                break;
            case AnnotationKind.Step:
                DrawStep(ctx, a, color);
                break;
        }
    }

    private static void DrawArrow(DrawingContext ctx, Annotation a, Color color, Pen pen)
    {
        var tail = new Point(a.X1, a.Y1);
        var head = new Point(a.X2, a.Y2);
        var dx = head.X - tail.X;
        var dy = head.Y - tail.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        var ux = dx / len;
        var uy = dy / len;
        var size = Math.Min(len * 0.6, Math.Max(14, a.Width * 4.5));
        // the shaft stops inside the head so a thick line does not poke out of the tip
        var baseCenter = new Point(head.X - ux * size * 0.8, head.Y - uy * size * 0.8);
        ctx.DrawLine(pen, tail, baseCenter);
        var left = new Point(head.X - ux * size + uy * size * 0.5, head.Y - uy * size - ux * size * 0.5);
        var right = new Point(head.X - ux * size - uy * size * 0.5, head.Y - uy * size + ux * size * 0.5);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(head, true);
            g.LineTo(left);
            g.LineTo(right);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }

    private static void DrawCallout(DrawingContext ctx, Annotation a, Color color, Pen pen)
    {
        var box = Bounds(a);
        var textColor = TextOn(color);
        var anchor = Anchor(a);
        // a pointer from the nearest point on the box edge to the anchor
        var from = new Point(Math.Clamp(anchor.X, box.Left + 6, box.Right - 6), Math.Clamp(anchor.Y, box.Top, box.Bottom));
        if (!box.Contains(anchor))
        {
            var cx = box.Center.X;
            var cy = box.Center.Y;
            var dx = anchor.X - cx;
            var dy = anchor.Y - cy;
            from = box.Center; // starts inside the box; the box is drawn over the start
            var ux = dx;
            var uy = dy;
            var len = Math.Sqrt(ux * ux + uy * uy);
            if (len > 1)
            {
                // a thin wedge from the box to the anchor
                var nx = -uy / len * 8;
                var ny = ux / len * 8;
                var wedge = new StreamGeometry();
                using (var g = wedge.Open())
                {
                    g.BeginFigure(new Point(from.X + nx, from.Y + ny), true);
                    g.LineTo(anchor);
                    g.LineTo(new Point(from.X - nx, from.Y - ny));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(color), null, wedge);
                ctx.DrawEllipse(new SolidColorBrush(color), null, anchor, 5, 5);
            }
        }
        ctx.DrawRectangle(new SolidColorBrush(color), null, box, 8, 8);
        ctx.DrawText(Format(a.Text, a.FontSize, textColor, TextWidth(a)), new Point(box.X + Pad, box.Y + Pad));
    }

    private static void DrawStep(DrawingContext ctx, Annotation a, Color color)
    {
        var r = StepRadius(a);
        var c = new Point(a.X1, a.Y1);
        ctx.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, Math.Max(2, r * 0.12)), c, r, r);
        var number = Format(a.Number.ToString(CultureInfo.InvariantCulture), r * 1.1, TextOn(color));
        ctx.DrawText(number, new Point(c.X - number.Width / 2, c.Y - number.Height / 2));
        if (a.Text.Length > 0)
        {
            var label = Format(a.Text, a.FontSize * 0.7, TextOn(color));
            var lx = c.X + r + 6;
            var box = new Rect(lx, c.Y - label.Height / 2 - 4, label.Width + 16, label.Height + 8);
            ctx.DrawRectangle(new SolidColorBrush(color), null, box, 6, 6);
            ctx.DrawText(label, new Point(lx + 8, box.Y + 4));
        }
    }

    /// <summary>The annotation's text on a label in the shape's colour. A line / arrow starts inside its label (centred on the
    /// tail); a box keeps the label inside its top-left corner, an ellipse inside its top.</summary>
    private static void Tag(DrawingContext ctx, Annotation a, Color color, Point at, bool inside = false, bool centered = false)
    {
        if (a.Text.Length == 0) return;
        var f = Format(a.Text, Math.Max(14, a.FontSize * 0.6), TextOn(color));
        var size = new Size(f.Width + 12, f.Height + 6);
        var box = centered ? new Rect(at.X - size.Width / 2, at.Y - size.Height / 2, size.Width, size.Height)
            : new Rect(at, size);
        ctx.DrawRectangle(new SolidColorBrush(color), null, box, 4, 4);
        ctx.DrawText(f, new Point(box.X + 6, box.Y + 3));
    }

    /// <summary>Mosaic: every block of the area gets the average colour of that block in the original picture.</summary>
    private static void Pixelate(DrawingContext ctx, Rect r, Pixels source)
    {
        var block = Math.Max(8, source.Height / 90);
        for (var y = r.Top; y < r.Bottom; y += block)
        {
            for (var x = r.Left; x < r.Right; x += block)
            {
                var w = Math.Min(block, r.Right - x);
                var h = Math.Min(block, r.Bottom - y);
                int sb = 0, sg = 0, sr = 0, n = 0;
                for (var j = 0; j < 4; j++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var (b, g, rr) = source.At((int)(x + w * (i + 0.5) / 4), (int)(y + h * (j + 0.5) / 4));
                        sb += b; sg += g; sr += rr; n++;
                    }
                }
                ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n))), null,
                    new Rect(x, y, w + 0.5, h + 0.5));
            }
        }
    }

    /// <summary>The picture with its annotations drawn in, as PNG bytes.</summary>
    public static byte[] ExportPng(Pixels source, IEnumerable<Annotation> annotations, Bitmap? bitmap = null)
    {
        var own = bitmap is null ? source.ToBitmap() : null;
        try
        {
            var image = bitmap ?? own!;
            using var target = new RenderTargetBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96));
            using (var ctx = target.CreateDrawingContext())
            {
                ctx.DrawImage(image, new Rect(0, 0, source.Width, source.Height));
                foreach (var a in annotations) Draw(ctx, a, source);
            }
            using var ms = new MemoryStream();
            target.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            own?.Dispose();
        }
    }
}
