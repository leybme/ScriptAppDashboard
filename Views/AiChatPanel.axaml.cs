using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>What the chat needs to know about the note on screen when a message is sent.</summary>
public sealed record AiChatContext(string Html, string Title, string? SelectionText, object? SelectionToken, string AppInfo = "", Guid? NoteId = null);

/// <summary>The result of applying the AI's changes to (or undoing them on) the note; Before / After are its HTML.</summary>
public sealed record AiChatOutcome(bool Ok, string Message, string? Before = null, string? After = null);

/// <summary>
/// The AI chat beside a note. The AI answers questions, or proposes changes to the note as edit commands that are
/// shown first and applied only when the user presses Apply (Undo puts the note back). Each note has its own
/// conversation, kept until the notes window closes. The host (<see cref="NotesWindow"/>) supplies the note and applies results.
/// </summary>
public partial class AiChatPanel : UserControl
{
    private enum State { Pending, Answered, Edit, Failed, Stopped }

    private sealed class Turn
    {
        public string Question = "";
        public string? Selection;
        public object? SelectionToken;
        public State State = State.Pending;
        public string Answer = "";
        public string Preface = "";
        public List<NoteDocument.NoteEdit>? Edits;
        public string Html = ""; // the note when the question was sent: the edits' block numbers refer to it
        public double Seconds;
        public bool Applied;
        public string? Before, After;
        public CancellationTokenSource? Cancel;
        // a ready-made request from the AI menu: its own buttons, and what each one does with the answer
        public (string Action, string Label)[]? Buttons;
        public Func<string, string, Task<string>>? OnChoice;
    }

    private readonly Dictionary<Guid, List<Turn>> _chats = new();
    private Guid _noteId = Guid.Empty;
    private Turn? _running;

    private List<Turn> Turns => _chats.TryGetValue(_noteId, out var list) ? list : _chats[_noteId] = new List<Turn>();

    /// <summary>The note on screen and the selection last captured in it.</summary>
    public Func<AiChatContext>? GetContext { get; set; }

    /// <summary>Applies edit commands to the note if it is still as it was (the HTML passed in).</summary>
    public Func<string, List<NoteDocument.NoteEdit>, AiChatOutcome>? ApplyEdits { get; set; }

    /// <summary>Puts a note back to <c>before</c> if it is still as <c>after</c>.</summary>
    public Func<string, string, AiChatOutcome>? Undo { get; set; }

    /// <summary>Adds an answer to the note: target "insert" (below the selection token, or at the end), "selection" or "new". Returns a message.</summary>
    public Func<string, string, object?, string>? Insert { get; set; }

    public event Action? CloseRequested;
    public event Action? SelectionCleared;

    /// <summary>The pointer went down in the panel, before it takes the focus (and the note's text box loses its selection).</summary>
    public event Action? Pressing;

    public AiChatPanel()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => Pressing?.Invoke(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputBox.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        Render();
    }

    // ---- notes and selection ----------------------------------------------------------------

    /// <summary>Shows the conversation of another note. A question still waiting for its answer is stopped.</summary>
    public void SetNote(Guid id)
    {
        if (id == _noteId) return;
        Stop();
        _noteId = id;
        StatusText.Text = "";
        Render();
    }

    /// <summary>A new note got its real id when it was saved: its conversation moves with it.</summary>
    public void Adopt(Guid from, Guid to)
    {
        if (_chats.Remove(from, out var list) && !_chats.ContainsKey(to)) _chats[to] = list;
        if (_noteId == from) _noteId = to;
    }

