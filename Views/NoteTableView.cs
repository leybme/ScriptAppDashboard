using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>The text box being typed in (a block's or a table cell's), how to change its text and how to select in it.</summary>
internal sealed record TextTarget(TextBox Box, bool Markup, Action<string> Set, Action<int, int> Select);

/// <summary>A note block shown with its own controls (a table or a chart) instead of one text box.</summary>
internal abstract class GridBlockView : Border
{
    /// <summary>Raised after every edit of the block.</summary>
    public event Action? Changed;

    /// <summary>The user asked to remove the whole block.</summary>
    public event Action? DeleteRequested;

    /// <summary>Moves the caret out of the block, up (-1) or down (1); false when there is nowhere to go.</summary>
    public Func<int, bool>? Exit;

    protected void RaiseChanged() => Changed?.Invoke();
    protected void RaiseDelete() => DeleteRequested?.Invoke();

    /// <summary>Puts the caret in the first (or last) place of the block.</summary>
    public abstract void FocusEdge(bool atEnd);

    /// <summary>The text box last typed in, for the toolbar (bold, link, time…).</summary>
    public abstract TextTarget? ActiveTarget();
}

/// <summary>
/// A table in the visual editor: a grid of cells, each rendered with formatting and edited (as markup) when clicked.
/// Tab / Enter / arrows move between cells, the context menu adds and removes rows and columns, and pasted
/// spreadsheet cells fill the table from the current cell.
/// </summary>
internal sealed class NoteTableView : GridBlockView
{
    private sealed class Cell
    {
        public int R, C;
        public required TextBlock View;
        public required TextBox Edit;
        public List<InlineSpan> Spans = new();
        public bool Editing;
    }

    private const double CellMaxWidth = 320;

    private readonly NoteBlock _block;
    private readonly bool _chartData;
    private readonly Grid _grid = new();
    private readonly TextBlock _hint;
    private readonly List<Cell> _cells = new();
    private Cell? _active;

    /// <summary>"Make a chart from this table" was chosen.</summary>
    public event Action? ChartRequested;

    /// <param name="chartData">The data of a chart: its first row always names the series; no table-only menu items.</param>
    public NoteTableView(NoteBlock block, bool chartData = false)
    {
        _block = block;
        _chartData = chartData;
        var frame = new Border
        {
            BorderBrush = NoteEditor.Line,
            BorderThickness = new Thickness(1, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _grid,
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = frame,
        };
        _hint = new TextBlock
        {
            FontSize = 11,
            Foreground = NoteEditor.Muted,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Text = "Tab next cell · Enter next row (adds one at the end) · Shift+Enter new line · right-click for rows and columns · paste cells from a spreadsheet",
        };
        Child = new StackPanel { Children = { scroller, _hint } };
        Build();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty) _hint.IsVisible = IsKeyboardFocusWithin;
    }

    private List<List<string>> Cells => _block.Cells;
    private int Columns => Cells[0].Count;
    private bool HeaderRow => _block.Header || _chartData;

    // ---- building -------------------------------------------------------------------------

