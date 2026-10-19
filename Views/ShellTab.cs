using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ScriptAppDashboard.Views;

/// <summary>
/// The content of one tab of the main window (<see cref="MainWindow"/>): apps, notes, captures, timers, work log, search,
/// settings. A tab is created the first time it is shown and then stays alive, hidden, while another tab is in front.
/// </summary>
public abstract class ShellTab : UserControl
{
    /// <summary>The window that holds the tab; set when the tab is added.</summary>
    public MainWindow Shell { get; internal set; } = null!;

    /// <summary>The AI chat shared by all tabs.</summary>
    protected AiChatPanel Chat => Shell.Chat;

    /// <summary>The window around the tab (owner of dialogs and file pickers).</summary>
    protected Window Host => (Window)TopLevel.GetTopLevel(this)!;

    protected IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;

    /// <summary>What the chat does while this tab is in front; null when the tab has no AI use.</summary>
    public virtual AiChatProfile? ChatProfile => null;

    /// <summary>The tab came to the front (also when the window is shown again). Refresh what may have changed meanwhile.</summary>
    public virtual void OnShown() { }

    /// <summary>Another tab came to the front, or the window is hidden or closing. Save what is unsaved.</summary>
    public virtual void OnHidden() { }
}
