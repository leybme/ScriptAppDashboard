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
using Material.Icons;
using Material.Icons.Avalonia;
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
        public bool PictureOpen, PictureWasOpen;
        public required Panel Content;
        public GridBlockView? Grid;
        public Bitmap? Bitmap;
        public List<InlineSpan> Spans = new();
        public bool Editing;
    }

    private readonly List<Row> _rows = new();
    private Row? _active;
    private Window? _window;

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
        AddHandler(PointerReleasedEvent, (_, _) => { _dragAnchor = -1; _viewDrag = null; }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Editor_KeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DetachedFromVisualTree += (_, _) => DisposePictures();
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window || _window == window) return;
            _window = window;
            // back from another app: a block kept open for the returning focus is closed if the focus went elsewhere
            window.Activated += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                foreach (var row in _rows.Where(r => r.Editing && !r.Edit.IsFocused).ToList()) EndEdit(row);
            }, DispatcherPriority.Background);
        };
        Load("");
    }

    // ---- public API -----------------------------------------------------------

    /// <summary>Shows a note body and starts a new undo history. Does not raise <see cref="Changed"/>.</summary>
    public void Load(string html)
    {
        CloseSlash();
        ClearAllSelected();
        DisposePictures();
        _rows.Clear();
        BlockHost.Children.Clear();
        _active = null;
        var blocks = NoteDocument.Parse(html);
        if (blocks.Count == 0) blocks.Add(new NoteBlock());
        foreach (var block in blocks) AddRow(_rows.Count, block);
        RefreshLists();
        Scroller.Offset = default;
        _undo.Clear();
        _redo.Clear();
        _state = Capture();
        _typingRun = false;
        CurrentChanged?.Invoke();
    }

    /// <summary>Replaces the note body as one step that Ctrl+Z undoes (AI edits); unchanged blocks are kept as they are.</summary>
    public void SetHtml(string html)
    {
        if (html == ToHtml()) return;
        ApplyHtml(html);
        OnChanged();
        CurrentChanged?.Invoke();
    }

    // ---- undo / redo of the whole note ----------------------------------------------------
    // Each block's TextBox only knows its own text, so Enter, joins, deleted blocks and AI edits could not be undone.
    // The editor keeps snapshots of the note instead; typing in one block within a short pause is one step.

    private sealed record EditorState(string Html, int Row, int Start, int End);

    private readonly List<EditorState> _undo = new(), _redo = new();
    private EditorState? _state; // the note as it is now
    private DateTime _lastChange;
    private bool _typingRun;
    private const int MaxUndo = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised when the block the caret is in (or its kind) changes, for the toolbar.</summary>
    public event Action? CurrentChanged;

    /// <summary>Up on the first line of the first block: the caret moves on to the fields above the note.</summary>
    public event Action? ExitTop;

    /// <summary>The kind and level of the block being edited (null when none is).</summary>
    public (BlockKind Kind, int Level)? CurrentBlock
        => _active is { } row && _rows.Contains(row) ? (row.Block.Kind, row.Block.Level) : null;

    /// <summary>Records the change for undo, then raises <see cref="Changed"/>.</summary>
    private void OnChanged(bool typing = false)
    {
        Record(typing);
        Changed?.Invoke();
    }

    private EditorState Capture()
    {
        var index = _active is null ? -1 : _rows.IndexOf(_active);
        var (s, e) = index >= 0 && !_active!.Block.IsGrid ? Selection(_active.Edit) : (0, 0);
        return new EditorState(ToHtml(), index, s, e);
    }

    private void Record(bool typing)
    {
        var now = Capture();
        if (_state is null || now.Html == _state.Html)
        {
            _state = now;
            return;
        }
        var time = DateTime.UtcNow;
        var sameRun = typing && _typingRun && now.Row == _state.Row && time - _lastChange < TimeSpan.FromSeconds(1.5);
        if (!sameRun)
        {
            _undo.Add(_state);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }
        _redo.Clear();
        _state = now;
        _lastChange = time;
        _typingRun = typing;
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    private void Step(List<EditorState> from, List<EditorState> to)
    {
        if (from.Count == 0) return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(Capture());
        var (changed, count) = ApplyHtml(target.Html);
        // the caret goes to the change: where it was then if that is inside the change, else to the first changed block
        // (the focused block may have been replaced, and keys typed next must still reach the note)
        if (target.Row >= changed && target.Row < changed + Math.Max(1, count) && target.Row < _rows.Count
            && !_rows[target.Row].Block.IsAtomic)
            FocusRow(_rows[target.Row], target.Start, target.End);
        else if (_rows.Skip(changed).Concat(_rows.Take(changed).Reverse()).FirstOrDefault(r => !r.Block.IsAtomic) is { } near)
            FocusRow(near, near.Block.IsGrid ? 0 : int.MaxValue);
        _state = Capture();
        _typingRun = false;
        Changed?.Invoke();
        CurrentChanged?.Invoke();
    }

    /// <summary>Shows <paramref name="html"/> by rebuilding only the blocks between the unchanged start and end
    /// of the note, so pictures stay loaded and the rest keeps its state. Returns the index of the first changed block
    /// and how many blocks were put there.</summary>
    private (int First, int Count) ApplyHtml(string html)
    {
        CloseSlash();
        ClearAllSelected();
        var blocks = NoteDocument.Parse(html);
        if (blocks.Count == 0) blocks.Add(new NoteBlock());
        var keys = blocks.Select(BlockKey).ToList();
        int head = 0, tail = 0;
        while (head < _rows.Count && head < blocks.Count && BlockKey(_rows[head].Block) == keys[head]) head++;
        while (tail < _rows.Count - head && tail < blocks.Count - head
               && BlockKey(_rows[_rows.Count - 1 - tail].Block) == keys[blocks.Count - 1 - tail]) tail++;
        var old = _rows.Skip(head).Take(_rows.Count - head - tail).ToList();
        var at = head + old.Count;
        // add the new blocks before removing the old ones, so the note is never empty in between
        foreach (var block in blocks.Skip(head).Take(blocks.Count - head - tail)) AddRow(at++, block);
        foreach (var row in old) RemoveRow(row);
        RefreshLists();
        return (Math.Min(head, _rows.Count - 1), blocks.Count - head - tail);
    }

    private static string BlockKey(NoteBlock b) => $"{b.Indent}\u0001{b.Stamp}\u0001{NoteDocument.ToHtml(new[] { b })}";

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
        OnChanged();
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
        OnChanged();
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
        OnChanged();
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
        OnChanged();
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
            OnChanged();
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
        OnChanged();
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
        picture.PointerPressed += (_, e) => Picture_PointerPressed(row, e);
        picture.ContextMenu = PictureMenu(row);
        edit.AddHandler(KeyDownEvent, (_, e) => Edit_KeyDown(row, e), RoutingStrategies.Tunnel);
        edit.TextChanged += (_, _) => Edit_TextChanged(row);
        edit.GotFocus += (_, _) =>
        {
            var moved = _active != row;
            _active = row;
            row.Editing = true;
            if (moved) CurrentChanged?.Invoke();
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
            OnChanged(typing: true);
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
                OnChanged();
            };
            grid = table;
        }
        else
        {
            grid = new NoteChartView(row.Block, showData);
        }
        grid.Changed += () => OnChanged(typing: true);
        grid.Exit = direction => _rows.Contains(row) && LeaveGrid(row, direction);
        grid.DeleteRequested += () =>
        {
            if (!_rows.Contains(row)) return;
            var index = _rows.IndexOf(row);
            RemoveRow(row);
            RefreshLists();
            var near = _rows.ElementAtOrDefault(Math.Min(index, _rows.Count - 1));
            if (near is not null && !near.Block.IsAtomic) FocusRow(near, 0);
            OnChanged();
        };
        grid.AddHandler(GotFocusEvent, (_, _) =>
        {
            if (_active == row) return;
            _active = row;
            CurrentChanged?.Invoke();
        });
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
        OnChanged();
        return true;
    }

    private void RemoveRow(Row row)
    {
        row.Bitmap?.Dispose();
        row.Bitmap = null;
        _rows.Remove(row);
        BlockHost.Children.Remove(row.Frame);
        if (_active == row) _active = null;
        if (_slashRow == row) CloseSlash();
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
        double size = mono ? 13 : heading ? b.Level switch { 1 => 22, 2 => 18, 3 => 16, _ => 15 } : 14;

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
        // a click on the "/" menu moves the focus for a moment; close it only when the block really lost the focus
        if (_slashRow == row)
            Dispatcher.UIThread.Post(() =>
            {
                if (_slashRow == row && !row.Edit.IsFocused) CloseSlash();
            });
        // a context menu on the text box takes focus while it is open — keep editing
        if (row.Edit.ContextFlyout?.IsOpen == true || row.Edit.ContextMenu?.IsOpen == true) return;
        // switching to another app: the window gives the focus back to this box when it is activated again,
        // which fails if the box was hidden meanwhile (the caret was lost after Alt+Tab)
        if (TopLevel.GetTopLevel(this) is Window { IsActive: false }) return;
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
        OnChanged();
        CurrentChanged?.Invoke();
    }

    /// <summary>The block that follows <paramref name="b"/> when Enter is pressed in it.</summary>
    private static NoteBlock NextBlock(NoteBlock b) => b.IsList
        ? new NoteBlock { Kind = b.Kind, Indent = b.Indent, Stamp = b.Kind == BlockKind.Timed ? StampNow() : "" }
        : new NoteBlock { Kind = b.Kind == BlockKind.Quote ? BlockKind.Quote : BlockKind.Paragraph };

    // ---- keyboard ----------------------------------------------------------------

    /// <summary>Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) anywhere in the note, cells and time stamps included, use the note's history.</summary>
    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers & ~KeyModifiers.Shift;
        if (command is not (KeyModifiers.Control or KeyModifiers.Meta)) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift) Undo();
        else if (e.Key == Key.Y && !shift || e.Key == Key.Z && shift) Redo();
        else return;
        e.Handled = true;
    }

    private void Edit_KeyDown(Row row, KeyEventArgs e)
    {
        if (SlashKey(row, e)) return;
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
                if (CaretLine(box, out _) != 0) break;
                if (Move(row, -1, atEnd: true)) e.Handled = true;
                else if (ExitTop is not null)
                {
                    ExitTop();
                    e.Handled = true;
                }
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
        foreach (var r in _selected)
        {
            r.Frame.Background = AllSelectedBrush;
            r.Edit.ClearSelection(); // the text that was selected in the block the drag started in
        }
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
            ExtendViewDrag(e);
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
        OnChanged();
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
            OnChanged();
            return true;
        }

        if (text.Length == 0 && (b.IsList || b.Kind == BlockKind.Quote))
        {
            // Enter on an empty item: step out one level, then out of the list
            if (b.IsList && b.Indent > 0)
            {
                b.Indent--;
                RefreshLists();
                OnChanged();
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
            OnChanged();
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
        OnChanged();
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
            OnChanged();
            return true;
        }
        var prevText = prev.Edit.Text ?? "";
        if (prev.Block.Kind == BlockKind.Raw || prev.Block.IsGrid)
        {
            FocusRow(prev, int.MaxValue);
            if (text.Length == 0) RemoveRow(row);
            OnChanged();
            return true;
        }
        // join this paragraph onto the end of the previous block
        SetText(prev, prevText + text);
        FocusRow(prev, prevText.Length);
        RemoveRow(row);
        RefreshLists();
        OnChanged();
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
            OnChanged();
            return true;
        }
        if (!next.Block.HasMarkup) return false;
        var text = row.Edit.Text ?? "";
        SetText(row, text + next.Block.Text);
        RemoveRow(next);
        RefreshLists();
        FocusRow(row, text.Length);
        OnChanged();
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
        OnChanged();
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
        UpdateSlash(row);
        OnChanged(typing: true);
    }

    /// <summary>Turns a block into a divider and puts the caret in the paragraph below it.</summary>
    private void MakeDivider(Row row)
    {
        row.Block.Kind = BlockKind.Divider;
        ApplyKind(row);
        var index = _rows.IndexOf(row);
        var next = index + 1 < _rows.Count && _rows[index + 1].Block.HasMarkup ? _rows[index + 1] : AddRow(index + 1, new NoteBlock());
        RefreshLists();
        FocusRow(next, 0);
        OnChanged();
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
                MakeDivider(row);
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

    // ---- "/" menu: typed on an empty line, it turns the line into any kind of block ----------------

    private sealed record SlashItem(string Label, string Hint, string Words, MaterialIconKind Icon, Action<Row> Apply);

    private List<SlashItem>? _slashItems;
    private List<SlashItem> _slashMatches = new();
    private Row? _slashRow;
    private Row? _slashDismissed; // Escape closed the menu for this line until its "/" is gone

    /// <summary>Raised by "/picture": the host opens its file picker (the caret's block is where the pictures go).</summary>
    public event Action? PictureRequested;

    private List<SlashItem> SlashItems => _slashItems ??= new()
    {
        new("Text", "plain paragraph", "text paragraph plain normal", MaterialIconKind.FormatParagraph, r => ChangeKind(r, BlockKind.Paragraph)),
        new("Heading 1", "#", "heading h1 title", MaterialIconKind.FormatHeader1, r => ChangeKind(r, BlockKind.Heading, 1)),
        new("Heading 2", "##", "heading h2 subtitle", MaterialIconKind.FormatHeader2, r => ChangeKind(r, BlockKind.Heading, 2)),
        new("Heading 3", "###", "heading h3", MaterialIconKind.FormatHeader3, r => ChangeKind(r, BlockKind.Heading, 3)),
        new("Bulleted list", "-", "bullets list ul unordered", MaterialIconKind.FormatListBulleted, r => ChangeKind(r, BlockKind.Bullet)),
        new("Numbered list", "1.", "numbers list ol ordered", MaterialIconKind.FormatListNumbered, r => ChangeKind(r, BlockKind.Number)),
        new("Time bullets", "each item stamped", "timed time log stamp journal", MaterialIconKind.TimelineClockOutline, r => ChangeKind(r, BlockKind.Timed)),
        new("Quote", ">", "quote blockquote citation", MaterialIconKind.FormatQuoteOpen, r => ChangeKind(r, BlockKind.Quote)),
        new("Code block", "```", "code pre snippet script", MaterialIconKind.CodeBraces, r => ChangeKind(r, BlockKind.Code)),
        new("Divider", "---", "divider rule line hr separator", MaterialIconKind.Minus, MakeDivider),
        new("Table", "rows and columns", "table grid spreadsheet", MaterialIconKind.TableLarge, r => { _active = r; InsertTable(); }),
        new("Chart", "bar, line or pie", "chart graph bar line pie plot", MaterialIconKind.ChartBar, r => { _active = r; InsertChart(); }),
        new("Picture", "from a file", "picture image photo screenshot", MaterialIconKind.ImageOutline, r =>
        {
            _active = r;
            PictureRequested?.Invoke();
        }),
        new("Date and time", "now", "date time now today stamp", MaterialIconKind.ClockOutline, r =>
        {
            var stamp = TimestampService.Now((Application.Current as App)?.Config.TimestampFormat ?? "yyyy-MM-dd HH:mm");
            SetText(r, stamp);
            FocusRow(r, stamp.Length);
            OnChanged();
        }),
    };

    /// <summary>Shows, filters or closes the menu while a paragraph holds just "/" and a few letters.</summary>
    private void UpdateSlash(Row row)
    {
        var text = row.Block.Text;
        if (!text.StartsWith('/') && _slashDismissed == row) _slashDismissed = null;
        if (row.Block.Kind != BlockKind.Paragraph || !text.StartsWith('/') || text.Length > 24 || text.Contains('\n') || _slashDismissed == row)
        {
            if (_slashRow == row) CloseSlash();
            return;
        }
        var query = text[1..].Trim().ToLowerInvariant();
        _slashMatches = SlashItems.Where(i => query.Length == 0 || i.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || i.Words.Split(' ').Any(w => w.StartsWith(query))).ToList();
        if (_slashMatches.Count == 0)
        {
            CloseSlash();
            return;
        }
        _slashRow = row;
        SlashList.Items.Clear();
        for (var i = 0; i < _slashMatches.Count; i++)
        {
            var item = _slashMatches[i];
            var index = i;
            var label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center };
            var hint = new TextBlock { Text = item.Hint, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(hint, 2);
            var entry = new ListBoxItem
            {
                Focusable = false,
                Padding = new Thickness(8, 5),
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("26,*,Auto"),
                    Children = { new MaterialIcon { Kind = item.Icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left }, label, hint },
                },
            };
            AutomationProperties.SetName(entry, item.Label);
            // apply on press, before the click can move the focus out of the line
            entry.AddHandler(PointerPressedEvent, (_, e) =>
            {
                e.Handled = true;
                ApplySlash(index);
            }, RoutingStrategies.Tunnel);
            SlashList.Items.Add(entry);
        }
        SlashList.SelectedIndex = 0;
        SlashPopup.PlacementTarget = row.Edit;
        SlashPopup.IsOpen = true;
    }

    private void CloseSlash()
    {
        _slashRow = null;
        if (SlashPopup.IsOpen) SlashPopup.IsOpen = false;
    }

    /// <summary>Arrows, Enter / Tab and Escape while the menu is open. True when the key was used.</summary>
    private bool SlashKey(Row row, KeyEventArgs e)
    {
        if (_slashRow != row || !SlashPopup.IsOpen || e.KeyModifiers != KeyModifiers.None) return false;
        var count = _slashMatches.Count;
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                SlashList.SelectedIndex = (Math.Max(0, SlashList.SelectedIndex) + (e.Key == Key.Down ? 1 : count - 1)) % count;
                SlashList.ScrollIntoView(SlashList.SelectedIndex);
                break;
            case Key.Enter or Key.Tab:
                ApplySlash(Math.Max(0, SlashList.SelectedIndex));
                break;
            case Key.Escape:
                _slashDismissed = row;
                CloseSlash();
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    private void ApplySlash(int index)
    {
        var row = _slashRow;
        if (row is null || index < 0 || index >= _slashMatches.Count) return;
        var item = _slashMatches[index];
        CloseSlash();
        if (!_rows.Contains(row)) return;
        SetText(row, "");
        FocusRow(row, 0);
        item.Apply(row);
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
        var anchor = after?.Block is Row r && _rows.Contains(r) ? r : null;
        var index = anchor is null ? _rows.Count : _rows.IndexOf(anchor) + 1;
        // pictures take the place of the empty line they are put on
        static bool Empty(Row row) => row.Block is { Kind: BlockKind.Paragraph, Text.Length: 0 };
        var empty = anchor is not null && Empty(anchor) ? anchor : _rows.Count == 1 && Empty(_rows[0]) ? _rows[0] : null;
        foreach (var src in srcs) AddRow(index++, NoteDocument.Image(src));
        if (empty is not null)
        {
            if (_rows.IndexOf(empty) < index) index--;
            RemoveRow(empty);
        }
        var next = index < _rows.Count && _rows[index].Block.Kind == BlockKind.Paragraph ? _rows[index] : AddRow(index, new NoteBlock());
        RefreshLists();
        FocusRow(next, 0);
        OnChanged();
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
        ToolTip.SetTip(row.Picture, "Click to enlarge, click again to shrink · double-click to open in the capture editor · right-click for more");
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

    private const double PictureSmall = 40, PictureLarge = 700;

    /// <summary>One click enlarges the picture in the note, the next one shrinks it again; a double click (which
    /// undoes the toggle of its first click) opens it in the capture editor.</summary>
    private void Picture_PointerPressed(Row row, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(row.Picture).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            SetPictureOpen(row, row.PictureWasOpen);
            OpenInCaptureEditor(row);
            return;
        }
        row.PictureWasOpen = row.PictureOpen;
        SetPictureOpen(row, !row.PictureOpen);
    }

    private static void SetPictureOpen(Row row, bool open)
    {
        row.PictureOpen = open;
        row.Picture.MaxHeight = open ? PictureLarge : PictureSmall;
    }

    /// <summary>Opens the picture as a capture (annotate, OCR, AI). The same picture file reuses its capture.</summary>
    private static readonly Dictionary<string, Guid> PictureCaptures = new(StringComparer.OrdinalIgnoreCase);

    private static void OpenInCaptureEditor(Row row)
    {
        var file = NotesService.ResolveImage(NoteDocument.ImageSrc(row.Block));
        if (file is null || !File.Exists(file) || Application.Current is not App app) return;
        try
        {
            if (PictureCaptures.TryGetValue(file, out var id) && CaptureService.Load(id) is not null)
            {
                app.ShowCapture(id);
                return;
            }
            var before = CaptureService.Load().Select(c => c.Id).ToHashSet();
            using var stream = File.OpenRead(file);
            app.OpenCaptureFrom(Pixels.FromImage(stream), new OpenWindow("", Path.GetFileName(file)));
            if (CaptureService.Load().FirstOrDefault(c => !before.Contains(c.Id)) is { } created) PictureCaptures[file] = created.Id;
        }
        catch
        {
            OpenPicture(row); // not a picture the capture editor can read
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
            OnChanged();
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
            OnChanged();
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
        OnChanged();
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
        var caret = NoteDocument.SourceIndex(row.Spans, rendered, row.Block.Text.Length);
        FocusRow(row, caret);
        // the press went to the rendered text, so the text box never saw it: Editor_PointerMoved selects from here while the button is held
        if (props.IsLeftButtonPressed) _viewDrag = (row, caret);
    }

    private (Row Row, int From)? _viewDrag;

    /// <summary>Extends the selection in the block a drag started in (the press landed on its rendered text).</summary>
    private void ExtendViewDrag(PointerEventArgs e)
    {
        if (_viewDrag is not { } drag || !_rows.Contains(drag.Row)) return;
        var box = drag.Row.Edit;
        if (box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { TextLayout: { } layout } presenter) return;
        var hit = layout.HitTestPoint(e.GetPosition(presenter));
        var length = box.Text?.Length ?? 0;
        var to = Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, length);
        box.CaretIndex = to; // first: setting the caret resets the selection
        box.SelectionStart = Math.Clamp(drag.From, 0, length);
        box.SelectionEnd = to;
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
