using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>What the AI changed: counts for the status line and the optional title / tags / description.</summary>
public sealed record CaptureAiChange(string Summary, int Added, int Updated, int Deleted, string? Title, List<string> Tags, string? Description);

/// <summary>
/// Lets the AI work on a screenshot. The AI CLI reads text only, so it is given the OCR lines with their rectangles
/// and the existing annotations (<see cref="PictureInfo"/>) and answers with add / update / delete operations
/// (<see cref="Apply"/>), which refer to OCR lines ("L3") and annotations ("A2") by number.
/// </summary>
public static class CaptureAi
{
    private static string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    /// <summary>Picture size, window, OCR lines (L1…) and annotations (A1…) as plain text for a prompt.</summary>
    public static string PictureInfo(CaptureItem c, IReadOnlyList<Annotation> annotations)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Picture: {c.Width} x {c.Height} pixels.");
        if (c.SourceApp.Length + c.SourceTitle.Length > 0) sb.AppendLine($"Taken from: {c.SourceApp} — {c.SourceTitle}");
        if (c.Title.Length > 0) sb.AppendLine("Title: " + c.Title);
        sb.AppendLine();
        if (c.Ocr.Count == 0)
            sb.AppendLine(c.OcrDone ? "No text was found in the picture." : "The text of the picture has not been read.");
        else
        {
            sb.AppendLine("Text found in the picture (line, x,y,w,h):");
            for (var i = 0; i < c.Ocr.Count && i < 250; i++)
            {
                var l = c.Ocr[i];
                if (CaptureService.IsHidden(l, annotations)) continue; // redacted: the AI must not read it (numbers stay as in Apply)
                sb.AppendLine($"L{i + 1}: \"{l.Text}\" @ {N(l.X)},{N(l.Y)},{N(l.W)},{N(l.H)}");
            }
        }
        sb.AppendLine();
        if (annotations.Count == 0) sb.AppendLine("There are no annotations yet.");
        else
        {
            sb.AppendLine("Annotations:");
            for (var i = 0; i < annotations.Count; i++)
            {
                var a = annotations[i];
                sb.AppendLine($"A{i + 1}: {a.Kind} ({N(a.X1)},{N(a.Y1)})-({N(a.X2)},{N(a.Y2)}) {a.Color}" +
                              (a.Kind == AnnotationKind.Step ? $" number={a.Number}" : "") +
                              (a.Text.Length > 0 ? $" text=\"{a.Text}\"" : ""));
            }
        }
        return sb.ToString();
    }

    /// <summary>Applies the operations in the AI's answer to <paramref name="list"/> (the picture size keeps shapes inside it).
    /// Returns null when the answer holds no JSON.</summary>
    public static CaptureAiChange? Apply(string answer, CaptureItem c, List<Annotation> list, string defaultColor)
    {
        if (AiService.ExtractJson(answer) is not { } json) return null;

        // the numbers the AI saw: A1… are the annotations as they were before this answer
        var before = list.ToList();
        var added = 0;
        var updated = 0;
        var deleted = 0;
        var nextStep = list.Where(a => a.Kind == AnnotationKind.Step).Select(a => a.Number).DefaultIfEmpty(0).Max() + 1;

        if (json.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
        {
            foreach (var op in ops.EnumerateArray())
            {
                if (op.ValueKind != JsonValueKind.Object) continue;
                switch (AiService.Str(op, "op").ToLowerInvariant())
                {
                    case "add":
                    {
                        var kind = AiService.Str(op, "kind").ToLowerInvariant();
                        if (!AnnotationKind.All.Contains(kind)) continue;
                        var a = new Annotation { Kind = kind, Color = kind == AnnotationKind.Redact ? "#000000" : defaultColor };
                        Fill(a, op, c);
                        if (kind == AnnotationKind.Step && a.Number == 0) a.Number = nextStep++;
                        list.Add(a);
                        added++;
                        break;
                    }
                    case "update":
                    {
                        var target = Find(AiService.Str(op, "id"), before);
                        if (target is null) continue;
                        Fill(target, op, c, update: true);
                        updated++;
                        break;
                    }
                    case "delete":
                    {
                        var target = Find(AiService.Str(op, "id"), before);
                        if (target is null || !list.Remove(target)) continue;
                        deleted++;
                        break;
                    }
                }
            }
        }

        var tags = new List<string>();
        if (json.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            tags = t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (x.GetString() ?? "").Trim().TrimStart('#').Replace(' ', '-').ToLowerInvariant())
                .Where(x => x.Length > 0).Distinct().Take(6).ToList();
        var title = AiService.Str(json, "title").Trim();
        var description = AiService.Str(json, "description").Trim();
        return new CaptureAiChange(AiService.Str(json, "summary").Trim(), added, updated, deleted,
            title.Length > 0 ? title : null, tags, description.Length > 0 ? description : null);
    }

    private static Annotation? Find(string id, List<Annotation> before)
    {
        id = id.Trim();
        if (id.Length > 1 && char.ToUpperInvariant(id[0]) == 'A' && int.TryParse(id.AsSpan(1), out var n) && n >= 1 && n <= before.Count)
            return before[n - 1];
        return Guid.TryParse(id, out var guid) ? before.FirstOrDefault(a => a.Id == guid) : null;
    }

    /// <summary>Copies the fields of an AI operation onto an annotation; "line" puts it on an OCR line.</summary>
    private static void Fill(Annotation a, JsonElement op, CaptureItem c, bool update = false)
    {
        var lineId = AiService.Str(op, "line").Trim();
        if (lineId.Length > 1 && char.ToUpperInvariant(lineId[0]) == 'L' && int.TryParse(lineId.AsSpan(1), out var ln)
            && ln >= 1 && ln <= c.Ocr.Count)
            PlaceOnLine(a, c.Ocr[ln - 1], c);
        else if (!update || HasAny(op, "x1", "y1", "x2", "y2"))
        {
            if (Num(op, "x1") is { } x1) a.X1 = x1;
            if (Num(op, "y1") is { } y1) a.Y1 = y1;
            if (Num(op, "x2") is { } x2) a.X2 = x2;
            else if (!update && !HasAny(op, "x2")) a.X2 = a.Kind is AnnotationKind.Text or AnnotationKind.Step ? a.X1 : a.X1 + 120;
            if (Num(op, "y2") is { } y2) a.Y2 = y2;
            else if (!update && !HasAny(op, "y2")) a.Y2 = a.Kind is AnnotationKind.Text or AnnotationKind.Step ? a.Y1 : a.Y1 + 60;
        }

        if (op.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) a.Text = text.GetString() ?? "";
        if (op.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String && IsColor(color.GetString()))
            a.Color = color.GetString()!;
        if (Num(op, "width") is { } w) a.Width = Math.Clamp(w, 1, 40);
        if (Num(op, "fontSize") is { } fs) a.FontSize = Math.Clamp(fs, 8, 120);
        if (Num(op, "number") is { } number) a.Number = (int)number;

        // keep everything inside the picture
        a.X1 = Math.Clamp(a.X1, 0, c.Width);
        a.X2 = Math.Clamp(a.X2, 0, c.Width);
        a.Y1 = Math.Clamp(a.Y1, 0, c.Height);
        a.Y2 = Math.Clamp(a.Y2, 0, c.Height);
        if (a.Kind is AnnotationKind.Box or AnnotationKind.Ellipse or AnnotationKind.Highlight or AnnotationKind.Redact or AnnotationKind.Blur)
        {
            (a.X1, a.X2) = (Math.Min(a.X1, a.X2), Math.Max(a.X1, a.X2));
            (a.Y1, a.Y2) = (Math.Min(a.Y1, a.Y2), Math.Max(a.Y1, a.Y2));
        }
    }

    /// <summary>A shape for an OCR line: boxes cover it; an arrow points at its left edge; a callout / step sits next to it.</summary>
    private static void PlaceOnLine(Annotation a, OcrLine l, CaptureItem c)
    {
        const double pad = 4;
        switch (a.Kind)
        {
            case AnnotationKind.Arrow:
                (a.X2, a.Y2) = (Math.Max(0, l.X - 6), l.Y + l.H / 2);
                (a.X1, a.Y1) = (Math.Max(0, a.X2 - 90), Math.Max(0, a.Y2 - 60));
                break;
            case AnnotationKind.Line:
                (a.X1, a.Y1, a.X2, a.Y2) = (l.X, l.Y + l.H + 2, l.X + l.W, l.Y + l.H + 2);
                break;
            case AnnotationKind.Callout:
                (a.X2, a.Y2) = (l.X + l.W / 2, l.Y + l.H);
                (a.X1, a.Y1) = (Math.Min(l.X, Math.Max(0, c.Width - 260)), Math.Min(l.Y + l.H + 50, Math.Max(0, c.Height - 80)));
                break;
            case AnnotationKind.Step:
                (a.X1, a.Y1) = (Math.Max(14, l.X - 24), l.Y + l.H / 2);
                a.X2 = a.X1;
                a.Y2 = a.Y1;
                break;
            case AnnotationKind.Text:
                (a.X1, a.Y1) = (l.X, l.Y + l.H + 2);
                a.X2 = a.X1;
                a.Y2 = a.Y1;
                break;
            default:
                (a.X1, a.Y1, a.X2, a.Y2) = (l.X - pad, l.Y - pad, l.X + l.W + pad, l.Y + l.H + pad);
                break;
        }
    }

    private static bool HasAny(JsonElement op, params string[] names) => names.Any(n => op.TryGetProperty(n, out _));

    private static double? Num(JsonElement op, string name)
    {
        if (!op.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    private static bool IsColor(string? s)
        => s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    /// <summary>Gives a capture without a title the AI's title, tags and description (once the OCR text or an annotation
    /// text exists). Never throws; keeps anything the user typed in the meantime.</summary>
    public static async Task AutoDescribeAsync(Guid id)
    {
        try
        {
            if (CaptureService.Load(id) is not { } c || c.Title.Length > 0) return;
            if (c.Ocr.Count == 0 && CaptureService.AnnotationText(c).Length == 0) return;
            var result = await AiService.AskAsync(AiPrompts.DescribeCapture(PictureInfo(c, c.Annotations)), timeout: TimeSpan.FromSeconds(90));
            if (!result.Ok) return;
            var fresh = CaptureService.Load(id);
            if (fresh is null || fresh.Title.Length > 0) return;
            if (ApplyDescription(result.Text, fresh)) CaptureService.Save(fresh);
        }
        catch
        {
            // the capture stays searchable by its text without a title
        }
    }

    /// <summary>Reads {"title", "tags", "description"} from an answer into a capture: the title only when it has none,
    /// tags merged, the description replaced. Returns false when the answer held no JSON.</summary>
    public static bool ApplyDescription(string answer, CaptureItem c)
    {
        if (AiService.ExtractJson(answer) is not { } json) return false;
        var title = AiService.Str(json, "title").Trim().Trim('"', '#', ' ');
        if (c.Title.Length == 0 && title.Length > 0) c.Title = title;
        var description = AiService.Str(json, "description").Trim();
        if (description.Length > 0) c.Description = description;
        if (json.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            var tags = t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (x.GetString() ?? "").Trim().TrimStart('#').Replace(' ', '-').ToLowerInvariant())
                .Where(x => x.Length > 0).Take(5);
            c.Tags = MergeTags(c.Tags, tags);
        }
        return true;
    }

    /// <summary>"bug" + ["invoice", "bug"] → "bug invoice".</summary>
    public static string MergeTags(string existing, IEnumerable<string> tags)
    {
        var all = existing.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('#')).ToList();
        foreach (var tag in tags)
            if (!all.Contains(tag, StringComparer.OrdinalIgnoreCase)) all.Add(tag);
        return string.Join(' ', all);
    }
}
