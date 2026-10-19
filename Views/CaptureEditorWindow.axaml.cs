using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Draws on a screenshot: boxes, arrows, text, callouts, numbered steps, highlights, redaction and blur. The picture is
/// never changed (except by Crop), so every annotation stays editable; changes are saved on their own. The words of the
/// annotations, the title / tags / description and the text found by OCR go into the search index. The AI can edit the
/// annotations from an instruction.
/// </summary>
public partial class CaptureEditorWindow : Window
{
    private static readonly string[] Palette =
        { "#E53935", "#FB8C00", "#FDD835", "#43A047", "#1E88E5", "#8E24AA", "#000000", "#FFFFFF" };

    /// <summary>The tools in toolbar order; <c>Group</c> starts a new group (a separator before it).</summary>
    private static readonly (string Tool, string Label, MaterialIconKind Icon, Key Key, bool Group, string Hint)[] Tools =
    {
        (AnnotationSurface.SelectTool, "Select", MaterialIconKind.CursorDefaultOutline, Key.V, false, "Click a shape to select it, drag to move it, drag a handle to reshape it. Double-click to type. Ctrl+drag (or Ctrl+D) copies. Arrow keys nudge, Delete removes."),
        (AnnotationKind.Box, "Box", MaterialIconKind.RectangleOutline, Key.R, true, "Drag to draw a box. Shift = square."),
        (AnnotationKind.Ellipse, "Ellipse", MaterialIconKind.EllipseOutline, Key.E, false, "Drag to draw an ellipse. Shift = circle."),
        (AnnotationKind.Arrow, "Arrow", MaterialIconKind.ArrowTopRight, Key.A, false, "Drag from the tail to the point. Shift = 45° steps."),
        (AnnotationKind.Line, "Line", MaterialIconKind.VectorLine, Key.L, false, "Drag to draw a line. Shift = 45° steps."),
        (AnnotationKind.Text, "Text", MaterialIconKind.FormatText, Key.T, true, "Click, then type in the panel on the right (Esc goes back to the picture). Drag to set the wrap width."),
        (AnnotationKind.Callout, "Callout", MaterialIconKind.CommentTextOutline, Key.S, false, "Press where the box goes, drag to the thing it points at (the anchor), then type."),
        (AnnotationKind.Step, "Step", MaterialIconKind.Numeric1CircleOutline, Key.N, false, "Click to drop the next numbered marker; add a short label on the right. Stays on for the next step (Esc ends)."),
        (AnnotationKind.Highlight, "Highlight", MaterialIconKind.Marker, Key.H, true, "Drag over text to mark it with a translucent colour. Stays on for the next one (Esc ends)."),
        (AnnotationKind.Redact, "Redact", MaterialIconKind.Square, Key.X, false, "Drag over anything private to cover it with a solid block. Stays on for the next one (Esc ends)."),
        (AnnotationKind.Blur, "Blur", MaterialIconKind.Blur, Key.B, false, "Drag over anything private to pixelate it. Stays on for the next one (Esc ends)."),
        (AnnotationSurface.TextTool, "Grab text", MaterialIconKind.TextRecognition, Key.G, true, "Drag over the text in the picture (or click a line) to copy it. The lines found are outlined."),
        (AnnotationSurface.CropTool, "Crop", MaterialIconKind.Crop, Key.C, false, "Drag the area to keep. This changes the saved picture (Undo brings it back)."),
    };

    /// <summary>Tools used several times in a row (steps 1, 2, 3; every e-mail address): they stay on after a shape.</summary>
    private static bool IsRepeatTool(string tool)
        => tool is AnnotationKind.Step or AnnotationKind.Highlight or AnnotationKind.Redact or AnnotationKind.Blur;

    private static readonly string[] AiExampleList =
    {
        "Redact email addresses, phone numbers and keys",
        "Draw a box around the error message",
        "Number the buttons in order",
        "Highlight every number",
        "Add an arrow to each menu item",
    };

