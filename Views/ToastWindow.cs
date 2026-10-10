using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Small pop-up in the bottom-right corner of the screen that does not take focus.
/// It closes by itself after a few seconds (not while the mouse is over it); several pop-ups stack upwards.
/// </summary>
public class ToastWindow : Window
{
    private const int MaxOpen = 4;
    private static readonly List<ToastWindow> Open = new();

    private readonly DispatcherTimer _closeTimer;

    public ToastWindow(string title, string body, Action? onOpen = null, int seconds = 10)
    {
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var accent = Color.Parse("#2B7FD9");

        SystemDecorations = SystemDecorations.None;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        Opacity = 0; // shown once positioned
        Background = new SolidColorBrush(dark ? Color.Parse("#2B2B2B") : Colors.White);
        Title = title;

        var close = new Button
        {
            Content = "✕",
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(close, "Dismiss");
        Avalonia.Automation.AutomationProperties.SetName(close, "Dismiss");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);

        var header = new DockPanel();
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(body))
        {
            content.Children.Add(new TextBlock
            {
                Text = body.Length > 400 ? body[..400] + "…" : body,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 8,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.85,
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm:ss") + (onOpen is null ? "" : "  ·  click to open"),
            FontSize = 11,
            Foreground = Brushes.Gray,
        });

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(12, 8, 6, 10),
            Child = content,
        };

        if (onOpen is not null)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            PointerPressed += (_, e) =>
            {
                if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
                onOpen();
                Close();
            };
        }

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _closeTimer.Tick += (_, _) => Close();
        PointerEntered += (_, _) => _closeTimer.Stop();
        PointerExited += (_, _) => _closeTimer.Start();

        Opened += (_, _) =>
        {
            Open.Add(this);
            LayoutAll();
            Opacity = 1;
            _closeTimer.Start();
        };
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            Open.Remove(this);
            LayoutAll();
        };
    }

    /// <summary>Shows a pop-up; the oldest one is dismissed when too many are open.</summary>
    public static ToastWindow ShowToast(string title, string body, Action? onOpen = null, int seconds = 10)
    {
        if (Open.Count >= MaxOpen) Open[0].Close();
        var toast = new ToastWindow(title, body, onOpen, seconds);
        toast.Show();
        return toast;
    }

    /// <summary>Stacks the open pop-ups upwards from the bottom-right corner of the primary screen.</summary>
    private static void LayoutAll()
    {
        if (Open.Count == 0) return;
        var screen = Open[0].Screens.Primary ?? Open[0].Screens.ScreenFromWindow(Open[0]);
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var margin = (int)(12 * scale);
        var bottom = area.Bottom - margin;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var toast = Open[i];
            var w = (int)(toast.Bounds.Width * scale);
            var h = (int)(toast.Bounds.Height * scale);
            bottom -= h;
            toast.Position = new PixelPoint(area.Right - w - margin, bottom);
            bottom -= margin / 2;
        }
    }
}
