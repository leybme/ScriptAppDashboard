using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// A chart in the visual editor: a title, the chart type, the drawn chart and (on demand) its data as an editable table.
/// </summary>
internal sealed class NoteChartView : GridBlockView
{
    private static readonly string[] Types = { "bar", "line", "pie" };

    private readonly NoteBlock _block;
    private readonly TextBox _title;
    private readonly ComboBox _type;
    private readonly Button _dataButton;
    private readonly ChartCanvas _canvas;
    private readonly NoteTableView _data;

    public NoteChartView(NoteBlock block, bool showData)
    {
        _block = block;
        _title = new TextBox
        {
            Classes = { "blk", "stamp" },
            Text = block.Text,
            Watermark = "Chart title",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_title, "Chart title");
        _type = new ComboBox
        {
            ItemsSource = new[] { "Bar chart", "Line chart", "Pie chart" },
            SelectedIndex = Math.Max(0, Array.IndexOf(Types, NoteDocument.ChartTypeName(block.ChartType))),
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 120,
        };
        AutomationProperties.SetName(_type, "Chart type");
        ToolTip.SetTip(_type, "Chart type");
        _dataButton = new Button { Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(_dataButton, "Chart data");
        ToolTip.SetTip(_dataButton, "Show or hide the numbers the chart is drawn from: labels in the first column, one series per column");
        Grid.SetColumn(_type, 1);
        Grid.SetColumn(_dataButton, 2);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { _title, _type, _dataButton } };

        _canvas = new ChartCanvas(block) { Height = 280, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(_canvas, "Chart");
        _data = new NoteTableView(block, chartData: true) { Margin = new Thickness(0, 8, 0, 0) };
        var delete = new MenuItem { Header = "Delete chart" };
        delete.Click += (_, _) => RaiseDelete();
        var data = new MenuItem { Header = "Show or hide the data" };
        data.Click += (_, _) => ShowData(!_data.IsVisible);
        _canvas.ContextMenu = new ContextMenu { Items = { data, delete } };

        _data.Changed += () =>
        {
            _canvas.Refresh();
            RaiseChanged();
        };
        _data.Exit = direction => direction < 0 ? FocusTitle() : Exit?.Invoke(direction) ?? false;
        ShowData(showData);

        _title.TextChanged += (_, _) =>
        {
            var text = _title.Text ?? "";
            if (text == _block.Text) return;
            _block.Text = text;
            RaiseChanged();
        };
        _title.AddHandler(KeyDownEvent, Title_KeyDown, RoutingStrategies.Tunnel);
        _type.SelectionChanged += (_, _) =>
        {
            var type = Types[Math.Clamp(_type.SelectedIndex, 0, Types.Length - 1)];
            if (type == _block.ChartType) return;
            _block.ChartType = type;
            _canvas.Refresh();
            RaiseChanged();
        };
        _dataButton.Click += (_, _) => ShowData(!_data.IsVisible);

        Child = new StackPanel { Children = { header, _canvas, _data } };
    }

    private void ShowData(bool show)
    {
        _data.IsVisible = show;
        _dataButton.Content = show ? "Hide data" : "Edit data";
    }

    private bool FocusTitle()
    {
        _title.Focus();
        _title.CaretIndex = _title.Text?.Length ?? 0;
        return true;
    }

    private void Title_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Up:
                e.Handled = Exit?.Invoke(-1) ?? false;
                break;
            case Key.Down or Key.Enter:
                if (_data.IsVisible) _data.FocusEdge(atEnd: false);
                else Exit?.Invoke(1);
                e.Handled = true;
                break;
        }
    }

    public override void FocusEdge(bool atEnd)
    {
        if (atEnd && _data.IsVisible) _data.FocusEdge(atEnd: true);
        else FocusTitle();
    }

    public override TextTarget? ActiveTarget()
    {
        if (!_title.IsFocused) return _data.ActiveTarget();
        return new TextTarget(_title, false, text =>
        {
            _block.Text = text;
            _title.Text = text;
            RaiseChanged();
        }, (s, e) =>
        {
            _title.Focus();
            _title.SelectionStart = s;
            _title.SelectionEnd = e;
            _title.CaretIndex = e;
        });
    }
}

