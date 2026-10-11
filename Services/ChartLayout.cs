using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ScriptAppDashboard.Services;

public enum ShapeKind { Fill, Stroke, Dot, Text }

/// <summary>
/// One mark of a chart. Fill and Stroke draw <see cref="Path"/> (SVG path data); Dot is a circle at P = cx, cy, r with a
/// ring in the surface colour; Text is centred vertically on P = x, y and starts at, is centred on or ends at x
/// (<see cref="Align"/> -1, 0, 1). Colours are tokens for <see cref="ChartLayout.Color"/>.
/// </summary>
public sealed record ChartShape(ShapeKind Kind, string Color, string Path = "", double[]? P = null, string Text = "",
    double Size = 11, int Align = 0, bool Bold = false, double Width = 2, bool Gap = false, string? Tip = null);

/// <summary>What the pointer is over: a tooltip and what to highlight — a column band, a crosshair (W = 0) or nothing (H = 0).</summary>
public sealed record ChartHover(string Tip, double X, double Y, double W, double H);

/// <summary>A chart's labels (first column) and number series (the other columns that hold numbers).</summary>
public sealed class ChartData
{
    public List<string> Labels { get; } = new();
    public List<string> Series { get; } = new();
    public List<double?[]> Values { get; } = new();
    public int HiddenSeries { get; set; }
}

/// <summary>
/// Lays out a chart block (bar, line or pie) as shapes, so the editor (Avalonia) and the HTML export (SVG) draw the
/// same picture. Marks follow the app's chart style: thin bars with a rounded end, 2 px lines, ringed dots,
/// hairline grid, a legend for two or more series, and categorical colours in a fixed order (at most 8 series).
/// </summary>
public sealed class ChartLayout
{
    public const int MaxSeries = 8;
    private const double Pad = 10;