    /// <summary>The picture and annotations at one moment, for undo.</summary>
    private sealed record State(Pixels Image, List<Annotation> Items, List<OcrLine> Ocr, string Signature);

    private App AppRef => (App)Application.Current!;
    private readonly CaptureItem _item;
    private Pixels _pixels;
    private readonly AnnotationSurface _surface = new();
    private readonly Stack<State> _undo = new();
    private readonly Stack<State> _redo = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly Dictionary<string, ToggleButton> _toolButtons = new();
    private readonly List<Button> _swatches = new();
    private CancellationTokenSource? _aiCts;
    private Task? _ocrTask;
    private string _color = Palette[0];
    private bool _loading;
    private bool _dirty;
    private bool _fit = true;
    private bool _editUndoPushed;
    private bool _syncingTools;
    private double _zoom = 1;
    private Annotation? _lastSelected;
    private bool _discarded;

    public Guid CaptureId => _item.Id;

    /// <summary>Only for the XAML loader, which wants a parameterless constructor; use <see cref="CaptureEditorWindow(CaptureItem)"/>.</summary>
    public CaptureEditorWindow() => throw new InvalidOperationException("Open a capture with App.ShowCapture.");

    public CaptureEditorWindow(CaptureItem item)
    {
        InitializeComponent();
        _item = item;
        _pixels = LoadPixels(item);
        // a small picture gets a smaller window (the panel on the right is 310 wide, the bars about 200 high);
        // 900 × 600 still holds the tools on one row and the panel's fields
        Width = Math.Clamp(_pixels.Width + 310 + 60, 900, Width);
        Height = Math.Clamp(_pixels.Height + 210, 600, Height);
        _surface.Items = item.Annotations.Select(a => a.Clone()).ToList();
        _surface.SetImage(_pixels);
        _surface.DrawColor = _color;
        SurfaceHost.Children.Add(_surface);
        _surface.HorizontalAlignment = HorizontalAlignment.Center;
        _surface.VerticalAlignment = VerticalAlignment.Center;

        BuildTools();
        BuildColors();
        foreach (var example in AiExampleList)
        {
            var chip = new Button { Content = example, FontSize = 11, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4), Focusable = false };
            var text = example;
            chip.Click += (_, _) => { AiBox.Text = text; AiBox.Focus(); };
            AiExamples.Children.Add(chip);
        }