/// <summary>Draws a chart block (see <see cref="ChartLayout"/>) in the theme's colours, with a tooltip for the column or slice under the pointer.</summary>
internal sealed class ChartCanvas : Control
{
    private readonly NoteBlock _block;
    private readonly Dictionary<string, IBrush> _brushes = new();
    private ChartLayout? _layout;
    private Size _layoutSize;
    private bool _layoutDark;
    private ChartHover? _hover;

    public ChartCanvas(NoteBlock block)
    {
        _block = block;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Redraws after the data or the type changed.</summary>
    public void Refresh()
    {
        _layout = null;
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    private IBrush Brush(string token, bool dark)
    {
        var key = (dark ? "d:" : "l:") + token;
        if (!_brushes.TryGetValue(key, out var brush))
            _brushes[key] = brush = new SolidColorBrush(Color.Parse(ChartLayout.Color(token, dark)));
        return brush;
    }

    private ChartLayout Layout()
    {
        if (_layout is null || _layoutSize != Bounds.Size)
        {
            _layout = ChartLayout.Build(_block, Bounds.Width, Bounds.Height);
            _layoutSize = Bounds.Size;
        }
        return _layout;
    }

    public override void Render(DrawingContext context)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        if (dark != _layoutDark) _brushes.Clear();
        _layoutDark = dark;
        var bounds = new Rect(Bounds.Size).Deflate(0.5);
        context.DrawRectangle(Brush("surface", dark), new Pen(Brush("border", dark)), new RoundedRect(bounds, 6));
        var layout = Layout();

        if (_hover is { H: > 0 } hover)
        {
            if (hover.W > 0) context.FillRectangle(Brush("hover", dark), new Rect(hover.X, hover.Y, hover.W, hover.H));
            else context.DrawLine(new Pen(Brush("axis", dark)), new Point(Math.Round(hover.X) + 0.5, hover.Y), new Point(Math.Round(hover.X) + 0.5, hover.Y + hover.H));
        }

        foreach (var s in layout.Shapes)
        {
            switch (s.Kind)
            {
                case ShapeKind.Fill:
                    var gap = s.Gap ? new Pen(Brush("surface", dark), 2, lineJoin: PenLineJoin.Round) : null;
                    context.DrawGeometry(Brush(s.Color, dark), gap, StreamGeometry.Parse(s.Path));
                    break;
                case ShapeKind.Stroke:
                    var pen = new Pen(Brush(s.Color, dark), s.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                    context.DrawGeometry(null, pen, StreamGeometry.Parse(s.Path));
                    break;
                case ShapeKind.Dot:
                    context.DrawEllipse(Brush(s.Color, dark), new Pen(Brush("surface", dark), 2), new Point(s.P![0], s.P[1]), s.P[2], s.P[2]);
                    break;
                case ShapeKind.Text:
                    var text = new FormattedText(s.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default, FontStyle.Normal, s.Bold ? FontWeight.SemiBold : FontWeight.Normal), s.Size, Brush(s.Color, dark));
                    var x = s.Align < 0 ? s.P![0] : s.Align == 0 ? s.P![0] - text.Width / 2 : s.P![0] - text.Width;
                    context.DrawText(text, new Point(x, s.P[1] - text.Height / 2));
                    break;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var hover = Layout().HoverAt(p.X, p.Y);
        if (hover == _hover) return;
        var tipChanged = hover?.Tip != _hover?.Tip;
        _hover = hover;
        InvalidateVisual();
        if (!tipChanged) return;
        // reopen, so the tooltip moves to the pointer
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, hover?.Tip);
        if (hover is not null) ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }
}