    // validated categorical palette, fixed order; the dark column is the same hues stepped for a dark surface
    private static readonly string[] SeriesLight = { "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948" };
    private static readonly string[] SeriesDark = { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767" };

    public List<ChartShape> Shapes { get; } = new();
    private readonly List<(double X, double Y, double W, double H, string Tip, bool Cross, double CrossX)> _bands = new();
    private readonly List<(double Cx, double Cy, double R, double Inner, double Start, double Sweep, string Tip)> _wedges = new();

    /// <summary>
    /// A colour token as hex: s0–s7 (series), text, secondary, muted, grid, axis, surface; border and hover are
    /// translucent #AARRGGBB (the editor only).
    /// </summary>
    public static string Color(string token, bool dark) => token switch
    {
        "text" => dark ? "#f0efec" : "#0b0b0b",
        "secondary" => dark ? "#c3c2b7" : "#52514e",
        "muted" => "#898781",
        "grid" => dark ? "#2c2c2a" : "#e1e0d9",
        "axis" => dark ? "#383835" : "#c3c2b7",
        "surface" => dark ? "#1a1a19" : "#fcfcfb",
        "border" => dark ? "#1AFFFFFF" : "#1A0B0B0B",
        "hover" => dark ? "#12FFFFFF" : "#0D0B0B0B",
        _ when token.Length > 1 && token[0] == 's' && int.TryParse(token[1..], out var i) && i is >= 0 and < MaxSeries
            => (dark ? SeriesDark : SeriesLight)[i],
        _ => "#888888",
    };

    // ---- data -------------------------------------------------------------------------

    /// <summary>Labels from the first column; every other column with at least one number is a series.</summary>
    public static ChartData Data(NoteBlock block)
    {
        var data = new ChartData();
        var cells = block.Cells;
        if (cells.Count < 2 || cells[0].Count < 2) return data;
        var rows = cells.Skip(1).Where(r => r.Any(c => NoteDocument.PlainText(c).Trim().Length > 0)).ToList();
        foreach (var r in rows) data.Labels.Add(NoteDocument.PlainText(r[0]).Trim());
        for (var c = 1; c < cells[0].Count; c++)
        {
            var values = rows.Select(r => c < r.Count ? ParseNumber(NoteDocument.PlainText(r[c])) : null).ToArray();
            if (values.All(v => v is null)) continue;
            if (data.Series.Count == MaxSeries)
            {
                data.HiddenSeries++;
                continue;
            }
            var name = NoteDocument.PlainText(cells[0][c]).Trim();
            data.Series.Add(name.Length > 0 ? name : $"Series {c}");
            data.Values.Add(values);
        }
        return data;
    }

    private static readonly Regex NumberText = new(@"^[^\d\-−+.,]*([-−+]?[\d.,' ]*\d[\d.,' ]*)\D*$", RegexOptions.Compiled);

    /// <summary>
    /// A number in a cell: "1,234.5", "1.234,5", "12%", "$40", "3 kg". A lone comma before exactly three digits is a
    /// thousands separator, otherwise a decimal comma. Dates and words are not numbers.
    /// </summary>
    public static double? ParseNumber(string text)
    {
        var m = NumberText.Match(text.Trim());
        if (!m.Success) return null;
        var s = m.Groups[1].Value.Replace(" ", "").Replace("'", "").Replace('−', '-').Replace("+", "");
        int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
        if (dot >= 0 && comma >= 0) s = dot > comma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
        else if (comma >= 0) s = s.Count(c => c == ',') == 1 && s.Length - comma - 1 != 3 ? s.Replace(',', '.') : s.Replace(",", "");
        else if (s.Count(c => c == '.') > 1) s = s.Replace(".", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    public static string FormatValue(double v) => v.ToString("#,0.###", CultureInfo.CurrentCulture);

    /// <summary>Axis numbers: 950, 1.5K, 2M.</summary>
    private static string Compact(double v)
    {
        var a = Math.Abs(v);
        var (div, suffix) = a >= 1e9 ? (1e9, "B") : a >= 1e6 ? (1e6, "M") : a >= 1e3 ? (1e3, "K") : (1.0, "");
        return (v / div).ToString("0.##", CultureInfo.CurrentCulture) + suffix;
    }

    // ---- layout -----------------------------------------------------------------------

    public static ChartLayout Build(NoteBlock block, double width, double height)
    {
        var layout = new ChartLayout();
        if (width < 120 || height < 80) return layout;
        var data = Data(block);
        if (data.Series.Count == 0 || data.Labels.Count == 0)
        {
            layout.Text(width / 2, height / 2, Fit("Put labels in the first column and numbers in the next ones", width - 2 * Pad, 12), 12, 0, "muted");
            return layout;
        }
        switch (NoteDocument.ChartTypeName(block.ChartType))
        {
            case "pie": layout.Pie(data, width, height); break;
            case "line": layout.Axes(data, width, height, line: true); break;
            default: layout.Axes(data, width, height, line: false); break;
        }
        return layout;
    }

    /// <summary>The tooltip under a point: the pie slice, or the column (all series at that label).</summary>
    public ChartHover? HoverAt(double x, double y)
    {
        foreach (var w in _wedges)
        {
            var dist = Math.Sqrt((x - w.Cx) * (x - w.Cx) + (y - w.Cy) * (y - w.Cy));
            if (dist < w.Inner || dist > w.R) continue;
            var angle = Math.Atan2(y - w.Cy, x - w.Cx) * 180 / Math.PI;
            if (((angle - w.Start) % 360 + 360) % 360 < w.Sweep) return new ChartHover(w.Tip, 0, 0, 0, 0);
        }
        foreach (var b in _bands)
        {
            if (x < b.X || x >= b.X + b.W || y < b.Y || y > b.Y + b.H) continue;
            return b.Cross ? new ChartHover(b.Tip, b.CrossX, b.Y, 0, b.H) : new ChartHover(b.Tip, b.X, b.Y, b.W, b.H);
        }
        return null;
    }

    private void Axes(ChartData d, double width, double height, bool line)
    {
        var top = Pad;
        if (d.Series.Count > 1 || d.HiddenSeries > 0)
            top = Legend(d.Series, d.HiddenSeries > 0 ? $"+{d.HiddenSeries} more not shown" : null, Pad, top, width - 2 * Pad) + 6;
        var bottom = height - Pad - 18; // room for the labels
        if (bottom - top < 30) return;

        var all = d.Values.SelectMany(v => v).OfType<double>().ToList();
        double lo = all.Min(), hi = all.Max();
        if (!line || lo > 0 && lo < hi * 0.5) lo = Math.Min(lo, 0); // bars always grow from zero
        if (!line) hi = Math.Max(hi, 0);
        var (min, max, step) = Scale(lo, hi, Math.Max(2, (int)((bottom - top) / 44)));
        var ticks = new List<(double V, string S)>();
        for (var k = 0; min + k * step <= max + step * 1e-6; k++)
        {
            var v = min + k * step;
            if (Math.Abs(v) < step * 1e-9) v = 0;
            ticks.Add((v, Compact(v)));
        }
        var left = Pad + ticks.Max(t => TextWidth(t.S, 11)) + 8;
        var right = width - Pad;
        double Y(double v) => bottom - (v - min) / (max - min) * (bottom - top);
        var baseV = min > 0 ? min : max < 0 ? max : 0;

        foreach (var (v, s) in ticks)
        {
            var y = Math.Round(Y(v)) + 0.5; // hairlines on the pixel grid
            Shapes.Add(new ChartShape(ShapeKind.Stroke, v == baseV ? "axis" : "grid", $"M{F(left)},{F(y)}H{F(right)}", Width: 1));
            Text(left - 6, y, s, 11, 1, "muted");
        }

        var n = d.Labels.Count;
        var band = (right - left) / n;
        var widest = Math.Min(d.Labels.Max(l => TextWidth(l, 11)), 140);
        var every = Math.Max(1, (int)Math.Ceiling((widest + 10) / band)); // skip labels that would collide
        for (var i = 0; i < n; i += every)
        {
            var cx = left + (i + 0.5) * band;
            Text(cx, bottom + 12, Fit(d.Labels[i], Math.Min(band * every - 6, 2 * (cx - Pad)), 11), 11, 0, "muted");
        }

        var count = d.Series.Count;
        if (!line)
        {
            var barWidth = Math.Clamp((band * 0.72 - (count - 1) * 2) / count, 2, 24);
            var group = count * barWidth + (count - 1) * 2;
            for (var i = 0; i < n; i++)
            {
                var gx = left + i * band + (band - group) / 2;
                for (var j = 0; j < count; j++)
                {
                    if (d.Values[j][i] is not { } v) continue;
                    double y1 = Y(v), y0 = Y(baseV);
                    var h = Math.Abs(y1 - y0);
                    if (h < 0.5) continue;
                    var r = Math.Min(4, Math.Min(h, barWidth / 2));
                    Shapes.Add(new ChartShape(ShapeKind.Fill, $"s{j}", BarPath(gx + j * (barWidth + 2), Math.Min(y0, y1), barWidth, h, r, v >= baseV)));
                }
            }
        }
        else
        {
            for (var j = 0; j < count; j++)
            {
                var path = new StringBuilder();
                var dots = new List<ChartShape>();
                for (var i = 0; i < n; i++)
                {
                    if (d.Values[j][i] is not { } v) continue;
                    var (x, y) = (left + (i + 0.5) * band, Y(v));
                    var joined = i > 0 && d.Values[j][i - 1] is not null;
                    path.Append(joined ? 'L' : 'M').Append(F(x)).Append(',').Append(F(y));
                    // markers on every point while they fit; otherwise the end and points with no neighbour
                    var alone = !joined && (i + 1 >= n || d.Values[j][i + 1] is null);
                    if (n <= 24 || i == n - 1 || alone) dots.Add(new ChartShape(ShapeKind.Dot, $"s{j}", P: new[] { x, y, 4.0 }));
                }
                if (path.Length > 0) Shapes.Add(new ChartShape(ShapeKind.Stroke, $"s{j}", path.ToString()));
                Shapes.AddRange(dots);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var tip = new StringBuilder(d.Labels[i].Length > 0 ? d.Labels[i] : $"#{i + 1}");
            for (var j = 0; j < count; j++)
                tip.Append('\n').Append(d.Series[j]).Append(": ").Append(d.Values[j][i] is { } v ? FormatValue(v) : "—");
            _bands.Add((left + i * band, top, band, bottom - top, tip.ToString(), line, left + (i + 0.5) * band));
        }
    }

    private void Pie(ChartData d, double width, double height)
    {
        var values = d.Values[0];
        var slices = d.Labels.Select((l, i) => (Label: l.Length > 0 ? l : $"#{i + 1}", V: values[i] ?? 0)).Where(s => s.V > 0).ToList();
        if (slices.Count == 0)
        {
            Text(width / 2, height / 2, "A pie needs positive numbers", 12, 0, "muted");
            return;
        }
        if (slices.Count > MaxSeries)
        {
            // past eight slices the smallest fold into "Other", instead of repeating colours
            var keep = slices.Select((s, i) => (s.V, i)).OrderByDescending(x => x.V).Take(MaxSeries - 1).Select(x => x.i).ToHashSet();
            var other = slices.Where((_, i) => !keep.Contains(i)).Sum(s => s.V);
            slices = slices.Where((_, i) => keep.Contains(i)).ToList();
            slices.Add(("Other", other));
        }
        var total = slices.Sum(s => s.V);
        var legendWidth = Math.Min(200, width * 0.45);
        var size = Math.Min(height - 2 * Pad, width - legendWidth - 4 * Pad);
        if (size < 60)
        {
            legendWidth = 0;
            size = Math.Min(height, width) - 2 * Pad;
        }
        var r = size / 2;
        var inner = r * 0.6;
        // the doughnut and its legend, centred together
        double cx = legendWidth == 0 ? width / 2 : Math.Max(Pad, (width - size - 3 * Pad - legendWidth) / 2) + r, cy = height / 2;

        var start = -90.0;
        for (var i = 0; i < slices.Count; i++)
        {
            var sweep = slices[i].V / total * 360;
            var tip = $"{slices[i].Label}: {FormatValue(slices[i].V)} ({Percent(slices[i].V / total)})";
            Shapes.Add(new ChartShape(ShapeKind.Fill, $"s{i}", Wedge(cx, cy, r, inner, start, sweep), Gap: true, Tip: tip));
            _wedges.Add((cx, cy, r, inner, start, sweep, tip));
            start += sweep;
        }
        Text(cx, cy - 7, Compact(total), Math.Clamp(r / 4, 11, 18), 0, "text", bold: true);
        Text(cx, cy + 11, Fit(d.Series[0], inner * 1.7, 11), 11, 0, "muted");

        if (legendWidth == 0) return;
        var lx = cx + r + 3 * Pad;
        var rowHeight = 20.0;
        var ly = cy - slices.Count * rowHeight / 2;
        for (var i = 0; i < slices.Count; i++, ly += rowHeight)
        {
            Shapes.Add(new ChartShape(ShapeKind.Fill, $"s{i}", RoundRect(lx, ly + rowHeight / 2 - 5, 10, 10, 2)));
            var pct = Percent(slices[i].V / total);
            Text(lx + 16, ly + rowHeight / 2, Fit(slices[i].Label, legendWidth - 24 - TextWidth(pct, 12) - 8, 12), 12, -1, "secondary");
            Text(lx + legendWidth, ly + rowHeight / 2, pct, 12, 1, "muted");
        }
    }

    private static string Percent(double share) => (share * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";

    /// <summary>Legend swatches and names in rows; returns the y below them.</summary>
    private double Legend(IReadOnlyList<string> names, string? extra, double x0, double y, double maxWidth)
    {
        const double lineHeight = 18;
        var x = x0;
        for (var i = 0; i < names.Count; i++)
        {
            var label = Fit(names[i], 160, 12);
            var w = 16 + TextWidth(label, 12) + 16;
            if (x > x0 && x + w > x0 + maxWidth)
            {
                x = x0;
                y += lineHeight;
            }
            Shapes.Add(new ChartShape(ShapeKind.Fill, $"s{i}", RoundRect(x, y + lineHeight / 2 - 5, 10, 10, 2)));
            Text(x + 16, y + lineHeight / 2, label, 12, -1, "secondary");
            x += w;
        }
        if (extra is not null)
        {
            if (x > x0 && x + TextWidth(extra, 12) > x0 + maxWidth)
            {
                x = x0;
                y += lineHeight;
            }
            Text(x, y + lineHeight / 2, extra, 12, -1, "muted");
        }
        return y + lineHeight;
    }

    private void Text(double x, double y, string text, double size, int align, string color, bool bold = false)
    {
        if (text.Length > 0) Shapes.Add(new ChartShape(ShapeKind.Text, color, P: new[] { x, y }, Text: text, Size: size, Align: align, Bold: bold));
    }

    /// <summary>Round numbers for the value axis, about <paramref name="ticks"/> steps from lo to hi.</summary>
    private static (double Min, double Max, double Step) Scale(double lo, double hi, int ticks)
    {
        if (hi - lo < 1e-12)
        {
            if (hi > 0) lo = 0;
            else if (hi < 0) hi = 0;
            else hi = 1;
        }
        var raw = (hi - lo) / ticks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        var step = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * mag;
        return (Math.Floor(lo / step + 1e-9) * step, Math.Ceiling(hi / step - 1e-9) * step, step);
    }

    // ---- geometry and text ------------------------------------------------------------

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A bar with a rounded data end (top for a positive value) and a square end on the baseline.</summary>
    private static string BarPath(double x, double y, double w, double h, double r, bool roundTop)
    {
        if (r < 0.5) return $"M{F(x)},{F(y)}H{F(x + w)}V{F(y + h)}H{F(x)}Z";
        return roundTop
            ? $"M{F(x)},{F(y + h)}V{F(y + r)}A{F(r)},{F(r)} 0 0 1 {F(x + r)},{F(y)}H{F(x + w - r)}A{F(r)},{F(r)} 0 0 1 {F(x + w)},{F(y + r)}V{F(y + h)}Z"
            : $"M{F(x)},{F(y)}V{F(y + h - r)}A{F(r)},{F(r)} 0 0 0 {F(x + r)},{F(y + h)}H{F(x + w - r)}A{F(r)},{F(r)} 0 0 0 {F(x + w)},{F(y + h - r)}V{F(y)}Z";
    }

    private static string RoundRect(double x, double y, double w, double h, double r)
        => $"M{F(x + r)},{F(y)}H{F(x + w - r)}A{F(r)},{F(r)} 0 0 1 {F(x + w)},{F(y + r)}V{F(y + h - r)}A{F(r)},{F(r)} 0 0 1 {F(x + w - r)},{F(y + h)}"
         + $"H{F(x + r)}A{F(r)},{F(r)} 0 0 1 {F(x)},{F(y + h - r)}V{F(y + r)}A{F(r)},{F(r)} 0 0 1 {F(x + r)},{F(y)}Z";

    /// <summary>A doughnut slice from <paramref name="start"/> degrees (0 = right, clockwise) over <paramref name="sweep"/>.</summary>
    private static string Wedge(double cx, double cy, double r, double inner, double start, double sweep)
    {
        sweep = Math.Min(sweep, 359.99);
        double a0 = start * Math.PI / 180, a1 = (start + sweep) * Math.PI / 180;
        var large = sweep > 180 ? 1 : 0;
        string P(double radius, double a) => F(cx + radius * Math.Cos(a)) + "," + F(cy + radius * Math.Sin(a));
        return $"M{P(r, a0)}A{F(r)},{F(r)} 0 {large} 1 {P(r, a1)}L{P(inner, a1)}A{F(inner)},{F(inner)} 0 {large} 0 {P(inner, a0)}Z";
    }

    /// <summary>Estimated width of UI text (the layout is shared with SVG, where nothing can be measured).</summary>
    private static double TextWidth(string s, double size) => s.Sum(c => c >= 0x2E80 ? 1.0 : char.IsUpper(c) ? 0.66 : 0.55) * size;

    /// <summary>The text, cut with an ellipsis to fit the width.</summary>
    private static string Fit(string s, double width, double size)
    {
        if (TextWidth(s, size) <= width) return s;
        while (s.Length > 0 && TextWidth(s + "…", size) > width) s = s[..^1];
        return s.Length == 0 ? "" : s.TrimEnd() + "…";
    }

    // ---- SVG (export) -----------------------------------------------------------------

    /// <summary>The chart as an inline SVG picture (light colours), with a tooltip on every column or slice.</summary>
    public static string ToSvg(NoteBlock block, double width = 640, double height = 300)
    {
        var layout = Build(block, width, height);
        string C(string token) => Color(token, dark: false);
        string E(string s) => WebUtility.HtmlEncode(s);
        var title = NoteDocument.PlainText(block.Text).Trim();
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(width)} {F(height)}\" width=\"{F(width)}\" height=\"{F(height)}\" role=\"img\" aria-label=\"{E(title.Length > 0 ? title : "Chart")}\"")
          .Append(" style=\"max-width:100%;height:auto;font-family:system-ui,-apple-system,'Segoe UI',sans-serif\">\n");
        foreach (var s in layout.Shapes)
        {
            switch (s.Kind)
            {
                case ShapeKind.Fill:
                    sb.Append($"<path d=\"{s.Path}\" fill=\"{C(s.Color)}\"");
                    if (s.Gap) sb.Append($" stroke=\"{C("surface")}\" stroke-width=\"2\" stroke-linejoin=\"round\"");
                    sb.Append(s.Tip is null ? "/>\n" : $"><title>{E(s.Tip)}</title></path>\n");
                    break;
                case ShapeKind.Stroke:
                    sb.Append($"<path d=\"{s.Path}\" fill=\"none\" stroke=\"{C(s.Color)}\" stroke-width=\"{F(s.Width)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
                    break;
                case ShapeKind.Dot:
                    sb.Append($"<circle cx=\"{F(s.P![0])}\" cy=\"{F(s.P[1])}\" r=\"{F(s.P[2])}\" fill=\"{C(s.Color)}\" stroke=\"{C("surface")}\" stroke-width=\"2\"/>\n");
                    break;
                case ShapeKind.Text:
                    var anchor = s.Align < 0 ? "start" : s.Align == 0 ? "middle" : "end";
                    sb.Append($"<text x=\"{F(s.P![0])}\" y=\"{F(s.P[1])}\" dominant-baseline=\"central\" text-anchor=\"{anchor}\" font-size=\"{F(s.Size)}\" fill=\"{C(s.Color)}\"")
                      .Append(s.Bold ? " font-weight=\"600\"" : "").Append($">{E(s.Text)}</text>\n");
                    break;
            }
        }
        foreach (var b in layout._bands)
            sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.W)}\" height=\"{F(b.H)}\" fill=\"transparent\"><title>{E(b.Tip)}</title></rect>\n");
        return sb.Append("</svg>").ToString();
    }
}
