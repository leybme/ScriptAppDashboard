using System;
using Avalonia.Controls;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Shows and hides the AI chat panel of a window. The window grows by the panel's width when it opens (and shrinks back
/// when it closes), so the content keeps its room.
/// </summary>
public sealed class AiPanelDock
{
    private readonly Window _window;
    private readonly AiChatPanel _panel;
    private bool _grew;

    public AiPanelDock(Window window, AiChatPanel panel)
    {
        _window = window;
        _panel = panel;
        panel.IsVisible = false;
        panel.CloseRequested += () => Show(false);
    }

    public bool IsVisible => _panel.IsVisible;

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
                _window.Width += room;
                _grew = true;
            }
            _panel.FocusInput();
        }
        else if (_grew)
        {
            _window.Width = Math.Max(_window.MinWidth, _window.Width - room);
            _grew = false;
        }
    }
}