    /// <summary>(Re)creates every cell from the block; the table is kept rectangular with at least one cell.</summary>
    private void Build()
    {
        if (Cells.Count == 0) Cells.Add(new List<string> { "" });
        var columns = Math.Max(1, Cells.Max(r => r.Count));
        foreach (var r in Cells) while (r.Count < columns) r.Add("");

        _cells.Clear();
        _active = null;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        for (var c = 0; c < columns; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < Cells.Count; r++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++) _grid.Children.Add(CreateCell(r, c));
        }
    }

    private Border CreateCell(int r, int c)
    {
        var header = r == 0 && HeaderRow;
        var weight = header ? FontWeight.SemiBold : FontWeight.Normal;
        var view = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = CellMaxWidth,
            MinHeight = 19,
            FontSize = 14,
            FontWeight = weight,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        var edit = new TextBox
        {
            Classes = { "blk" },
            Text = Cells[r][c],
            MaxWidth = CellMaxWidth,
            FontSize = 14,
            FontWeight = weight,
            IsVisible = false,
            ContextFlyout = null,
        };
        var name = (_chartData ? "Data cell " : "Cell ") + $"{r + 1},{c + 1}";
        AutomationProperties.SetName(view, name);
        AutomationProperties.SetName(edit, name);
        var cell = new Cell { R = r, C = c, View = view, Edit = edit };
        _cells.Add(cell);
        Render(cell);

        var host = new Border
        {
            BorderBrush = NoteEditor.Line,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 4),
            MinWidth = 64,
            Background = header ? NoteEditor.CodeBack : Brushes.Transparent,
            Child = new Panel { Children = { view, edit } },
            ContextMenu = Menu(cell, clipboard: false),
        };
        edit.ContextMenu = Menu(cell, clipboard: true);
        Grid.SetRow(host, r);
        Grid.SetColumn(host, c);

        view.PointerPressed += (_, e) => View_PointerPressed(cell, e);
        host.PointerPressed += (_, e) =>
        {
            // a click in the cell's padding
            if (!e.GetCurrentPoint(host).Properties.IsLeftButtonPressed || cell.Editing) return;
            e.Handled = true;
            FocusCell(cell, int.MaxValue);
        };
        edit.AddHandler(KeyDownEvent, (_, e) => Edit_KeyDown(cell, e), RoutingStrategies.Tunnel);
        edit.TextChanged += (_, _) => Edit_TextChanged(cell);
        edit.GotFocus += (_, _) =>
        {
            _active = cell;
            cell.Editing = true;
        };
        edit.LostFocus += (_, _) => EndEdit(cell);
        edit.PastingFromClipboard += (_, e) => Edit_Paste(cell, e);
        return host;
    }

    private static void Render(Cell cell) => cell.Spans = NoteEditor.RenderInlines(cell.View, cell.Edit.Text ?? "");

    private Cell? CellAt(int r, int c) => _cells.FirstOrDefault(x => x.R == r && x.C == c);

    // ---- focus -------------------------------------------------------------------------

    public override void FocusEdge(bool atEnd)
    {
        var cell = atEnd ? _cells.LastOrDefault() : _cells.FirstOrDefault();
        if (cell is not null) FocusCell(cell, atEnd ? int.MaxValue : 0);
    }

    public override TextTarget? ActiveTarget()
    {
        if (_active is not { } cell || !_cells.Contains(cell)) return null;
        return new TextTarget(cell.Edit, true, text => SetText(cell, text), (s, e) => FocusCell(cell, s, e));
    }

    private void FocusCell(Cell cell, int caret, int selectTo = -1, bool retry = true)
    {
        if (TopLevel.GetTopLevel(cell.Edit) is null)
        {
            // a new table: its scroll viewer puts the cells in the tree only once it has a template
            if (retry) Dispatcher.UIThread.Post(() => FocusCell(cell, caret, selectTo, retry: false), DispatcherPriority.Loaded);
            return;
        }
        cell.Editing = true;
        cell.View.IsVisible = false;
        cell.Edit.IsVisible = true;
        _active = cell;
        cell.Edit.Focus();
        var length = cell.Edit.Text?.Length ?? 0;
        caret = Math.Clamp(caret, 0, length);
        cell.Edit.CaretIndex = caret;
        cell.Edit.SelectionStart = caret;
        cell.Edit.SelectionEnd = selectTo >= 0 ? Math.Clamp(selectTo, 0, length) : caret;
        cell.Edit.BringIntoView();
    }

    private void FocusAt(int r, int c, bool atEnd)
    {
        if (CellAt(r, c) is { } cell) FocusCell(cell, atEnd ? int.MaxValue : 0);
    }

    private static void EndEdit(Cell cell)
    {
        if (cell.Edit.ContextMenu?.IsOpen == true) return; // its menu took the focus
        cell.Editing = false;
        Render(cell);
        cell.View.IsVisible = true;
        cell.Edit.IsVisible = false;
    }

    private void SetText(Cell cell, string text)
    {
        Cells[cell.R][cell.C] = text; // first, so the late TextChanged sees no change
        cell.Edit.Text = text;
        if (!cell.Editing) Render(cell);
        RaiseChanged();
    }

    private void Edit_TextChanged(Cell cell)
    {
        var text = cell.Edit.Text ?? "";
        if (!_cells.Contains(cell) || text == Cells[cell.R][cell.C]) return;
        Cells[cell.R][cell.C] = text;
        if (!cell.Editing) Render(cell);
        RaiseChanged();
    }

    private void View_PointerPressed(Cell cell, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(cell.View).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        var rendered = cell.View.TextLayout.HitTestPoint(e.GetPosition(cell.View)).TextPosition;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && NoteEditor.OpenUrlAt(cell.Spans, rendered)) return;
        FocusCell(cell, NoteDocument.SourceIndex(cell.Spans, rendered, Cells[cell.R][cell.C].Length));
    }

    // ---- keyboard ----------------------------------------------------------------------

    private void Edit_KeyDown(Cell cell, KeyEventArgs e)
    {
        var box = cell.Edit;
        var length = box.Text?.Length ?? 0;
        var none = e.KeyModifiers == KeyModifiers.None;
        var collapsed = box.SelectionStart == box.SelectionEnd;
        switch (e.Key)
        {
            case Key.Tab when none || e.KeyModifiers == KeyModifiers.Shift:
                e.Handled = true;
                Step(cell, e.KeyModifiers == KeyModifiers.Shift ? -1 : 1, addRow: true);
                break;
            case Key.Enter when none:
                e.Handled = true;
                Vertical(cell, 1, addRow: true);
                break;
            case Key.Up when none:
                if (NoteEditor.CaretLine(box, out _) == 0) e.Handled = Vertical(cell, -1, addRow: false);
                break;
            case Key.Down when none:
                if (NoteEditor.CaretLine(box, out var lines) >= lines - 1) e.Handled = Vertical(cell, 1, addRow: false);
                break;
            case Key.Left when none && collapsed && box.CaretIndex == 0:
                e.Handled = Step(cell, -1, addRow: false);
                break;
            case Key.Right when none && collapsed && box.CaretIndex >= length:
                e.Handled = Step(cell, 1, addRow: false);
                break;
        }
    }

    /// <summary>To the next or previous cell in reading order; past the last one adds a row (Tab) or leaves the table.</summary>
    private bool Step(Cell cell, int direction, bool addRow)
    {
        var index = cell.R * Columns + cell.C + direction;
        if (index < 0) return Exit?.Invoke(-1) ?? false;
        if (index >= _cells.Count)
        {
            if (!addRow) return Exit?.Invoke(1) ?? false;
            InsertRow(Cells.Count, 0);
            return true;
        }
        FocusAt(index / Columns, index % Columns, atEnd: addRow || direction < 0);
        return true;
    }

    /// <summary>To the cell above or below; below the last row adds a row (Enter) or leaves the table.</summary>
    private bool Vertical(Cell cell, int direction, bool addRow)
    {
        var r = cell.R + direction;
        if (r < 0) return Exit?.Invoke(-1) ?? false;
        if (r >= Cells.Count)
        {
            if (!addRow) return Exit?.Invoke(1) ?? false;
            InsertRow(Cells.Count, cell.C);
            return true;
        }
        FocusAt(r, cell.C, atEnd: direction < 0);
        return true;
    }

    // ---- rows and columns ------------------------------------------------------------------

    private void Restructure(int focusRow, int focusColumn)
    {
        Build();
        RaiseChanged();
        FocusAt(Math.Clamp(focusRow, 0, Cells.Count - 1), Math.Clamp(focusColumn, 0, Columns - 1), atEnd: true);
    }

    private void InsertRow(int at, int focusColumn)
    {
        Cells.Insert(at, Enumerable.Repeat("", Columns).ToList());
        Restructure(at, focusColumn);
    }

    private void InsertColumn(int at, int focusRow)
    {
        foreach (var r in Cells) r.Insert(at, "");
        Restructure(focusRow, at);
    }

    private void DeleteRow(int r)
    {
        if (Cells.Count == 1)
        {
            if (!_chartData) RaiseDelete();
            return;
        }
        Cells.RemoveAt(r);
        Restructure(r, 0);
    }

    private void DeleteColumn(int c)
    {
        if (Columns == 1)
        {
            if (!_chartData) RaiseDelete();
            return;
        }
        foreach (var r in Cells) r.RemoveAt(c);
        Restructure(0, c);
    }

    private ContextMenu Menu(Cell cell, bool clipboard)
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var items = new List<Control>();
        if (clipboard)
        {
            items.Add(Item("Cut", () => cell.Edit.Cut()));
            items.Add(Item("Copy", () => cell.Edit.Copy()));
            items.Add(Item("Paste", () => cell.Edit.Paste()));
            items.Add(new Separator());
        }
        items.Add(Item("🔊 Speak", () => SpeechService.Speak(cell.Edit.SelectedText is { Length: > 0 } picked ? picked : Cells[cell.R][cell.C])));
        items.Add(Item("⏹ Stop speaking", SpeechService.Stop));
        items.Add(new Separator());
        items.Add(Item("Insert row above", () => InsertRow(cell.R, cell.C)));
        items.Add(Item("Insert row below", () => InsertRow(cell.R + 1, cell.C)));
        items.Add(Item("Insert column left", () => InsertColumn(cell.C, cell.R)));
        items.Add(Item("Insert column right", () => InsertColumn(cell.C + 1, cell.R)));
        items.Add(new Separator());
        items.Add(Item("Delete row", () => DeleteRow(cell.R)));
        items.Add(Item("Delete column", () => DeleteColumn(cell.C)));
        if (!_chartData)
        {
            items.Add(new Separator());
            var header = new MenuItem { Header = "Header row", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _block.Header };
            header.Click += (_, _) =>
            {
                _block.Header = !_block.Header;
                Restructure(cell.R, cell.C);
            };
            items.Add(header);
            items.Add(Item("Make a chart from this table", () => ChartRequested?.Invoke()));
            items.Add(Item("Delete table", RaiseDelete));
        }
        var menu = new ContextMenu();
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    // ---- pasting -----------------------------------------------------------------------

    /// <summary>Spreadsheet cells (tabs and lines) fill the table from this cell, growing it as needed; other text is typed in.</summary>
    private async void Edit_Paste(Cell cell, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        e.Handled = true;
        string? text;
        try
        {
            text = await clipboard.GetTextAsync();
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(text) || !_cells.Contains(cell)) return;
        var grid = NoteDocument.ParseDelimited(text.TrimEnd('\r', '\n'), '\t');
        if (grid.Count == 1 && grid[0].Count == 1)
        {
            var box = cell.Edit;
            var current = box.Text ?? "";
            var s = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
            var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), s, current.Length);
            var value = grid[0][0];
            SetText(cell, current[..s] + value + current[end..]);
            FocusCell(cell, s + value.Length);
            return;
        }
        for (var i = 0; i < grid.Count; i++)
        {
            var r = cell.R + i;
            while (Cells.Count <= r) Cells.Add(Enumerable.Repeat("", Columns).ToList());
            for (var j = 0; j < grid[i].Count; j++)
            {
                var c = cell.C + j;
                if (c >= Columns) foreach (var row in Cells) while (row.Count <= c) row.Add("");
                Cells[r][c] = grid[i][j].Trim();
            }
        }
        Restructure(cell.R, cell.C);
    }
}
