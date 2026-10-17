using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>A read-only, formatted view of an AI answer written in Markdown (bold, lists, tables, code…), so the
/// answer reads well before it is used. It reuses the note parser and inline renderer of the note editor.</summary>
public static class MarkdownPreview
{
    private static readonly FontFamily Mono = new("Consolas,Menlo,monospace");

    /// <summary>The formatted blocks without a scroll viewer, for placing inside another scrolling list (the AI chat).</summary>
    public static Control BuildInline(string markdown)
    {
        var host = new StackPanel { Spacing = 6 };
        var number = 0;
        foreach (var block in NoteDocument.FromMarkdown(markdown))
        {
            number = block.Kind == BlockKind.Number ? number + 1 : 0;
            host.Children.Add(Row(block, number));
        }
        return host;
    }

    public static Control Build(string markdown)
    {
        var host = (StackPanel)BuildInline(markdown);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(4, 2, 12, 2), Child = host },
        };
    }

    private static TextBlock Text(string markup, double size = 14, FontWeight weight = FontWeight.Normal)
    {
        var view = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight };
        NoteEditor.RenderInlines(view, markup);
        return view;
    }

    private static Control Row(NoteBlock block, int number)
    {
        switch (block.Kind)
        {
            case BlockKind.Heading:
                return Text(block.Text, block.Level <= 1 ? 20 : block.Level == 2 ? 17 : 15, FontWeight.SemiBold);
            case BlockKind.Bullet or BlockKind.Number or BlockKind.Timed:
                var marker = new TextBlock
                {
                    Text = block.Kind == BlockKind.Number ? number + "." : "•",
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var item = new DockPanel { Margin = new Thickness(block.Indent * 20 + 8, 0, 0, 0) };
                DockPanel.SetDock(marker, Dock.Left);
                item.Children.Add(marker);
                item.Children.Add(Text(block.Kind == BlockKind.Timed && block.Stamp.Length > 0 ? block.Stamp + "  " + block.Text : block.Text));
                return item;
            case BlockKind.Quote:
                return new Border
                {
                    BorderBrush = NoteEditor.Line,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Child = Text(block.Text),
                };
            case BlockKind.Code or BlockKind.Raw:
                return new Border
                {
                    Background = NoteEditor.CodeBack,
                    Padding = new Thickness(8, 6),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock { Text = block.Text, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.Wrap },
                };
            case BlockKind.Divider:
                return new Border { Height = 1, Background = NoteEditor.Line, Margin = new Thickness(0, 6) };
            case BlockKind.Table or BlockKind.Chart:
                return Table(block);
            default:
                return Text(block.Text);
        }
    }

    private static Control Table(NoteBlock block)
    {
        var grid = new Grid();
        var rows = block.Cells;
        var columns = 0;
        foreach (var r in rows) columns = Math.Max(columns, r.Count);
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var header = r == 0 && (block.Header || block.Kind == BlockKind.Chart);
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = new Border
                {
                    BorderBrush = NoteEditor.Line,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Background = header ? NoteEditor.CodeBack : Brushes.Transparent,
                    Child = Text(rows[r][c], 14, header ? FontWeight.SemiBold : FontWeight.Normal),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }
}
