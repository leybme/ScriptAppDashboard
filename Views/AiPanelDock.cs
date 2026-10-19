using System;
using Avalonia.Controls;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Shows and hides the AI chat panel of the main window. The window's width is the host's business: it is told (<c>resize</c>)
/// before the panel changes, with the old and the new visibility.
/// </summary>
public sealed class AiPanelDock
{
    private readonly AiChatPanel _panel;
    private readonly Action<bool, bool>? _resize;

    /// <summary>The width of the chat panel while the tab content is open (the panel stretches while it is collapsed).</summary>
    public const double PanelWidth = 340;

    /// <summary>The width of the screen the window is on, in the window's units; null when unknown.</summary>
    public static double? AvailableWidth(Window window)
        => window.Screens.ScreenFromWindow(window) is { } screen ? screen.WorkingArea.Width / screen.Scaling : null;

    /// <summary>The panel with its margins.</summary>
    public double Room => PanelWidth + _panel.Margin.Left + _panel.Margin.Right;

    /// <param name="startVisible">The panel is shown from the start; the window's size already has room for it.</param>
    /// <param name="resize">Called with (was visible, will be visible) when the panel is about to be shown or hidden.</param>
    public AiPanelDock(AiChatPanel panel, bool startVisible = false, Action<bool, bool>? resize = null)
    {
        _panel = panel;
        _resize = resize;
        panel.IsVisible = startVisible;
    }

    public bool IsVisible => _panel.IsVisible;

    /// <summary>Raised after the panel was shown or hidden.</summary>
    public event Action? Changed;

    public void Toggle() => Show(!_panel.IsVisible);

    public void Show(bool show)
    {
        if (_panel.IsVisible == show) return;
        _resize?.Invoke(!show, show);
        _panel.IsVisible = show;
        Changed?.Invoke();
    }
}
