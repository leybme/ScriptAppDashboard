using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Visual (WYSIWYG) note editor, one row per block. The block being edited shows its inline markup
/// (**bold**, *italic*, `code`, [link](url)) in a TextBox; every other block is rendered with real formatting.
/// Enter, Backspace, Delete and the arrow keys move between blocks like a word processor.
/// </summary>
public partial class NoteEditor : UserControl
{
    private sealed class Row
    {
        public required NoteBlock Block;
        public required Border Frame;
        public required Panel MarkerHost;
        public required TextBlock Marker;
        public required TextBox Stamp;
        public required TextBlock View;
        public required TextBox Edit;
        public required Border Rule;
        public required Image Picture;
        public required Panel Content;
        public GridBlockView? Grid;
        public Bitmap? Bitmap;
        public List<InlineSpan> Spans = new();
        public bool Editing;
    }

    private readonly List<Row> _rows = new();
    private Row? _active;

    internal static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    internal static readonly IBrush CodeBack = new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80));
    internal static readonly IBrush Line = new SolidColorBrush(Color.FromArgb(0x90, 0x80, 0x80, 0x80));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly FontFamily Mono = new("Consolas,Menlo,monospace");
    private static readonly string[] Bullets = { "•", "◦", "▪" };
    private const double IndentWidth = 22;

    /// <summary>Raised after every edit (text or structure).</summary>
    public event Action? Changed;

    public string Placeholder { get; set; } = "Start writing…  Type - for a list, # for a heading, > for a quote, ``` for code";

    public NoteEditor()
    {
        InitializeComponent();
        Filler.PointerPressed += Filler_PointerPressed;
        AddHandler(PointerPressedEvent, (_, e) => Editor_PointerPressed(e), RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) => Editor_PointerMoved(e), RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _dragAnchor = -1, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DetachedFromVisualTree += (_, _) => DisposePictures();
        Load("");
    }

    // ---- public API -----------------------------------------------------------

    /// <summary>Shows a note body. Does not raise <see cref="Changed"/>.</summary>
    public void Load(string html)
    {
        DisposePictures();
        _rows.Clear();
        BlockHost.Children.Clear();
        _active = null;
        var blocks = NoteDocument.Parse(html);
        if (blocks.Count == 0) blocks.Add(new NoteBlock());
        foreach (var block in blocks) AddRow(_rows.Count, block);
        RefreshLists();
        Scroller.Offset = default;
    }

    public string ToHtml() => NoteDocument.ToHtml(_rows.Select(r => r.Block).ToList());

    /// <summary>Puts the caret at the start of the first block.</summary>
    public void FocusStart()
    {
        var row = _rows.FirstOrDefault(r => !r.Block.IsAtomic);
        if (row is not null) FocusRow(row, 0);
    }

    /// <summary>Wraps the selection in an emphasis marker (or removes it), or inserts a placeholder.</summary>
    public void ToggleInline(string marker, string placeholder)
    {
        if (ActiveTarget() is not { Markup: true } target) return;
        var text = target.Box.Text ?? "";
        var (s, e) = Selection(target.Box);
        var ch = marker[0];
        int before = 0, after = 0;
        while (s - before > 0 && text[s - before - 1] == ch) before++;
        while (e + after < text.Length && text[e + after] == ch) after++;
        // "**" is on when 2 or 3 stars surround the selection, "*" when 1 or 3 do
        var on = marker.Length == 2 ? before >= 2 && after >= 2 : before % 2 == 1 && after % 2 == 1;
        if (s < e && on)
        {
            target.Set(text[..(s - marker.Length)] + text[s..e] + text[(e + marker.Length)..]);
            target.Select(s - marker.Length, e - marker.Length);
        }
        else
        {
            var inner = s < e ? text[s..e] : placeholder;
            target.Set(text[..s] + marker + inner + marker + text[e..]);
            target.Select(s + marker.Length, s + marker.Length + inner.Length);
        }
        Changed?.Invoke();
    }

    /// <summary>Turns the selection into a link and selects the address (or inserts a sample link).</summary>
    public void InsertLink()
    {
        if (ActiveTarget() is not { Markup: true } target) return;
        var text = target.Box.Text ?? "";
        var (s, e) = Selection(target.Box);
        const string url = "https://";
        if (s < e)
        {
            var label = text[s..e];
            target.Set(text[..s] + "[" + label + "](" + url + ")" + text[e..]);
            var at = s + label.Length + 3;
            target.Select(at, at + url.Length);
        }
        else
        {
            const string label = "link text";
            target.Set(text[..s] + "[" + label + "](" + url + ")" + text[e..]);
            target.Select(s + 1, s + 1 + label.Length);
        }
        Changed?.Invoke();
    }

    /// <summary>Changes the current block to <paramref name="kind"/>, or back to a paragraph if it already is one.</summary>
    public void ToggleBlock(BlockKind kind)
    {
        var row = ActiveRow();
        if (row is null || row.Block.Kind == BlockKind.Raw || row.Block.IsGrid) return;
        ChangeKind(row, row.Block.Kind == kind ? BlockKind.Paragraph : kind);
    }

    /// <summary>Paragraph → Heading 1 → Heading 2 → Heading 3 → paragraph.</summary>
    public void CycleHeading()
    {
        var row = ActiveRow();
        if (row is null || row.Block.Kind == BlockKind.Raw || row.Block.IsGrid) return;
        var b = row.Block;
        if (b.Kind != BlockKind.Heading) ChangeKind(row, BlockKind.Heading, 1);
        else if (b.Level < 3) ChangeKind(row, BlockKind.Heading, b.Level + 1);
        else ChangeKind(row, BlockKind.Paragraph);
    }

    /// <summary>Inline code for a selection; otherwise toggles a code block.</summary>
    public void CodeButton()
    {
        if (ActiveTarget() is not { } target) return;
        var (s, e) = Selection(target.Box);
        if (s < e && target.Markup) ToggleInline("`", "code");
        else ToggleBlock(BlockKind.Code);
    }

    /// <summary>Types <paramref name="text"/> at the caret, replacing any selection.</summary>
    public void InsertText(string text)
    {
        if (ActiveTarget() is not { } target) return;
        var current = target.Box.Text ?? "";
        var (s, e) = Selection(target.Box);
        target.Set(current[..s] + text + current[e..]);
        target.Select(s + text.Length, s + text.Length);
        Changed?.Invoke();
    }

    /// <summary>Adds an empty table below the current block and puts the caret in its first cell.</summary>
    public void InsertTable() => InsertGrid(NoteDocument.NewTable());

    /// <summary>Adds a chart with sample data below the current block, with its data shown for editing.</summary>
    public void InsertChart() => InsertGrid(NoteDocument.NewChart());

    /// <summary>Adds a table or chart after the current block (in place of an empty paragraph), with a paragraph below it.</summary>
    private void InsertGrid(NoteBlock block)
    {
        var current = _active is not null && _rows.Contains(_active) ? _active : null;
        var index = current is null ? _rows.Count : _rows.IndexOf(current) + 1;
        var row = AddRow(index, block, showData: true);
        if (current is { Block: { Kind: BlockKind.Paragraph, Text.Length: 0 } }) RemoveRow(current);
        var i = _rows.IndexOf(row);
        if (i == _rows.Count - 1 || _rows[i + 1].Block.IsGrid) AddRow(i + 1, new NoteBlock());
        RefreshLists();
        FocusRow(row, 0);
        Changed?.Invoke();
    }

    /// <summary>A block and the text selected in it, taken before a menu or dialog moves the focus away.</summary>
    public sealed record EditorSelection(object Block, int Start, int End, string Text, string BlockText);

    /// <summary>The block last edited and its selection (<see cref="EditorSelection.Text"/> is empty when nothing is selected).</summary>
    public EditorSelection? CaptureSelection()
    {
        if (_active is not { } row || !_rows.Contains(row) || row.Block.IsAtomic) return null;
        if (row.Block.IsGrid) return new EditorSelection(row, 0, 0, "", "");
        var text = row.Edit.Text ?? "";
        var (s, e) = row.Editing ? Selection(row.Edit) : (text.Length, text.Length);
        return new EditorSelection(row, s, e, text[s..e], text);
    }

    /// <summary>Replaces a captured selection; several lines become several blocks. False when the block was removed
    /// or the selected text is no longer there.</summary>
    public bool ReplaceSelection(EditorSelection selection, string text)
    {
        if (selection.Block is not Row row || !_rows.Contains(row) || selection.Text.Length == 0) return false;
        var current = row.Edit.Text ?? "";
        var start = selection.Start;
        if (start + selection.Text.Length > current.Length || string.CompareOrdinal(current, start, selection.Text, 0, selection.Text.Length) != 0)
        {
            start = current.IndexOf(selection.Text, StringComparison.Ordinal); // edited since: find it again
            if (start < 0) return false;
        }
        var end = start + selection.Text.Length;
        if (!row.Block.HasMarkup)
        {
            SetText(row, current[..start] + text + current[end..]);
            FocusRow(row, start + text.Length);
            Changed?.Invoke();
            return true;
        }
        FocusRow(row, start, end);
        InsertPlain(row, text.Trim());
        return true;
    }

    /// <summary>Inserts blocks after the captured block, at the start, or at the end of the note.</summary>
    public void InsertBlocks(IReadOnlyList<NoteBlock> blocks, EditorSelection? after = null, bool atStart = false)
    {
        if (blocks.Count == 0) return;
        var index = atStart ? 0 : after?.Block is Row r && _rows.Contains(r) ? _rows.IndexOf(r) + 1 : _rows.Count;
        var emptyOnly = _rows.Count == 1 && _rows[0].Block.Kind == BlockKind.Paragraph && _rows[0].Block.Text.Length == 0 ? _rows[0] : null;
        Row? last = null;
        foreach (var block in blocks) last = AddRow(index++, block);
        if (emptyOnly is not null) RemoveRow(emptyOnly);
        RefreshLists();
        if (last is not null && !last.Block.IsAtomic) FocusRow(last, last.Block.Text.Length);
        Changed?.Invoke();
    }

    // ---- rows -------------------------------------------------------------------

    private Row? ActiveRow()
    {
        if (_active is not null && _rows.Contains(_active) && !_active.Block.IsAtomic) return _active;
        return _rows.FirstOrDefault(r => !r.Block.IsAtomic && !r.Block.IsGrid);
    }

    /// <summary>The text box the toolbar acts on: the current block's, or the current cell of a table or chart.</summary>
    private TextTarget? ActiveTarget()
    {
        var row = ActiveRow();
        if (row is null) return null;
        if (row.Grid is { } grid) return grid.ActiveTarget();
        return new TextTarget(row.Edit, row.Block.HasMarkup, text => SetText(row, text), (s, e) => FocusRow(row, s, e));
    }

    private Row CreateRow(NoteBlock block)
    {
        var marker = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var stamp = new TextBox
        {
            Classes = { "blk", "stamp" },
            FontSize = 12,
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            Watermark = "time",
            Text = block.Stamp,
        };
        AutomationProperties.SetName(stamp, "Time stamp");
        var markerHost = new Panel { Children = { marker, stamp } };
        var view = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        var edit = new TextBox { Classes = { "blk" }, Text = block.Text };
        var rule = new Border { Height = 1, Background = Line, Margin = new Thickness(0, 8), IsVisible = false };
        var picture = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var content = new Panel { Children = { view, edit, rule, picture } };
        Grid.SetColumn(content, 1);
        var frame = new Border
        {
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { markerHost, content } },
        };

        var row = new Row
        {
            Block = block, Frame = frame, MarkerHost = markerHost, Marker = marker, Stamp = stamp,
            View = view, Edit = edit, Rule = rule, Picture = picture, Content = content,
        };
        picture.DoubleTapped += (_, _) => OpenPicture(row);
        picture.ContextMenu = PictureMenu(row);
        edit.AddHandler(KeyDownEvent, (_, e) => Edit_KeyDown(row, e), RoutingStrategies.Tunnel);
        edit.TextChanged += (_, _) => Edit_TextChanged(row);
        edit.GotFocus += (_, _) =>
        {
            _active = row;
            row.Editing = true;
        };
        edit.LostFocus += (_, _) => EndEdit(row);
        edit.PastingFromClipboard += (_, e) => Edit_Paste(row, e);
        edit.ContextFlyout = null;
        edit.ContextMenu = TextMenu(row);
        view.PointerPressed += (_, e) => View_PointerPressed(row, e);
        stamp.TextChanged += (_, _) =>
        {
            var value = stamp.Text ?? "";
            if (value == row.Block.Stamp) return;
            row.Block.Stamp = value;
            Changed?.Invoke();
        };
        return row;
    }

    private Row AddRow(int index, NoteBlock block, bool showData = false)
    {
        var row = CreateRow(block);
        _rows.Insert(index, row);
        BlockHost.Children.Insert(index, row.Frame);
        if (block.IsGrid) AddGrid(row, showData);
        ApplyKind(row);
        return row;
    }

    /// <summary>Gives a table or chart row its view and connects it to the editor.</summary>
    private void AddGrid(Row row, bool showData)
    {
        GridBlockView grid;
        if (row.Block.Kind == BlockKind.Table)
        {
            var table = new NoteTableView(row.Block);
            table.ChartRequested += () =>
            {
                if (!_rows.Contains(row)) return;
                var chart = AddRow(_rows.IndexOf(row) + 1, NoteDocument.ChartFromTable(row.Block));
                RefreshLists();
                FocusRow(chart, 0);
                Changed?.Invoke();
            };
            grid = table;
        }
        else
        {
            grid = new NoteChartView(row.Block, showData);
        }
        grid.Changed += () => Changed?.Invoke();
        grid.Exit = direction => _rows.Contains(row) && LeaveGrid(row, direction);
        grid.DeleteRequested += () =>
        {
            if (!_rows.Contains(row)) return;
            var index = _rows.IndexOf(row);
            RemoveRow(row);
            RefreshLists();
            var near = _rows.ElementAtOrDefault(Math.Min(index, _rows.Count - 1));
            if (near is not null && !near.Block.IsAtomic) FocusRow(near, 0);
            Changed?.Invoke();
        };
        grid.AddHandler(GotFocusEvent, (_, _) => _active = row);
        row.Grid = grid;
        row.Content.Children.Add(grid);
    }

    /// <summary>The caret leaves a table or chart; below the last block a new paragraph is started.</summary>
    private bool LeaveGrid(Row row, int direction)
    {
        if (Move(row, direction, atEnd: direction < 0)) return true;
        if (direction < 0) return false;
        var next = AddRow(_rows.IndexOf(row) + 1, new NoteBlock());
        RefreshLists();
        FocusRow(next, 0);
        Changed?.Invoke();
        return true;
    }

    private void RemoveRow(Row row)
    {
        row.Bitmap?.Dispose();
        row.Bitmap = null;
        _rows.Remove(row);
        BlockHost.Children.Remove(row.Frame);
        if (_active == row) _active = null;
        if (_rows.Count == 0) AddRow(0, new NoteBlock());
    }

    private static string KindName(NoteBlock b) => b.Kind switch
    {
        BlockKind.Heading => $"Heading {b.Level}",
        BlockKind.Bullet => "Bullet item",
        BlockKind.Number => "Numbered item",
        BlockKind.Timed => "Time bullet",
        BlockKind.Quote => "Quote",
        BlockKind.Code => "Code block",
        BlockKind.Raw => "HTML block",
        BlockKind.Divider => "Divider",
        BlockKind.Image => "Picture",
        BlockKind.Table => "Table",
        BlockKind.Chart => "Chart",
        _ => "Paragraph",
    };

    /// <summary>Styles a row for its block kind (font, frame, marker) without rebuilding it, so focus and undo survive.</summary>
    private void ApplyKind(Row row)
    {
        var b = row.Block;
        var heading = b.Kind == BlockKind.Heading;
        var mono = b.Kind is BlockKind.Code or BlockKind.Raw;
        double size = mono ? 13 : heading ? b.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 } : 14;

        row.View.FontSize = row.Edit.FontSize = size;
        row.View.FontWeight = row.Edit.FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal;
        row.View.FontStyle = row.Edit.FontStyle = b.Kind == BlockKind.Quote ? FontStyle.Italic : FontStyle.Normal;
        row.View.MinHeight = row.Edit.MinHeight = Math.Ceiling(size * 1.35);
        if (mono) row.Edit.FontFamily = Mono;
        else row.Edit.ClearValue(TextBox.FontFamilyProperty);
        row.Edit.AcceptsTab = mono;
        row.Edit.TextWrapping = mono ? TextWrapping.NoWrap : TextWrapping.Wrap;
        if (b.Kind == BlockKind.Raw) row.Edit.Foreground = Muted;
        else row.Edit.ClearValue(TextBox.ForegroundProperty);
        AutomationProperties.SetName(row.Edit, KindName(b));

        var f = row.Frame;
        f.ClearValue(Border.BackgroundProperty);
        f.ClearValue(Border.BorderBrushProperty);
        f.BorderThickness = default;
        f.CornerRadius = default;
        f.Padding = default;
        switch (b.Kind)
        {
            case BlockKind.Heading:
                f.Margin = new Thickness(0, b.Level <= 1 ? 14 : 10, 0, 2);
                break;
            case BlockKind.Code:
                f.Margin = new Thickness(0, 4);
                f.Padding = new Thickness(10, 8);
                f.Background = CodeBack;
                f.CornerRadius = new CornerRadius(4);
                break;
            case BlockKind.Table or BlockKind.Chart:
                f.Margin = new Thickness(0, 8);
                break;
            case BlockKind.Raw:
                f.Margin = new Thickness(0, 4);
                f.Padding = new Thickness(8, 6);
                f.BorderBrush = Line;
                f.BorderThickness = new Thickness(1);
                f.CornerRadius = new CornerRadius(4);
                break;
            case BlockKind.Quote:
                f.Margin = new Thickness(0, 1);
                f.Padding = new Thickness(12, 2, 0, 2);
                f.BorderBrush = Line;
                f.BorderThickness = new Thickness(3, 0, 0, 0);
                break;
            default:
                f.Margin = new Thickness(0, b.IsList ? 1 : 3);
                break;
        }

        row.MarkerHost.IsVisible = b.IsList || b.Kind == BlockKind.Raw;
        row.MarkerHost.MinWidth = b.Kind is BlockKind.Bullet or BlockKind.Number ? IndentWidth + 4 : 0;
        row.Marker.IsVisible = b.Kind != BlockKind.Timed;
        row.Stamp.IsVisible = b.Kind == BlockKind.Timed;
        row.Marker.FontSize = b.Kind == BlockKind.Raw ? 10 : 14;
        if (b.Kind == BlockKind.Raw)
        {
            row.Marker.Text = "HTML";
            row.Marker.Foreground = Muted;
        }

        var divider = b.Kind == BlockKind.Divider;
        row.Rule.IsVisible = divider;
        row.Picture.IsVisible = b.Kind == BlockKind.Image;
        if (row.Grid is not null) row.Grid.IsVisible = b.IsGrid;
        if (b.Kind == BlockKind.Image)
        {
            row.View.IsVisible = row.Edit.IsVisible = false;
            ShowPicture(row);
        }
        else if (divider || b.IsGrid)
        {
            row.View.IsVisible = row.Edit.IsVisible = false;
        }
        else if (!b.HasMarkup)
        {
            row.View.IsVisible = false;
            row.Edit.IsVisible = true;
        }
        else
        {
            row.Edit.IsVisible = row.Editing;
            row.View.IsVisible = !row.Editing;
            Render(row);
        }
    }

    /// <summary>Numbers, bullet glyphs and indents (they depend on the neighbouring blocks).</summary>
    private void RefreshLists()
    {
        var (numbers, depths) = NoteDocument.ListInfo(_rows.Select(r => r.Block).ToList());
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var b = row.Block;
            if (!b.IsList) continue;
            b.Indent = depths[i];
            row.MarkerHost.Margin = new Thickness(depths[i] * IndentWidth, 0, 0, 0);
            row.Marker.Text = b.Kind == BlockKind.Number ? numbers[i] + "." : Bullets[depths[i] % Bullets.Length];
        }
        var only = _rows.Count == 1 ? _rows[0] : null;
        foreach (var row in _rows) row.Edit.Watermark = row == only && row.Block.Kind == BlockKind.Paragraph ? Placeholder : null;
        if (only is not null && !only.Editing && only.Block.HasMarkup) Render(only);
    }

    private void Render(Row row)
    {
        row.Spans = RenderInlines(row.View, row.Block.Text);
        if (row.Spans.Count == 0 && _rows.Count == 1 && row.Block.Kind == BlockKind.Paragraph)
            row.View.Inlines!.Add(new Run(Placeholder) { Foreground = Muted });
    }

    /// <summary>Shows inline markup with real formatting; returns the runs (to map clicks back to the markup).</summary>
    internal static List<InlineSpan> RenderInlines(TextBlock view, string markup)
    {
        var inlines = view.Inlines ??= new InlineCollection();
        inlines.Clear();
        var spans = NoteDocument.ParseInline(markup);
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Bold) run.FontWeight = FontWeight.Bold;
            if (span.Italic) run.FontStyle = FontStyle.Italic;
            if (span.Code)
            {
                run.FontFamily = Mono;
                run.Background = CodeBack;
            }
            if (span.Url is not null)
            {
                run.Foreground = LinkBrush;
                run.TextDecorations = TextDecorations.Underline;
            }
            if (span.Muted) run.Foreground = Muted;
            inlines.Add(run);
        }
        return spans;
    }

    private void FocusRow(Row row, int caret, int selectTo = -1)
    {
        if (row.Block.IsAtomic) return;
        if (row.Grid is { } grid)
        {
            // a caret past the start means "at the end": the last cell
            _active = row;
            grid.FocusEdge(atEnd: caret > 0);
            return;
        }
        if (row.Block.HasMarkup)
        {
            row.Editing = true;
            row.View.IsVisible = false;
            row.Edit.IsVisible = true;
        }
        _active = row;
        row.Edit.Focus();
        var length = row.Edit.Text?.Length ?? 0;
        caret = Math.Clamp(caret, 0, length);
        row.Edit.CaretIndex = caret;
        row.Edit.SelectionStart = caret;
        row.Edit.SelectionEnd = selectTo >= 0 ? Math.Clamp(selectTo, 0, length) : caret;
        row.Frame.BringIntoView();
        // a row that was just added has no layout yet: scroll again once it has
        Dispatcher.UIThread.Post(() =>
        {
            if (!_rows.Contains(row)) return;
            row.Frame.UpdateLayout();
            row.Frame.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void EndEdit(Row row)
    {
        // a context menu on the text box takes focus while it is open — keep editing
        if (row.Edit.ContextFlyout?.IsOpen == true || row.Edit.ContextMenu?.IsOpen == true) return;
        row.Editing = false;
        if (!_rows.Contains(row) || !row.Block.HasMarkup) return;
        Render(row);
        row.View.IsVisible = true;
        row.Edit.IsVisible = false;
    }

    private static void SetText(Row row, string text)
    {
        row.Block.Text = text; // set first, so the (possibly late) TextChanged sees no change
        row.Edit.Text = text;
    }

    /// <summary>The letters or digits around an index (an empty range at the index when it is not on a word).</summary>
    private static (int Start, int End) WordAt(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        static bool Part(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-';
        int s = index, e = index;
        while (s > 0 && Part(text[s - 1])) s--;
        while (e < text.Length && Part(text[e])) e++;
        while (s < e && !char.IsLetterOrDigit(text[s])) s++; // quotes and dashes around a word are not part of it
        while (e > s && !char.IsLetterOrDigit(text[e - 1])) e--;
        return (s, e);
    }

    private static (int Start, int End) Selection(TextBox box)
    {
        var length = box.Text?.Length ?? 0;
        var s = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, length);
        var e = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), s, length);
        if (s == e) s = e = Math.Clamp(box.CaretIndex, 0, length);
        return (s, e);
    }

    private static string StampNow()
    {
        var format = (Application.Current as App)?.Config.TimeBulletFormat ?? "yyyy-MM-dd HH:mm";
        return TimestampService.Now(format);
    }

    private void ChangeKind(Row row, BlockKind kind, int level = 2)
    {
        var (s, e) = Selection(row.Edit);
        var b = row.Block;
        if (kind == BlockKind.Timed && b.Stamp.Length == 0)
        {
            b.Stamp = StampNow();
            row.Stamp.Text = b.Stamp;
        }
        if (!b.IsList || kind is not (BlockKind.Bullet or BlockKind.Number or BlockKind.Timed)) b.Indent = 0;
        b.Kind = kind;
        b.Level = level;
        ApplyKind(row);
        RefreshLists();
        FocusRow(row, s, e);
        Changed?.Invoke();
    }

    /// <summary>The block that follows <paramref name="b"/> when Enter is pressed in it.</summary>
    private static NoteBlock NextBlock(NoteBlock b) => b.IsList
        ? new NoteBlock { Kind = b.Kind, Indent = b.Indent, Stamp = b.Kind == BlockKind.Timed ? StampNow() : "" }
        : new NoteBlock { Kind = b.Kind == BlockKind.Quote ? BlockKind.Quote : BlockKind.Paragraph };

    // ---- keyboard ----------------------------------------------------------------

    private void Edit_KeyDown(Row row, KeyEventArgs e)
    {
        var box = row.Edit;
        var text = box.Text ?? "";
        var (s, end) = Selection(box);
        var none = e.KeyModifiers == KeyModifiers.None;
        var collapsed = s == end;
        if (_selected.Count > 0)
        {
            if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = CopyAllAsync();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Delete or Key.Back && none)
            {
                DeleteAll();
                e.Handled = true;
                return;
            }
            if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)) ClearAllSelected();
        }
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            SelectAllBlocks();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when none:
                e.Handled = OnEnter(row, s, end);
                break;
            case Key.Back when none && collapsed && s == 0:
                e.Handled = OnBackspaceAtStart(row);
                break;
            case Key.Delete when none && collapsed && s == text.Length:
                e.Handled = OnDeleteAtEnd(row);
                break;
            case Key.Tab when row.Block.IsList && (none || e.KeyModifiers == KeyModifiers.Shift):
                Indent(row, e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Up when none:
                if (CaretLine(box, out _) == 0 && Move(row, -1, atEnd: true)) e.Handled = true;
                break;
            case Key.Down when none:
                if (CaretLine(box, out var lines) >= lines - 1 && Move(row, 1, atEnd: false)) e.Handled = true;
                break;
            case Key.Left when none && collapsed && s == 0:
                if (Move(row, -1, atEnd: true)) e.Handled = true;
                break;
            case Key.Right when none && collapsed && s == text.Length:
                if (Move(row, 1, atEnd: false)) e.Handled = true;
                break;
        }
    }

    private List<Row> _selected = new();
    private int _dragAnchor = -1;
    private static readonly IBrush AllSelectedBrush = new SolidColorBrush(Color.FromArgb(70, 60, 130, 220));

    /// <summary>Ctrl+A: highlights every block (Ctrl+C copies, Delete clears).</summary>
    private void SelectAllBlocks() => SelectRange(0, _rows.Count - 1);

    private void SelectRange(int from, int to)
    {
        if (from > to) (from, to) = (to, from);
        var wanted = _rows.Skip(from).Take(to - from + 1).ToList();
        if (wanted.SequenceEqual(_selected)) return;
        ClearAllSelected();
        _selected = wanted;
        foreach (var r in _selected) r.Frame.Background = AllSelectedBrush;
    }

    private void ClearAllSelected()
    {
        foreach (var r in _selected) r.Frame.Background = null;
        _selected = new List<Row>();
    }

    private void Editor_PointerPressed(PointerPressedEventArgs e)
    {
        if (_selected.Count > 0) ClearAllSelected();
        var point = e.GetCurrentPoint(BlockHost);
        _dragAnchor = point.Properties.IsLeftButtonPressed ? RowIndexAt(point.Position.Y) : -1;
    }

    private void Editor_PointerMoved(PointerEventArgs e)
    {
        if (_dragAnchor < 0) return;
        var point = e.GetCurrentPoint(BlockHost);
        if (!point.Properties.IsLeftButtonPressed) { _dragAnchor = -1; return; }
        var index = RowIndexAt(point.Position.Y);
        if (index < 0) return;
        if (index == _dragAnchor)
        {
            if (_selected.Count > 0) ClearAllSelected();
            return;
        }
        SelectRange(_dragAnchor, index);
        e.Handled = true;
    }

    private int RowIndexAt(double y)
    {
        if (_rows.Count == 0) return -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            var bounds = _rows[i].Frame.Bounds;
            if (y < bounds.Bottom) return i;
        }
        return _rows.Count - 1;
    }

    private async Task CopyAllAsync()
    {
        var text = NoteDocument.ToMarkdown(_selected.Select(r => r.Block).ToList(), h => h);
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private void DeleteAll()
    {
        var doomed = _selected.ToList();
        ClearAllSelected();
        if (doomed.Count == 0) return;
        var first = doomed[0];
        foreach (var r in doomed.Skip(1)) RemoveRow(r);
        if (_rows.Count == 1)
        {
            first.Block.Kind = BlockKind.Paragraph;
            first.Block.Indent = 0;
            first.Block.Stamp = "";
            SetText(first, "");
            ApplyKind(first);
        }
        else
        {
            var index = _rows.IndexOf(first);
            RemoveRow(first);
            first = _rows[Math.Min(index, _rows.Count - 1)];
        }
        RefreshLists();
        FocusRow(first, 0);
        Changed?.Invoke();
    }

    /// <summary>The visual line the caret is on (wrapped lines count), and how many there are.</summary>
    internal static int CaretLine(TextBox box, out int lineCount)
    {
        var caret = Math.Clamp(box.CaretIndex, 0, box.Text?.Length ?? 0);
        var layout = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.TextLayout;
        if (layout is null)
        {
            var text = box.Text ?? "";
            lineCount = text.Count(c => c == '\n') + 1;
            return text[..caret].Count(c => c == '\n');
        }
        lineCount = layout.TextLines.Count;
        return layout.GetLineIndexFromCharacterIndex(caret, false);
    }

    private bool Move(Row row, int direction, bool atEnd)
    {
        var i = _rows.IndexOf(row) + direction;
        while (i >= 0 && i < _rows.Count && _rows[i].Block.IsAtomic) i += direction;
        if (i < 0 || i >= _rows.Count) return false;
        FocusRow(_rows[i], atEnd ? int.MaxValue : 0);
        return true;
    }

    private bool OnEnter(Row row, int s, int e)
    {
        var b = row.Block;
        var text = row.Edit.Text ?? "";
        var index = _rows.IndexOf(row);
        if (b.Kind == BlockKind.Raw) return false;
        if (b.Kind == BlockKind.Code)
        {
            // Enter on an empty last line leaves the code block
            if (s != e || s != text.Length || !text.EndsWith('\n')) return false;
            SetText(row, text[..^1]);
            FocusRow(AddRow(index + 1, new NoteBlock()), 0);
            Changed?.Invoke();
            return true;
        }

        if (text.Length == 0 && (b.IsList || b.Kind == BlockKind.Quote))
        {
            // Enter on an empty item: step out one level, then out of the list
            if (b.IsList && b.Indent > 0)
            {
                b.Indent--;
                RefreshLists();
                Changed?.Invoke();
            }
            else
            {
                ChangeKind(row, BlockKind.Paragraph);
            }
            return true;
        }

        if (s == 0 && e == 0 && text.Length > 0)
        {
            // at the start of a block: open an empty one above and stay put
            var above = NextBlock(b);
            if (b.Kind == BlockKind.Heading) above.Kind = BlockKind.Paragraph;
            AddRow(index, above);
            RefreshLists();
            FocusRow(row, 0);
            Changed?.Invoke();
            return true;
        }

        var before = text[..s];
        var after = text[e..];
        var open = NoteDocument.OpenMarkers(before);
        var reopen = string.Concat(open);
        if (open.Count > 0)
        {
            // keep formatting that spans the split: "**bo|ld**" → "**bo**" + "**ld**"
            before += string.Concat(Enumerable.Reverse(open));
            after = reopen + after;
        }
        SetText(row, before);
        var next = NextBlock(b);
        next.Text = after;
        var newRow = AddRow(index + 1, next);
        RefreshLists();
        FocusRow(newRow, reopen.Length);
        if (!row.Editing) Render(row);
        Changed?.Invoke();
        return true;
    }

    private bool OnBackspaceAtStart(Row row)
    {
        var b = row.Block;
        var text = row.Edit.Text ?? "";
        if (b.IsList && b.Indent > 0)
        {
            Indent(row, -1);
            return true;
        }
        if (b.Kind is BlockKind.Heading or BlockKind.Quote or BlockKind.Bullet or BlockKind.Number or BlockKind.Timed
            || b.Kind is BlockKind.Code or BlockKind.Raw && text.Length == 0)
        {
            ChangeKind(row, BlockKind.Paragraph);
            return true;
        }
        if (b.Kind != BlockKind.Paragraph) return false;

        var index = _rows.IndexOf(row);
        if (index <= 0) return false;
        var prev = _rows[index - 1];
        if (prev.Block.IsAtomic)
        {
            RemoveRow(prev);
            Changed?.Invoke();
            return true;
        }
        var prevText = prev.Edit.Text ?? "";
        if (prev.Block.Kind == BlockKind.Raw || prev.Block.IsGrid)
        {
            FocusRow(prev, int.MaxValue);
            if (text.Length == 0) RemoveRow(row);
            Changed?.Invoke();
            return true;
        }
        // join this paragraph onto the end of the previous block
        SetText(prev, prevText + text);
        FocusRow(prev, prevText.Length);
        RemoveRow(row);
        RefreshLists();
        Changed?.Invoke();
        return true;
    }

    private bool OnDeleteAtEnd(Row row)
    {
        var index = _rows.IndexOf(row);
        if (index < 0 || index + 1 >= _rows.Count || !row.Block.HasMarkup && row.Block.Kind != BlockKind.Code) return false;
        var next = _rows[index + 1];
        if (next.Block.IsAtomic)
        {
            RemoveRow(next);
            Changed?.Invoke();
            return true;
        }
        if (!next.Block.HasMarkup) return false;
        var text = row.Edit.Text ?? "";
        SetText(row, text + next.Block.Text);
        RemoveRow(next);
        RefreshLists();
        FocusRow(row, text.Length);
        Changed?.Invoke();
        return true;
    }

    private void Indent(Row row, int delta)
    {
        var index = _rows.IndexOf(row);
        var max = index > 0 && _rows[index - 1].Block.IsList ? _rows[index - 1].Block.Indent + 1 : 0;
        var next = Math.Clamp(row.Block.Indent + delta, 0, max);
        if (next == row.Block.Indent) return;
        row.Block.Indent = next;
        RefreshLists();
        Changed?.Invoke();
    }

    // ---- typing, pasting, clicking -----------------------------------------------------

    private static readonly System.Text.RegularExpressions.Regex Shortcut =
        new(@"^(#{1,3} |[-*•] |\d+[.)] |> )", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void Edit_TextChanged(Row row)
    {
        var text = row.Edit.Text ?? "";
        var old = row.Block.Text;
        if (text == old) return;
        row.Block.Text = text;
        if (!row.Editing && row.Block.HasMarkup) Render(row);
        if (row.Block.Kind == BlockKind.Paragraph) TryShortcut(row, old);
        Changed?.Invoke();
    }

    /// <summary>Markdown-style shortcuts typed at the start of a paragraph: "- ", "1. ", "# ", "> ", "```", "---".</summary>
    private void TryShortcut(Row row, string old)
    {
        var text = row.Block.Text;
        string prefix;
        BlockKind kind;
        var level = 2;
        if (text == "---")
        {
            prefix = "---";
            kind = BlockKind.Divider;
        }
        else if (text.StartsWith("```"))
        {
            prefix = "```";
            kind = BlockKind.Code;
        }
        else
        {
            var m = Shortcut.Match(text);
            if (!m.Success) return;
            prefix = m.Value;
            kind = prefix[0] switch
            {
                '#' => BlockKind.Heading,
                '>' => BlockKind.Quote,
                >= '0' and <= '9' => BlockKind.Number,
                _ => BlockKind.Bullet,
            };
            if (kind == BlockKind.Heading) level = prefix.Length - 1;
        }
        if (old.StartsWith(prefix)) return;

        // change the text box after its own text update has finished
        Dispatcher.UIThread.Post(() =>
        {
            var current = row.Edit.Text ?? "";
            if (!_rows.Contains(row) || row.Block.Kind != BlockKind.Paragraph || !current.StartsWith(prefix)) return;
            var caret = Math.Max(0, row.Edit.CaretIndex - prefix.Length);
            SetText(row, current[prefix.Length..]);
            if (kind == BlockKind.Divider)
            {
                row.Block.Kind = BlockKind.Divider;
                ApplyKind(row);
                var index = _rows.IndexOf(row);
                var next = index + 1 < _rows.Count && _rows[index + 1].Block.HasMarkup ? _rows[index + 1] : AddRow(index + 1, new NoteBlock());
                RefreshLists();
                FocusRow(next, 0);
                Changed?.Invoke();
                return;
            }
            row.Block.Level = level;
            ChangeKind(row, kind, level);
            FocusRow(row, caret);
        });
    }

    private async void Edit_Paste(Row row, RoutedEventArgs e)
    {
        if (!row.Block.HasMarkup) return; // code and HTML blocks take text as it is
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        e.Handled = true;
        string? text;
        IEnumerable<IStorageItem>? files = null;
        try
        {
            text = await clipboard.GetTextAsync();
            // text wins: Excel and browsers put a picture next to the text they copy
            if (string.IsNullOrEmpty(text)) files = await clipboard.GetDataAsync(DataFormats.Files) as IEnumerable<IStorageItem>;
        }
        catch
        {
            return;
        }
        if (!_rows.Contains(row)) return;
        if (!string.IsNullOrEmpty(text))
        {
            InsertPlain(row, text);
            return;
        }
        var srcs = AddImageFiles(files);
        if (srcs.Count == 0 && ClipboardImageService.TryGetImage() is { } image) srcs = AddImageBytes(image.Data, image.Extension);
        if (srcs.Count > 0) InsertImages(srcs, CaptureSelection());
    }

    // ---- pictures ----------------------------------------------------------------------

    /// <summary>Copies picture files into the notes' image folder; returns their srcs (non-pictures are skipped).</summary>
    private List<string> AddImageFiles(IEnumerable<IStorageItem>? items)
    {
        var srcs = new List<string>();
        foreach (var item in items ?? Enumerable.Empty<IStorageItem>())
        {
            if (item.TryGetLocalPath() is not { } path || !NotesService.IsImageFile(path)) continue;
            try { srcs.Add(NotesService.AddImage(path)); }
            catch (Exception ex) { PictureError?.Invoke("Could not add the picture: " + ex.Message); }
        }
        return srcs;
    }

    /// <summary>Saves pasted picture bytes; a BMP (what Windows offers for some screenshots) is stored as PNG.</summary>
    private List<string> AddImageBytes(byte[] data, string extension)
    {
        try
        {
            if (extension == ".bmp")
            {
                using var bitmap = new Bitmap(new MemoryStream(data));
                using var png = new MemoryStream();
                bitmap.Save(png);
                (data, extension) = (png.ToArray(), ".png");
            }
            return new List<string> { NotesService.AddImage(data, extension) };
        }
        catch (Exception ex)
        {
            PictureError?.Invoke("Could not paste the picture: " + ex.Message);
            return new List<string>();
        }
    }

    /// <summary>Raised when a picture could not be added (the message is for the status bar).</summary>
    public event Action<string>? PictureError;

    /// <summary>Adds picture blocks (srcs from <see cref="NotesService.AddImage(string)"/>) after the captured block,
    /// or at the end, and puts the caret in a paragraph below them.</summary>
    public void InsertImages(IReadOnlyList<string> srcs, EditorSelection? after = null)
    {
        if (srcs.Count == 0) return;
        var index = after?.Block is Row r && _rows.Contains(r) ? _rows.IndexOf(r) + 1 : _rows.Count;
        var emptyOnly = _rows.Count == 1 && _rows[0].Block.Kind == BlockKind.Paragraph && _rows[0].Block.Text.Length == 0 ? _rows[0] : null;
        foreach (var src in srcs) AddRow(index++, NoteDocument.Image(src));
        if (emptyOnly is not null)
        {
            RemoveRow(emptyOnly);
            index--;
        }
        var next = index < _rows.Count && _rows[index].Block.Kind == BlockKind.Paragraph ? _rows[index] : AddRow(index, new NoteBlock());
        RefreshLists();
        FocusRow(next, 0);
        Changed?.Invoke();
    }

    /// <summary>Loads the picture scaled down to at most 1600 px wide, so big screenshots don't hold a lot of memory.</summary>
    private void ShowPicture(Row row)
    {
        row.Bitmap?.Dispose();
        row.Bitmap = null;
        var src = NoteDocument.ImageSrc(row.Block);
        var file = NotesService.ResolveImage(src);
        try
        {
            if (file is not null && File.Exists(file))
            {
                using (var stream = File.OpenRead(file)) row.Bitmap = new Bitmap(stream);
                if (row.Bitmap.PixelSize.Width > 1600)
                {
                    row.Bitmap.Dispose();
                    using var stream = File.OpenRead(file);
                    row.Bitmap = Bitmap.DecodeToWidth(stream, 1600);
                }
            }
        }
        catch
        {
            row.Bitmap = null; // not a readable picture
        }
        row.Picture.Source = row.Bitmap;
        row.Picture.IsVisible = row.Bitmap is not null;
        if (row.Bitmap is null)
        {
            // a missing file or a web picture: show where it points instead
            row.View.Inlines?.Clear();
            row.View.Text = file is null ? $"[web picture: {src}]" : $"[picture not found: {src}]";
            row.View.Foreground = Muted;
            row.View.IsVisible = true;
        }
        ToolTip.SetTip(row.Picture, "Small preview (about 2 lines) · double-click to open · right-click for more");
        AutomationProperties.SetName(row.Picture, "Picture " + Path.GetFileName(src));
    }

    private void DisposePictures()
    {
        foreach (var row in _rows)
        {
            row.Bitmap?.Dispose();
            row.Bitmap = null;
            row.Picture.Source = null;
        }
    }

    private static void OpenPicture(Row row)
    {
        var file = NotesService.ResolveImage(NoteDocument.ImageSrc(row.Block));
        if (file is null || !File.Exists(file)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
        }
        catch
        {
            // no app registered for the picture type
        }
    }

    /// <summary>Languages offered by "Translate to" (menu text, name sent to the AI).</summary>
    public static readonly (string Header, string Language)[] TranslateLanguages =
    {
        ("English", "English"),
        ("Vietnamese (Tiếng Việt)", "Vietnamese"),
        ("Japanese (日本語)", "Japanese"),
        ("Chinese, Simplified (简体中文)", "Simplified Chinese"),
        ("Korean (한국어)", "Korean"),
        ("French (Français)", "French"),
        ("German (Deutsch)", "German"),
        ("Spanish (Español)", "Spanish"),
    };

    /// <summary>Raised by the right-click menu: an AI action ("ask", "explain", "improve", "fix", "shorten",
    /// "translate:&lt;language&gt;") on the selected text.</summary>
    public event Action<string, EditorSelection>? SelectionAction;

    /// <summary>The right-click menu of a block: clipboard, read aloud, and AI actions on the selected text.</summary>
    private ContextMenu TextMenu(Row row)
    {
        EditorSelection? selection = null;
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
        MenuItem AiItem(string header, string action) => Item(header, () =>
        {
            if (selection is { Text.Length: > 0 }) SelectionAction?.Invoke(action, selection);
        });

        var cut = Item("Cut", () => row.Edit.Cut());
        var copy = Item("Copy", () => row.Edit.Copy());
        var paste = Item("Paste", () => row.Edit.Paste());
        var selectAll = Item("Select all", () => row.Edit.SelectAll());
        var speak = Item("🔊 Speak", () => SpeakText(row, selection, slow: false));
        var slow = Item("🔉 Speak slowly", () => SpeakText(row, selection, slow: true));
        var stop = Item("⏹ Stop speaking", SpeechService.Stop);
        var explain = AiItem("✨ Explain (for learning English)", "explain");
        var ask = AiItem("✨ Ask AI about the selection…", "ask");
        var translate = new MenuItem { Header = "✨ Translate to" };
        foreach (var (header, language) in TranslateLanguages) translate.Items.Add(AiItem(header, "translate:" + language));
        var more = new MenuItem { Header = "✨ More AI" };
        more.Items.Add(AiItem("Improve writing", "improve"));
        more.Items.Add(AiItem("Fix spelling and grammar", "fix"));
        more.Items.Add(AiItem("Shorten", "shorten"));

        var menu = new ContextMenu
        {
            Items = { cut, copy, paste, selectAll, new Separator(), speak, slow, stop, new Separator(), explain, ask, translate, more },
        };
        var prepared = false;
        void Prepare()
        {
            if (prepared) return;
            prepared = true;
            _active = row;
            selection = CaptureSelection(); // the text box keeps its selection while this menu is open; the AI window won't
            var selected = selection is { Text.Length: > 0 };
            var hasText = (row.Edit.Text ?? "").Trim().Length > 0;
            cut.IsEnabled = copy.IsEnabled = selected;
            selectAll.IsEnabled = hasText;
            speak.IsEnabled = slow.IsEnabled = hasText;
            speak.Header = selected ? "🔊 Speak the selection" : "🔊 Speak this paragraph";
            stop.IsEnabled = SpeechService.IsSpeaking;
            explain.IsEnabled = ask.IsEnabled = translate.IsEnabled = more.IsEnabled = selected;
        }
        // a menu opened from code (right-click on a rendered block) may skip Opening
        menu.Opening += (_, _) => Prepare();
        menu.Opened += (_, _) =>
        {
            Prepare();
            prepared = false;
        };
        return menu;
    }

    private static void SpeakText(Row row, EditorSelection? selection, bool slow)
    {
        var text = selection is { Text.Length: > 0 } ? selection.Text : row.Edit.Text ?? "";
        SpeechService.Speak(text, slow);
    }

    private ContextMenu PictureMenu(Row row)
    {
        var open = new MenuItem { Header = "Open picture" };
        open.Click += (_, _) => OpenPicture(row);
        var remove = new MenuItem { Header = "Remove picture from note" };
        remove.Click += (_, _) =>
        {
            if (!_rows.Contains(row)) return;
            var index = _rows.IndexOf(row);
            RemoveRow(row);
            RefreshLists();
            var near = _rows.ElementAtOrDefault(Math.Min(index, _rows.Count - 1));
            if (near is not null && !near.Block.IsAtomic) FocusRow(near, 0);
            Changed?.Invoke();
        };
        return new ContextMenu { Items = { open, remove } };
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped picture files go below the block they were dropped on.</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var srcs = AddImageFiles(e.Data.GetFiles());
        if (srcs.Count == 0) return;
        var y = e.GetPosition(BlockHost).Y;
        var target = _rows.LastOrDefault(r => r.Frame.Bounds.Top <= y) ?? _rows.LastOrDefault();
        InsertImages(srcs, target is null ? null : new EditorSelection(target, 0, 0, "", target.Block.Text));
    }

    /// <summary>Pastes text; each further line becomes its own block (list and heading prefixes are recognised).</summary>
    private void InsertPlain(Row row, string text)
    {
        if (NoteDocument.GridFromText(text) is { } cells)
        {
            // cells copied from a spreadsheet, or a Markdown table
            _active = row;
            InsertGrid(new NoteBlock { Kind = BlockKind.Table, Cells = cells });
            return;
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = row.Edit.Text ?? "";
        var (s, e) = Selection(row.Edit);
        if (lines.Length == 1)
        {
            SetText(row, current[..s] + text + current[e..]);
            FocusRow(row, s + text.Length);
            Changed?.Invoke();
            return;
        }

        var after = current[e..];
        SetText(row, current[..s] + lines[0].TrimEnd());
        var index = _rows.IndexOf(row);
        var last = row;
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim().Length == 0) continue;
            var block = NoteDocument.ParseLine(line, fromHtml: false, markdown: true);
            if (block.Kind == BlockKind.Paragraph && row.Block.IsList)
            {
                // plain lines pasted into a list become more items of that list
                block = NextBlock(row.Block);
                block.Text = line.Trim();
            }
            last = AddRow(++index, block);
        }
        var caret = last.Block.Text.Length;
        SetText(last, last.Block.Text + after);
        if (last != row && !last.Editing) Render(last);
        RefreshLists();
        FocusRow(last, caret);
        Changed?.Invoke();
    }

    private void View_PointerPressed(Row row, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(row.View).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed) return;
        var rendered = row.View.TextLayout.HitTestPoint(e.GetPosition(row.View)).TextPosition;
        e.Handled = true;
        if (props.IsRightButtonPressed)
        {
            // like a browser: right-click on a word picks it, then the menu opens on the text box
            var index = NoteDocument.SourceIndex(row.Spans, rendered, row.Block.Text.Length);
            var (ws, we) = WordAt(row.Block.Text, index);
            FocusRow(row, ws, we);
            Dispatcher.UIThread.Post(() => row.Edit.ContextMenu?.Open(row.Edit));
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && OpenUrlAt(row.Spans, rendered)) return;
        FocusRow(row, NoteDocument.SourceIndex(row.Spans, rendered, row.Block.Text.Length));
    }

    /// <summary>Opens the link under a rendered character position (Ctrl+click); false when there is none.</summary>
    internal static bool OpenUrlAt(IReadOnlyList<InlineSpan> spans, int rendered)
    {
        string? url = null;
        foreach (var span in spans)
        {
            url = span.Url;
            if (rendered < span.Text.Length) break;
            rendered -= span.Text.Length;
        }
        if (url is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // not a launchable address — ignore
        }
        return true;
    }

    private void Filler_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var last = _rows[^1];
        if (last.Block.Kind == BlockKind.Paragraph && last.Block.Text.Length == 0)
        {
            FocusRow(last, 0);
        }
        else
        {
            var row = AddRow(_rows.Count, new NoteBlock());
            RefreshLists();
            FocusRow(row, 0);
        }
        e.Handled = true;
    }
}