    public void SetSelectionPreview(string? text)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        SelectionChip.IsVisible = line.Length > 0;
        SelectionText.Text = line.Length > 0 ? "Selected: “" + line + "”" : "";
    }

    /// <summary>Forgets the conversation of a note (a new, unsaved note starts with an empty one).</summary>
    public void Forget(Guid id)
    {
        if (!_chats.Remove(id) || id != _noteId) return;
        Stop();
        Render();
    }

    public void FocusInput() => InputBox.Focus();

    /// <summary>Stops a question that is waiting for its answer.</summary>
    public void Stop()
    {
        _running?.Cancel?.Cancel();
        _running = null;
        UpdateBusy();
    }

    private void UpdateBusy() => SendButton.Content = _running is null ? "Send" : "Stop";

    // ---- sending ----------------------------------------------------------------------------

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (e.KeyModifiers & KeyModifiers.Shift) != 0) return;
        e.Handled = true;
        _ = SendAsync();
    }

    private async void Send_Click(object? sender, RoutedEventArgs e) => await SendAsync();

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task SendAsync()
    {
        if (_running is not null)
        {
            Stop();
            return;
        }
        var message = (InputBox.Text ?? "").Trim();
        if (message.Length == 0 || GetContext?.Invoke() is not { } ctx) return;

        var withNote = NoteCheck.IsChecked == true;
        var units = withNote ? NoteDocument.Units(NoteDocument.Parse(ctx.Html)) : null;
        var numbered = units is null ? null
            : Cap(string.Join("\n\n", units.Select((u, i) => $"[{i + 1}]\n" + NotesService.BlocksToAiMarkdown(u))), 60000);
        var history = Turns.Where(t => t.State is State.Answered or State.Edit).TakeLast(4)
            .Select(t => (Cap(t.Question, 600), Cap(t.State == State.Edit ? $"(I changed the note: {t.Preface})" : t.Answer, 800)))
            .ToList();

        var turn = new Turn
        {
            Question = message,
            Selection = string.IsNullOrWhiteSpace(ctx.SelectionText) ? null : ctx.SelectionText,
            SelectionToken = ctx.SelectionToken,
            Html = ctx.Html,
        };
        InputBox.Text = "";
        StatusText.Text = "Searching your notes and screenshots…";
        var related = await Task.Run(() => ChatRelated.Build(message, ctx.NoteId, ctx.Html));
        var appInfo = related.Length == 0 ? ctx.AppInfo
            : ctx.AppInfo + "\n\nFound in the user's own notes and screenshots for this question (search results; answer from them and name the note or screenshot):\n" + related;
        await RunTurn(turn, token => AiService.AskAsync(AiPrompts.Chat(history, message, numbered, units?.Count ?? 0, turn.Selection, appInfo), token),
            allowEdits: numbered is not null);
    }

    /// <summary>
    /// Runs a ready-made request (an action of the AI menu: improve, shorten, summarise…) as a turn of the chat. The answer
    /// comes with <paramref name="buttons"/>; pressing one calls <paramref name="onChoice"/> with its action and the answer
    /// and shows the message it returns. False when the chat is still waiting for an answer.
    /// </summary>
    public bool RunRequest(string heading, Func<CancellationToken, Task<AiResult>> ask,
        (string Action, string Label)[] buttons, Func<string, string, Task<string>> onChoice)
    {
        if (_running is not null)
        {
            StatusText.Text = "Still waiting for the AI…";
            return false;
        }
        _ = RunTurn(new Turn { Question = heading, Buttons = buttons, OnChoice = onChoice }, ask, allowEdits: false);
        return true;
    }

    private async Task RunTurn(Turn turn, Func<CancellationToken, Task<AiResult>> ask, bool allowEdits)
    {
        turn.Cancel = new CancellationTokenSource();
        var list = Turns;
        list.Add(turn);
        _running = turn;
        StatusText.Text = "";
        UpdateBusy();
        Render();

        var token = turn.Cancel.Token;
        var result = await ask(token);
        turn.Cancel.Dispose();
        turn.Cancel = null;
        if (ReferenceEquals(_running, turn)) _running = null;
        turn.Seconds = result.Elapsed.TotalSeconds;
        if (token.IsCancellationRequested)
        {
            turn.State = State.Stopped;
        }
        else if (!result.Ok)
        {
            turn.State = State.Failed;
            turn.Answer = AiService.Failed(result);
        }
        else
        {
            var answer = AiService.StripOuterFence(result.Text);
            var edits = allowEdits ? NoteDocument.ParseEdits(answer) : null;
            if (edits is null)
            {
                turn.State = State.Answered;
                turn.Answer = answer;
            }
            else
            {
                turn.State = State.Edit;
                turn.Edits = edits;
                turn.Preface = NoteDocument.EditPreface(answer);
            }
        }
        UpdateBusy();
        if (ReferenceEquals(Turns, list)) Render();
    }

    // ---- drawing the conversation -----------------------------------------------------------

    private void Render()
    {
        Messages.Children.Clear();
        if (Turns.Count == 0)
        {
            Messages.Children.Add(new TextBlock
            {
                Text = "Ask a question about this note, or tell the AI what to change:\n\n"
                       + "• “Summarise this in three bullets”\n• “Turn the list into a table”\n• “Make a chart from the numbers”\n• “Translate it to English”\n\n"
                       + "A change is shown first. Nothing happens to the note until you press Apply.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 4),
            });
        }
        foreach (var turn in Turns)
        {
            Messages.Children.Add(UserBubble(turn));
            Messages.Children.Add(Reply(turn));
        }
        Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd());
    }

    private static Control UserBubble(Turn turn)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = turn.Question, TextWrapping = TextWrapping.Wrap });
        if (turn.Selection is not null)
            panel.Children.Add(new TextBlock
            {
                Text = "on: “" + Cap(turn.Selection.ReplaceLineEndings(" "), 60) + "”",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return new Border
        {
            Background = NoteEditor.CodeBack,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(24, 0, 0, 0),
            Child = panel,
        };
    }

    private Control Reply(Turn turn)
    {
        var panel = new StackPanel { Spacing = 6 };
        switch (turn.State)
        {
            case State.Pending:
                panel.Children.Add(Note(AiService.Asking()));
                break;
            case State.Stopped:
                panel.Children.Add(Note("Stopped."));
                break;
            case State.Failed:
                panel.Children.Add(new TextBlock { Text = turn.Answer, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed });
                break;
            case State.Answered:
                panel.Children.Add(MarkdownPreview.BuildInline(turn.Answer));
                panel.Children.Add(AnswerButtons(turn));
                panel.Children.Add(Note($"Answered in {turn.Seconds:0} s."));
                break;
            case State.Edit:
                if (turn.Preface.Length > 0) panel.Children.Add(MarkdownPreview.BuildInline(turn.Preface));
                panel.Children.Add(Changes(turn));
                panel.Children.Add(EditButtons(turn));
                panel.Children.Add(Note($"Answered in {turn.Seconds:0} s."));
                break;
        }
        return panel;
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

    private static Button SmallButton(string label, string tip, Action click, bool accent = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 6, 6) };
        if (accent) button.Classes.Add("accent");
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private Control AnswerButtons(Turn turn)
    {
        var row = new WrapPanel();
        if (turn.Buttons is not null)
        {
            foreach (var (action, label) in turn.Buttons)
                row.Children.Add(SmallButton(label, label, async () => StatusText.Text = await turn.OnChoice!(action, turn.Answer), accent: row.Children.Count == 0));
            AddCopyAndSpeak(row, turn);
            return row;
        }
        var below = turn.SelectionToken is not null;
        row.Children.Add(SmallButton(below ? "Insert below" : "Add to note", below ? "Insert the answer below the paragraph you were in" : "Add the answer at the end of the note",
            () => InsertAnswer("insert", turn)));
        if (turn.Selection is not null && turn.SelectionToken is not null)
            row.Children.Add(SmallButton("Replace selection", "Replace the selected text with the answer", () => InsertAnswer("selection", turn)));
        row.Children.Add(SmallButton("New note", "Save the answer as a new note", () => InsertAnswer("new", turn)));
        AddCopyAndSpeak(row, turn);
        return row;
    }

    private void AddCopyAndSpeak(Panel row, Turn turn)
    {
        row.Children.Add(SmallButton("Copy", "Copy the answer", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(turn.Answer);
                StatusText.Text = "Copied.";
            }
        }));
        row.Children.Add(SmallButton("🔊", "Read the answer aloud; click again to stop", () =>
        {
            if (SpeechService.IsSpeaking) SpeechService.Stop();
            else if (!SpeechService.SpeakMarkdown(turn.Answer)) StatusText.Text = "No speech voice is available on this system.";
        }));
    }

    private void InsertAnswer(string target, Turn turn)
        => StatusText.Text = Insert?.Invoke(target, turn.Answer, turn.SelectionToken) ?? "";

    /// <summary>The proposed changes, one entry per command, with the new content formatted.</summary>
    private static Control Changes(Turn turn)
    {
        var units = NoteDocument.Units(NoteDocument.Parse(turn.Html));
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var edit in turn.Edits!)
        {
            var range = edit.From == edit.To ? $"block {edit.From}" : $"blocks {edit.From}–{edit.To}";
            var title = edit.Op switch
            {
                "replace" => "Replace " + range,
                "delete" => "Delete " + range,
                _ => edit.From == 0 ? "Add at the top" : $"Add after block {edit.From}",
            };
            list.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12 });
            if (edit.Op == "delete")
            {
                var old = string.Join(" ", Enumerable.Range(edit.From, Math.Max(1, edit.To - edit.From + 1))
                    .Where(n => n >= 1 && n <= units.Count)
                    .Select(n => NotesService.BlocksToAiMarkdown(units[n - 1]).ReplaceLineEndings(" ")));
                list.Children.Add(Note(Cap(old, 90)));
            }
            else
            {
                list.Children.Add(new Border
                {
                    BorderBrush = NoteEditor.Line,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                    Child = MarkdownPreview.BuildInline(edit.Markdown),
                });
            }
        }
        return new Expander
        {
            Header = turn.Edits.Count == 1 ? "1 change" : $"{turn.Edits.Count} changes",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = list,
        };
    }

    private Control EditButtons(Turn turn)
    {
        var row = new WrapPanel();
        if (turn.Applied)
        {
            row.Children.Add(new TextBlock { Text = "✓ Applied", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) });
            if (turn.Before is not null && turn.After is not null)
                row.Children.Add(SmallButton("Undo", "Put the note back as it was before this change", () => UndoEdit(turn)));
            return row;
        }
        row.Children.Add(SmallButton("Apply", "Make these changes to the note (Undo is offered afterwards)", () => ApplyEdit(turn), accent: true));
        return row;
    }

    private void ApplyEdit(Turn turn)
    {
        if (ApplyEdits?.Invoke(turn.Html, turn.Edits!) is not { } outcome) return;
        StatusText.Text = outcome.Message;
        if (!outcome.Ok) return;
        turn.Applied = true;
        turn.Before = outcome.Before;
        turn.After = outcome.After;
        Render();
    }

    private void UndoEdit(Turn turn)
    {
        if (Undo?.Invoke(turn.Before!, turn.After!) is not { } outcome) return;
        StatusText.Text = outcome.Message;
        if (!outcome.Ok) return;
        turn.Applied = false;
        turn.Before = turn.After = null;
        // the note is as it was when this question was asked: the same changes can be applied again
        Render();
    }

    private void NewChat_Click(object? sender, RoutedEventArgs e)
    {
        Stop();
        Turns.Clear();
        StatusText.Text = "New chat.";
        Render();
        InputBox.Focus();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        SetSelectionPreview(null);
        SelectionCleared?.Invoke();
    }
}