        TitleBox.Text = item.Title;
        TagsBox.Text = item.Tags;
        DescriptionBox.Text = item.Description;
        TitleBox.TextChanged += (_, _) => FieldChanged();
        TagsBox.TextChanged += (_, _) => FieldChanged();
        DescriptionBox.TextChanged += (_, _) => FieldChanged();
        AnnotationTextBox.TextChanged += (_, _) => AnnotationTextChanged();
        StepBox.ValueChanged += (_, _) => StepChanged();
        WidthSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) WidthChanged(); };
        FontSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) FontChanged(); };

        _surface.BeforeChange += () => PushUndo();
        _surface.Changed += MarkDirty;
        _surface.SelectionChanged += SelectionChanged;
        _surface.ToolDone += ToolDone;
        _surface.CropRequested += Crop;
        _surface.OcrLines = _item.Ocr;
        _surface.TextGrabbed += GrabbedText;
        _surface.TextRequested += _ => { AnnotationTextBox.Focus(); AnnotationTextBox.SelectAll(); };
        // Esc while typing goes back to the picture, so Delete, the arrow keys and the tool keys work again
        AnnotationTextBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _surface.Focus();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        _autosave.Tick += (_, _) =>
        {
            _autosave.Stop();
            SaveNow();
        };
        Viewer.SizeChanged += (_, _) => { if (_fit) ApplyFit(); CenterHost(); };
        Viewer.AddHandler(PointerWheelChangedEvent, Viewer_Wheel, RoutingStrategies.Tunnel);
        Opened += (_, _) => ApplyFit();
        Closing += (_, _) => { _aiCts?.Cancel(); SaveNow(); };
        Closed += (_, _) =>
        {
            // like notes: only with "AI title and tags" on in Settings, since the text in the picture goes to the AI
            if (AppRef.Config.AiAutoTitle && !_discarded)
                _ = CaptureAi.AutoDescribeAsync(_item.Id).ContinueWith(_ => { }, TaskScheduler.Default);
        };

        Title = "Capture — " + CaptureService.DisplayTitle(item);
        SourceText.Text = (item.SourceApp.Length > 0 ? $"From {item.SourceApp}" + (item.SourceTitle.Length > 0 ? $" — {item.SourceTitle}" : "") + "\n" : "")
                          + $"{item.Created:yyyy-MM-dd HH:mm} · {item.Width} × {item.Height} px";
        CopyTextButton.Click += CopyAllText_Click;
        SelectionChanged();
        SelectTool(AnnotationSurface.SelectTool);
        UpdateUndoButtons();
        UpdateOcrText();
        _ocrTask = RunOcrAsync();
        StatusText.Text = "Saved automatically. The words you add, the title, tags and the text in the picture can all be searched later.";
    }

    private static Pixels LoadPixels(CaptureItem item)
    {
        using var stream = File.OpenRead(CaptureService.ImagePath(item.Id));
        return Pixels.FromImage(stream);
    }

    // ---- toolbar ---------------------------------------------------------------------------

    private void BuildTools()
    {
        foreach (var (tool, label, icon, key, group, hint) in Tools)
        {
            if (group)
                ToolHost.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(4, 0, 8, 0), Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) });
            // icons only, so every tool fits on one row; the hint line under the colours names the active tool
            var button = new ToggleButton
            {
                Classes = { "tool" },
                Content = new MaterialIcon { Kind = icon, Width = 18, Height = 18 },
            };
            Avalonia.Automation.AutomationProperties.SetName(button, label + " tool");
            ToolTip.SetTip(button, $"{label} ({key})\n{hint}");
            var captured = tool;
            // IsCheckedChanged (not Click) so UI Automation and the keyboard select the tool too
            button.IsCheckedChanged += (_, _) =>
            {
                if (_syncingTools) return;
                if (button.IsChecked == true) SelectTool(captured);
                else SelectTool(_surface.Tool); // a tool stays selected: clicking it again keeps it
            };
            _toolButtons[tool] = button;
            ToolHost.Children.Add(button);
        }
    }

    private void SelectTool(string tool)
    {
        _surface.Tool = tool;
        _syncingTools = true;
        foreach (var (key, button) in _toolButtons) button.IsChecked = key == tool;
        _syncingTools = false;
        var info = Tools.First(t => t.Tool == tool);
        HintText.Text = $"{info.Label} ({info.Key}): {info.Hint}";
        ToolTip.SetTip(HintText, HintText.Text); // the full hint when the window is too narrow
        _surface.Cursor = new Cursor(tool == AnnotationSurface.SelectTool ? StandardCursorType.Arrow : StandardCursorType.Cross);
        if (tool != AnnotationSurface.SelectTool) _surface.Select(null);
        if (tool != AnnotationSurface.TextTool) _surface.ClearGrab();
        _surface.OcrLines = _item.Ocr;
        _surface.Refresh();
    }

    /// <summary>After a shape: back to Select, except for the tools used several times in a row.</summary>
    private void ToolDone()
    {
        if (!IsRepeatTool(_surface.Tool))
        {
            SelectTool(AnnotationSurface.SelectTool);
            return;
        }
        // a step stays selected so its label can be typed; the others let go, the next drag makes a new one anyway
        if (_surface.Tool != AnnotationKind.Step) _surface.Select(null);
    }

    private void BuildColors()
    {
        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Focusable = false,
                Tag = hex,
            };
            Avalonia.Automation.AutomationProperties.SetName(swatch, "Colour " + hex);
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => PickColor(hex);
            _swatches.Add(swatch);
            ColorHost.Children.Add(swatch);
        }
        MarkSwatch();
    }

    private void MarkSwatch()
    {
        foreach (var s in _swatches)
            s.BorderBrush = string.Equals((string?)s.Tag, _color, StringComparison.OrdinalIgnoreCase) ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
    }

    private void PickColor(string hex)
    {
        _color = hex;
        _surface.DrawColor = hex;
        MarkSwatch();
        if (_surface.Selected is { } selected)
        {
            PushUndoOnce();
            selected.Color = hex;
            _surface.Refresh();
            MarkDirty();
        }
    }

    private void WidthChanged()
    {
        _surface.StrokeWidth = WidthSlider.Value;
        WidthValue.Text = $"{WidthSlider.Value:0} px";
        if (_loading || _surface.Selected is not { } a) return;
        PushUndoOnce();
        a.Width = WidthSlider.Value;
        _surface.Refresh();
        MarkDirty();
    }

    private void FontChanged()
    {
        _surface.FontSize = FontSlider.Value;
        FontValue.Text = $"{FontSlider.Value:0} px";
        if (_loading || _surface.Selected is not { } a) return;
        PushUndoOnce();
        a.FontSize = FontSlider.Value;
        _surface.Refresh();
        MarkDirty();
    }

    private void StepChanged()
    {
        if (_loading || _surface.Selected is not { Kind: AnnotationKind.Step } a) return;
        PushUndoOnce();
        a.Number = (int)(StepBox.Value ?? 1);
        _surface.Refresh();
        MarkDirty();
    }

    private void AnnotationTextChanged()
    {
        if (_loading || _surface.Selected is not { } a) return;
        PushUndoOnce();
        a.Text = AnnotationTextBox.Text ?? "";
        _surface.Refresh();
        MarkDirty();
    }

    private void FieldChanged()
    {
        // TextChanged also fires (late) for the values set while opening: only a real change counts
        if (_loading || ((TitleBox.Text ?? "").Trim() == _item.Title && (TagsBox.Text ?? "").Trim() == _item.Tags
                         && (DescriptionBox.Text ?? "").Trim() == _item.Description)) return;
        Title = "Capture — " + (string.IsNullOrWhiteSpace(TitleBox.Text) ? CaptureService.DisplayTitle(_item) : TitleBox.Text.Trim());
        MarkDirty();
    }

    /// <summary>The selected annotation's values go into the panel and the colour / size controls.</summary>
    private void SelectionChanged()
    {
        _editUndoPushed = false;
        var a = _surface.Selected;
        // a text that was left empty is invisible: drop it instead of keeping a shape nobody can see
        if (_lastSelected is { Kind: AnnotationKind.Text } left && !ReferenceEquals(left, a) && string.IsNullOrWhiteSpace(left.Text)
            && _surface.Items.Remove(left))
        {
            _surface.Refresh();
            MarkDirty();
        }
        _lastSelected = a;
        DeleteAnnotationButton.IsEnabled = a is not null;
        _loading = true;
        try
        {
            AnnotationTextBox.IsEnabled = a is not null && a.Kind is not (AnnotationKind.Highlight or AnnotationKind.Redact or AnnotationKind.Blur);
            AnnotationTextBox.Text = a?.Text ?? "";
            StepPanel.IsVisible = a?.Kind == AnnotationKind.Step;
            if (a is not null)
            {
                _color = a.Color;
                MarkSwatch();
                WidthSlider.Value = a.Width;
                FontSlider.Value = a.FontSize;
                if (a.Kind == AnnotationKind.Step) StepBox.Value = a.Number;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- undo ------------------------------------------------------------------------------

    private static string Signature(IEnumerable<Annotation> items) => JsonSerializer.Serialize(items);

    private State Snapshot()
        => new(_pixels, _surface.Items.Select(a => a.Clone()).ToList(), _item.Ocr.ToList(), Signature(_surface.Items));

    private void PushUndo()
    {
        var snapshot = Snapshot();
        if (_undo.Count > 0 && _undo.Peek().Signature == snapshot.Signature && ReferenceEquals(_undo.Peek().Image, snapshot.Image)) return;
        _undo.Push(snapshot);
        _redo.Clear();
        UpdateUndoButtons();
    }

    /// <summary>Typing or dragging a slider changes things many times; one undo step covers the whole edit.</summary>
    private void PushUndoOnce()
    {
        if (_editUndoPushed) return;
        _editUndoPushed = true;
        PushUndo();
    }

    private void Restore(State s)
    {
        var imageChanged = !ReferenceEquals(s.Image, _pixels);
        _pixels = s.Image;
        if (imageChanged)
        {
            _surface.SetImage(_pixels);
            _item.Width = _pixels.Width;
            _item.Height = _pixels.Height;
            CaptureService.SaveOriginal(_item.Id, _pixels.ToPng());
            ApplyFit();
        }
        _item.Ocr = s.Ocr.ToList();
        _surface.Items = s.Items.Select(a => a.Clone()).ToList();
        _surface.Select(null);
        _surface.Refresh();
        MarkDirty();
        UpdateUndoButtons();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        var current = Snapshot();
        // entries equal to the present state (a click that changed nothing) are skipped
        while (_undo.Count > 0 && _undo.Peek().Signature == current.Signature && ReferenceEquals(_undo.Peek().Image, current.Image)) _undo.Pop();
        if (_undo.Count == 0)
        {
            UpdateUndoButtons();
            return;
        }
        _redo.Push(current);
        Restore(_undo.Pop());
    }

    private void Redo_Click(object? sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void DeleteAnnotation_Click(object? sender, RoutedEventArgs e)
    {
        if (_surface.Selected is not { } a) return;
        PushUndo();
        _surface.Items.Remove(a);
        _surface.Select(null);
        MarkDirty();
    }

    // ---- crop ------------------------------------------------------------------------------

    private void Crop(PixelRect area)
    {
        PushUndo();
        _pixels = _pixels.Crop(area);
        var dx = -area.X;
        var dy = -area.Y;
        foreach (var a in _surface.Items)
        {
            a.X1 += dx; a.X2 += dx; a.Y1 += dy; a.Y2 += dy;
        }
        _item.Ocr = _item.Ocr
            .Where(l => l.X + l.W > area.X && l.X < area.Right && l.Y + l.H > area.Y && l.Y < area.Bottom)
            .Select(l => new OcrLine { Text = l.Text, X = l.X + dx, Y = l.Y + dy, W = l.W, H = l.H })
            .ToList();
        _item.Width = _pixels.Width;
        _item.Height = _pixels.Height;
        CaptureService.SaveOriginal(_item.Id, _pixels.ToPng());
        _surface.SetImage(_pixels);
        SelectTool(AnnotationSurface.SelectTool);
        ApplyFit();
        MarkDirty();
        UpdateOcrText();
        StatusText.Text = $"Cropped to {_pixels.Width} × {_pixels.Height} px. Undo brings the old picture back.";
    }

    // ---- zoom ------------------------------------------------------------------------------

    private void ApplyFit()
    {
        if (Viewer.Bounds.Width < 50) return;
        var w = Math.Max(40, Viewer.Bounds.Width - 28);
        var h = Math.Max(40, Viewer.Bounds.Height - 28);
        SetZoom(Math.Min(1, Math.Min(w / _pixels.Width, h / _pixels.Height)), fit: true);
    }

    private void SetZoom(double zoom, bool fit)
    {
        _fit = fit;
        _zoom = Math.Clamp(zoom, 0.05, 8);
        _surface.Zoom = _zoom;
        ZoomText.Content = fit ? "Fit" : $"{_zoom * 100:0}%";
        CenterHost();
    }

    /// <summary>A picture smaller than the window sits in the middle.</summary>
    private void CenterHost()
    {
        SurfaceHost.MinWidth = Math.Max(0, Viewer.Bounds.Width - 24);
        SurfaceHost.MinHeight = Math.Max(0, Viewer.Bounds.Height - 24);
    }

    private void ZoomFit_Click(object? sender, RoutedEventArgs e)
    {
        if (_fit && Math.Abs(_zoom - 1) > 0.001) SetZoom(1, fit: false); // a second click shows the real size
        else ApplyFit();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25, fit: false);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25, fit: false);

    private void Viewer_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), fit: false);
        e.Handled = true;
    }

    // ---- saving ----------------------------------------------------------------------------

    private void MarkDirty()
    {
        _dirty = true;
        _autosave.Stop();
        _autosave.Start();
    }

    /// <summary>Writes the annotations and details to the capture's JSON and the annotated picture next to it.</summary>
    private void SaveNow()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            _item.Title = (TitleBox.Text ?? "").Trim();
            _item.Tags = (TagsBox.Text ?? "").Trim();
            _item.Description = (DescriptionBox.Text ?? "").Trim();
            _item.Annotations = _surface.Items.Select(a => a.Clone()).ToList();
            CaptureService.Save(_item);
            if (_item.Annotations.Count > 0)
                CaptureService.SaveAnnotated(_item.Id, AnnotationRenderer.ExportPng(_pixels, _item.Annotations));
            else
                CaptureService.DeleteAnnotated(_item.Id);
            StatusText.Text = $"Saved {DateTime.Now:HH:mm:ss} — {_item.Annotations.Count} annotation(s) indexed for search.";
        }
        catch (Exception ex)
        {
            _dirty = true;
            StatusText.Text = "Could not save: " + ex.Message;
        }
    }

    private byte[] AnnotatedPng() => AnnotationRenderer.ExportPng(_pixels, _surface.Items);

    // ---- OCR -------------------------------------------------------------------------------

    private async Task RunOcrAsync()
    {
        if (_item.OcrDone || !OcrService.IsAvailable || !AppRef.Config.CaptureOcr) return;
        try
        {
            var result = await OcrService.EnsureAsync(_item);
            _item.Ocr = result.Ocr;
            _item.OcrDone = result.OcrDone;
        }
        catch
        {
            // OCR is optional
        }
        UpdateOcrText();
    }

    private async void GrabbedText(string text, int lines)
    {
        if (lines == 0)
        {
            StatusText.Text = _item.Ocr.Count == 0 ? "No text was found in this picture." : "No text there — drag over the outlined lines.";
            return;
        }
        await CopyTextAsync(text, lines);
    }

    private async Task CopyTextAsync(string text, int lines)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            var flat = text.Replace('\n', ' ');
            StatusText.Text = $"Copied {lines} line(s) of text: " + (flat.Length > 80 ? flat[..80] + "…" : flat);
        }
        catch
        {
            StatusText.Text = "Could not copy — another program may be using the clipboard.";
        }
    }

    private async void CopyAllText_Click(object? sender, RoutedEventArgs e)
    {
        var visible = _item.Ocr.Where(l => !CaptureService.IsHidden(l, _surface.Items)).ToList();
        if (visible.Count == 0)
        {
            StatusText.Text = "No text was found in this picture.";
            return;
        }
        await CopyTextAsync(string.Join("\n", visible.Select(l => l.Text)), visible.Count);
    }

    private void UpdateOcrText()
    {
        _surface.OcrLines = _item.Ocr;
        CopyTextButton.IsEnabled = _item.Ocr.Count > 0;
        OcrText.Text = !OcrService.IsAvailable ? "Text in the picture is not read on this system."
            : !_item.OcrDone && !AppRef.Config.CaptureOcr ? "Reading the text in pictures is off (Settings → Capture)."
            : !_item.OcrDone ? "Reading the text in the picture…"
            : _item.Ocr.Count == 0 ? "No text was found in the picture."
            : $"{_item.Ocr.Count} line(s) of text in the picture are searchable.";
    }

    // ---- AI --------------------------------------------------------------------------------

    private CaptureItem InfoItem()
    {
        _item.Title = (TitleBox.Text ?? "").Trim();
        return _item;
    }

    private async void AiEdit_Click(object? sender, RoutedEventArgs e)
    {
        var instruction = (AiBox.Text ?? "").Trim();
        if (instruction.Length == 0)
        {
            AiStatus.Text = "Say what the AI should do first.";
            AiBox.Focus();
            return;
        }
        await RunAiAsync(async token =>
        {
            if (_ocrTask is { IsCompleted: false })
            {
                AiStatus.Text = "Reading the text in the picture first…";
                await _ocrTask;
            }
            AiStatus.Text = AiService.Asking("edit the annotations");
            var info = CaptureAi.PictureInfo(InfoItem(), _surface.Items);
            var result = await AiService.AskAsync(AiPrompts.EditCapture(instruction, info), token);
            if (!result.Ok)
            {
                AiStatus.Text = AiService.Failed(result);
                return;
            }
            var before = Snapshot();
            var change = CaptureAi.Apply(result.Text, _item, _surface.Items, _color);
            if (change is null)
            {
                AiStatus.Text = "The AI's answer wasn't a list of changes — try rephrasing.";
                return;
            }
            var changed = change.Added + change.Updated + change.Deleted;
            if (changed > 0)
            {
                _undo.Push(before);
                _redo.Clear();
                UpdateUndoButtons();
                _surface.Select(null);
                _surface.Refresh();
            }
            ApplyDescriptionFields(change.Title, change.Tags, change.Description);
            if (changed > 0 || change.Title is not null || change.Tags.Count > 0 || change.Description is not null) MarkDirty();
            AiStatus.Text = "✨ " + (change.Summary.Length > 0 ? change.Summary + " " : "") +
                            (changed > 0 ? $"(added {change.Added}, changed {change.Updated}, removed {change.Deleted} — Ctrl+Z undoes it)" : "(nothing changed)")
                            + (!_item.OcrDone || _item.Ocr.Count == 0 ? "\nThe AI could not read any text from the picture, so it can only guess positions." : "");
        });
    }

    private async void AiDescribe_Click(object? sender, RoutedEventArgs e)
    {
        await RunAiAsync(async token =>
        {
            if (_ocrTask is { IsCompleted: false })
            {
                AiStatus.Text = "Reading the text in the picture first…";
                await _ocrTask;
            }
            AiStatus.Text = AiService.Asking("describe the picture");
            var result = await AiService.AskAsync(AiPrompts.DescribeCapture(CaptureAi.PictureInfo(InfoItem(), _surface.Items)), token);
            if (!result.Ok)
            {
                AiStatus.Text = AiService.Failed(result);
                return;
            }
            var probe = new CaptureItem { Title = _item.Title, Tags = (TagsBox.Text ?? "").Trim() };
            if (!CaptureAi.ApplyDescription(result.Text, probe))
            {
                AiStatus.Text = "The AI's answer wasn't a title and tags — try again.";
                return;
            }
            ApplyDescriptionFields(probe.Title, probe.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(), probe.Description);
            MarkDirty();
            AiStatus.Text = "✨ Title, tags and description filled in — edit them if you like.";
        });
    }

    /// <summary>Fills the title only when empty, merges tags, replaces the description.</summary>
    private void ApplyDescriptionFields(string? title, IReadOnlyList<string> tags, string? description)
    {
        _loading = true;
        try
        {
            if (title is not null && string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Text = title;
            if (tags.Count > 0) TagsBox.Text = CaptureAi.MergeTags(TagsBox.Text ?? "", tags);
            if (description is not null) DescriptionBox.Text = description;
        }
        finally
        {
            _loading = false;
        }
        Title = "Capture — " + CaptureService.DisplayTitle(new CaptureItem { Title = (TitleBox.Text ?? "").Trim(), SourceTitle = _item.SourceTitle, Created = _item.Created });
    }

    private async Task RunAiAsync(Func<CancellationToken, Task> work)
    {
        _aiCts?.Cancel();
        var cts = _aiCts = new CancellationTokenSource();
        AiApplyButton.IsEnabled = false;
        AiDescribeButton.IsEnabled = false;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            AiStatus.Text = "Stopped.";
        }
        finally
        {
            AiApplyButton.IsEnabled = true;
            AiDescribeButton.IsEnabled = true;
        }
    }

    // ---- actions ---------------------------------------------------------------------------

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = ClipboardImageService.TrySetImage(AnnotatedPng())
            ? "Copied the picture to the clipboard."
            : "Could not copy — another program may be using the clipboard.";
    }

    private async void SaveAs_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save the picture",
                SuggestedFileName = SafeFileName(CaptureService.DisplayTitle(_item)) + ".png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("PNG picture") { Patterns = new[] { "*.png" } } },
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            var png = AnnotatedPng();
            await stream.WriteAsync(png);
            StatusText.Text = "Saved " + file.Name;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
        }
    }

    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "capture" : clean.Length > 60 ? clean[..60] : clean;
    }

    /// <summary>A new note holding the annotated picture, the description and the annotation words.</summary>
    private void Note_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveNow();
            var src = NotesService.AddImage(AnnotatedPng(), ".png");
            var html = new System.Text.StringBuilder();
            var description = (DescriptionBox.Text ?? "").Trim();
            if (description.Length > 0) html.Append("<p>").Append(WebUtility.HtmlEncode(description)).Append("</p>\n");
            html.Append("<p><img src=\"").Append(src).Append("\"></p>\n");
            var words = _surface.Items.Where(a => a.Text.Length > 0).Select(a => a.Text.Replace('\n', ' ')).ToList();
            if (words.Count > 0)
            {
                html.Append("<ul>\n");
                foreach (var w in words) html.Append("<li>").Append(WebUtility.HtmlEncode(w)).Append("</li>\n");
                html.Append("</ul>\n");
            }
            var note = new NoteItem
            {
                Title = CaptureService.DisplayTitle(new CaptureItem { Title = (TitleBox.Text ?? "").Trim(), SourceTitle = _item.SourceTitle, Created = _item.Created }),
                Tags = App.MergeTags("capture", (TagsBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#'))),
                Html = html.ToString(),
            };
            AppRef.AddNote(note);
            AppRef.ShowNote(note.Id);
            StatusText.Text = "Made a note with the picture.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not make the note: " + ex.Message;
        }
    }

    private async void DeleteCapture_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.Confirm(this, "Delete capture", "Delete this capture and its annotations? Notes that already contain the picture keep their own copy.", "Delete")) return;
        AppRef.DeleteCapture(_item.Id);
    }

    /// <summary>Closes without saving: the capture was deleted.</summary>
    public void Discard()
    {
        _discarded = true;
        _dirty = false;
        _autosave.Stop();
        _aiCts?.Cancel();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var inText = FocusManager?.GetFocusedElement() is TextBox;
        if (ctrl && e.Key == Key.Z && !inText)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo_Click(null, new RoutedEventArgs());
            else Undo_Click(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y && !inText)
        {
            Redo_Click(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C && !inText)
        {
            Copy_Click(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !inText)
        {
            if (_surface.Tool != AnnotationSurface.SelectTool) SelectTool(AnnotationSurface.SelectTool);
            else _surface.Select(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0)
        {
            ApplyFit();
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add)
        {
            ZoomIn_Click(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract)
        {
            ZoomOut_Click(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && !inText && !e.Handled && Tools.FirstOrDefault(t => t.Key == e.Key) is { Tool: not null } tool)
        {
            SelectTool(tool.Tool);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
