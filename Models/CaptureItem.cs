using System;
using System.Collections.Generic;

namespace ScriptAppDashboard.Models;

/// <summary>A screenshot with its annotations. The picture itself is <c>captures/&lt;id&gt;.png</c> and is never changed,
/// so every annotation stays editable; the text fields below are what the capture search looks at.</summary>
public class CaptureItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Tags { get; set; } = "";

    /// <summary>What the AI says the picture shows (searchable).</summary>
    public string Description { get; set; } = "";

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;

    /// <summary>The program and window title that were in front when the capture was taken.</summary>
    public string SourceApp { get; set; } = "";
    public string SourceTitle { get; set; } = "";

    public int Width { get; set; }
    public int Height { get; set; }

    public List<Annotation> Annotations { get; set; } = new();

    /// <summary>Text found in the picture by the OCR of the system (lines with their position).</summary>
    public List<OcrLine> Ocr { get; set; } = new();

    /// <summary>OCR has run (an empty <see cref="Ocr"/> then means "no text", not "not looked at yet").</summary>
    public bool OcrDone { get; set; }
}

/// <summary>The kinds of <see cref="Annotation.Kind"/>.</summary>
public static class AnnotationKind
{
    public const string Box = "box", Ellipse = "ellipse", Arrow = "arrow", Line = "line", Text = "text",
        Callout = "callout", Step = "step", Highlight = "highlight", Redact = "redact", Blur = "blur";

    public static readonly string[] All = { Box, Ellipse, Arrow, Line, Text, Callout, Step, Highlight, Redact, Blur };
}

/// <summary>One shape on a capture. Coordinates are pixels of the original picture.</summary>
public class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>box | ellipse | arrow | line | text | callout | step | highlight | redact | blur</summary>
    public string Kind { get; set; } = AnnotationKind.Box;

    /// <summary>The shape's first corner / the arrow's tail / a callout's box corner.</summary>
    public double X1 { get; set; }
    public double Y1 { get; set; }

    /// <summary>The opposite corner / the arrow's head. For a callout this is the anchor the pointer ends at.</summary>
    public double X2 { get; set; }
    public double Y2 { get; set; }

    /// <summary>"#RRGGBB".</summary>
    public string Color { get; set; } = "#E53935";

    /// <summary>Line width in pixels.</summary>
    public double Width { get; set; } = 4;

    /// <summary>The words of a text / callout / step (also shown beside a box or arrow when set — searchable).</summary>
    public string Text { get; set; } = "";

    public double FontSize { get; set; } = 28;

    /// <summary>A step's number.</summary>
    public int Number { get; set; }

    public Annotation Clone() => (Annotation)MemberwiseClone();

    /// <summary>Kinds that have a text the user can type.</summary>
    public bool HasText => Kind is AnnotationKind.Text or AnnotationKind.Callout or AnnotationKind.Step
        || (Kind is AnnotationKind.Box or AnnotationKind.Ellipse or AnnotationKind.Arrow && Text.Length > 0);

    public double Left => Math.Min(X1, X2);
    public double Top => Math.Min(Y1, Y2);
    public double Right => Math.Max(X1, X2);
    public double Bottom => Math.Max(Y1, Y2);
}

/// <summary>A line of text the OCR found, with its rectangle in picture pixels.</summary>
public class OcrLine
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}
