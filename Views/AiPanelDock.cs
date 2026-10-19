using System;
using Avalonia.Controls;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Shows and hides the AI chat panel of the main window. The window grows by the panel's width when it opens (as far as the
/// screen allows) and shrinks back when it closes, so the tab in front keeps its room.
/// </summary>
public sealed class AiPanelDock
{
    private readonly Window _window;
    private readonly AiChatPanel _panel;
    private double _grew;

    /// <param name="startVisible">The panel is shown from the start; the window's size already has room for it.</param>
    public AiPanelDock(Window window, AiChatPanel panel, bool startVisible = false)
    {
        _window = window;
        _panel = panel;
        panel.IsVisible = startVisible;
        if (startVisible) _grew = panel.Width + panel.Margin.Left + panel.Margin.Right;
        panel.CloseRequested += () => Show(false);
    }

    public bool IsVisible => _panel.IsVisible;

    /// <summary>Raised after the panel was shown or hidden.</summary>
    public event Action? Changed;

    public void Toggle() => Show(!_panel.IsVisible);

    public void Show(bool show)
    {
        if (_panel.IsVisible == show) return;
        _panel.IsVisible = show;
        var room = _panel.Width + _panel.Margin.Left + _panel.Margin.Right;
        if (show)
        {
            if (_window.WindowState == WindowState.Normal)
            {
                var available = _window.Screens.ScreenFromWindow(_window)?.WorkingArea.Width / (_window.Screens.ScreenFromWindow(_window)?.Scaling ?? 1);
                var target = available is { } a ? Math.Min(_window.Width + room, a) : _window.Width + room;
                _grew = Math.Max(0, target - _window.Width);
                _window.Width = target;
            }
        }
        else if (_grew > 0)
        {
            _window.Width = Math.Max(_window.MinWidth, _window.Width - _grew);
            _grew = 0;
        }
        Changed?.Invoke();
    }
}
