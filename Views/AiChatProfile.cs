using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// What the shared <see cref="AiChatPanel"/> does while one tab is in front: its texts, what the AI is told about the tab,
/// the buttons under an answer, and (for the notes tab) how to apply edits. Each tab makes one profile and hands it to
/// <see cref="AiChatPanel.Use"/> whenever it is shown; the conversations stay in the panel, keyed by <see cref="Key"/>.
/// </summary>
public sealed class AiChatProfile
{
    /// <summary>The conversation on screen: the open note's id on the notes tab, a fixed id on the other tabs.</summary>
    public Guid Key { get; set; } = Guid.NewGuid();

    /// <summary>True on the notes tab: the AI reads the note and may propose edits. Off elsewhere: the AI only answers.</summary>
    public bool NoteMode { get; init; }

    public string Watermark { get; init; } = "Ask the AI… (Enter sends, Shift+Enter is a new line)";
    public string EmptyHint { get; init; } = "";
    public string ShareText { get; init; } = "Send what's on screen";
    public string ShareTip { get; init; } = "The AI sees what is on this tab. Untick to chat without sharing it.";

    /// <summary>The line under the panel title that says what the AI is looking at (change it with <see cref="SetLabel"/>).</summary>
    public string? ContextLabel { get; private set; }

    /// <summary>One-press requests above the input box.</summary>
    public (string Label, string Tip, Action Click)[] Quick { get; init; } = Array.Empty<(string, string, Action)>();

    /// <summary>The tab's content and the selection last captured in it.</summary>
    public Func<AiChatContext>? GetContext { get; init; }

    /// <summary>Notes: applies edit commands to the note if it is still as it was (the HTML passed in).</summary>
    public Func<string, List<NoteDocument.NoteEdit>, AiChatOutcome>? ApplyEdits { get; init; }

    /// <summary>Notes: puts a note back to <c>before</c> if it is still as <c>after</c>.</summary>
    public Func<string, string, AiChatOutcome>? Undo { get; init; }

    /// <summary>Notes: adds an answer to the note: target "insert" (below the selection token, or at the end), "selection" or "new". Returns a message.</summary>
    public Func<string, string, object?, string>? Insert { get; init; }

    /// <summary>Other tabs: buttons under an answer (action, label); <see cref="OnAnswerAction"/> handles a press and returns a status message.</summary>
    public (string Action, string Label)[]? AnswerActions { get; init; }

    public Func<string, string, Task<string>>? OnAnswerAction { get; init; }

    /// <summary>The pointer went down in the panel, before it takes the focus (the notes tab captures its selection here).</summary>
    public Action? Pressing { get; init; }

    public Action? SelectionCleared { get; init; }

    internal AiChatPanel? Panel { get; set; }

    public void SetLabel(string? text)
    {
        ContextLabel = text;
        Panel?.SetContextLabel(text);
    }

    /// <summary>Switches to another conversation of this profile (another note); a no-op for the panel while another tab is in front.</summary>
    public void SetKey(Guid id)
    {
        Key = id;
        Panel?.KeyChanged(this);
    }
}
