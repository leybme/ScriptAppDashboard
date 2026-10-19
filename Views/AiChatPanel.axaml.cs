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

/// <summary>
/// What the chat needs to know about the note on screen when a message is sent. In a window that is not the notes editor
/// (<see cref="AiChatPanel.NoteMode"/> off) <c>Html</c> is the text the window shows and <c>Title</c> names what the user is looking at.
/// </summary>
public sealed record AiChatContext(string Html, string Title, string? SelectionText, object? SelectionToken, string AppInfo = "", Guid? NoteId = null);

/// <summary>The result of applying the AI's changes to (or undoing them on) the note; Before / After are its HTML.</summary>
public sealed record AiChatOutcome(bool Ok, string Message, string? Before = null, string? After = null);

/// <summary>
/// The AI chat panel of the shell, shared by every tab: the active tab hands it an <see cref="AiChatProfile"/>
/// (<see cref="Use"/>). The AI answers questions; on the notes tab it also proposes changes to the note as edit commands
/// that are shown first and applied only when the user presses Apply (Undo puts the note back). Each note, and each other
/// tab, has its own conversation, kept until the app closes; several conversations can wait for an answer at the same time.
/// </summary>
public partial class AiChatPanel : UserControl
{
    private enum State { Pending, Answered, Edit, Failed, Stopped, Search }

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
        // while waiting: when it started, the answer so far, and the two text blocks that show them
        public DateTime Started = DateTime.Now;
        public string Partial = "";
        public TextBlock? StatusBlock;
        public Border? PartialHost;
        public string Rendered = "";
        public DateTime LastRender = DateTime.MinValue;
        // a ready-made request from the AI menu: its own buttons, and what each one does with the answer
        public (string Action, string Label)[]? Buttons;
        public Func<string, string, Task<string>>? OnChoice;
        // an "@" quick search: what it found (null while it runs)
        public SearchResults? Search;
    }

    private readonly Dictionary<Guid, List<Turn>> _chats = new();
    private Guid _noteId = Guid.Empty; // key of the conversation on screen
    private AiChatProfile _profile = new();

    private List<Turn> Turns => _chats.TryGetValue(_noteId, out var list) ? list : _chats[_noteId] = new List<Turn>();

    /// <summary>The question the conversation on screen is waiting for, if any.</summary>
    private Turn? Running => Turns.FirstOrDefault(t => t.State == State.Pending);

    private bool NoteMode => _profile.NoteMode;

    /// <summary>Makes the panel work for the tab in front: its texts, quick buttons, context and conversation.</summary>
    public void Use(AiChatProfile profile)
    {
        if (!ReferenceEquals(profile, _profile))
        {
            _profile.Panel = null;
            _profile = profile;
            profile.Panel = this;
            StatusText.Text = "";
            SetSelectionPreview(null);
        }
        _noteId = profile.Key;
        InputBox.Watermark = profile.Watermark;
        NoteCheck.Content = profile.ShareText;
        ToolTip.SetTip(NoteCheck, profile.ShareTip);
        SetContextLabel(profile.ContextLabel);
        QuickActions.Children.Clear();
        foreach (var (label, tip, click) in profile.Quick) QuickActions.Children.Add(SmallButton(label, tip, click));
        QuickActions.Children.Add(SmallButton("@ Search", "Quick search of your apps, notes, pictures and captures: type @ and a word, then Enter", () => Prefill("@")));
        QuickActions.IsVisible = true;
        UpdateBusy();
        Render();
    }

    internal void KeyChanged(AiChatProfile profile)
    {
        if (!ReferenceEquals(profile, _profile) || _noteId == profile.Key) return;
        _noteId = profile.Key;
        StatusText.Text = "";
        UpdateBusy();
        Render();
    }

    internal void SetContextLabel(string? text)
    {
        ContextLabel.Text = text ?? "";
        ContextLabel.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>Puts text in the input box (the user finishes the sentence) and focuses it.</summary>
    public void Prefill(string text)
    {
        InputBox.Text = text;
        InputBox.Focus();
        InputBox.CaretIndex = text.Length;
    }

    /// <summary>Sends a message as if the user typed it.</summary>
    public Task Ask(string message)
    {
        InputBox.Text = message;
        return SendAsync();
    }

    public AiChatPanel()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => _profile.Pressing?.Invoke(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputBox.AddHandler(KeyDownEvent, InputBox_KeyDown, RoutingStrategies.Tunnel);
        InputBox.GotFocus += (_, _) => WarmUp();
        InputBox.TextChanged += (_, _) => WarmUp();
        _heartbeat.Tick += (_, _) => Heartbeat();
        AttachedToVisualTree += (_, _) => _heartbeat.Start();
        DetachedFromVisualTree += (_, _) => _heartbeat.Stop();
        Render();
    }

    // ---- heartbeat: seconds counter while waiting, and a warm AI process while the chat is in use -----

    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastWarm = DateTime.MinValue;
    private int _beats;

    /// <summary>Has the AI command start in the background, so the question being typed doesn't wait for it to boot.</summary>
    private void WarmUp()
    {
        if ((DateTime.Now - _lastWarm).TotalSeconds < 15) return;
        _lastWarm = DateTime.Now;
        AiService.Warm();
    }

    private void Heartbeat()
    {
        foreach (var turn in Turns.Where(t => t.State == State.Pending)) ShowProgress(turn);
        // every two minutes, while this chat is on screen in the window in use, make sure a warm process is ready
        if (++_beats % 120 == 0 && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is Window { IsActive: true })
        {
            _lastWarm = DateTime.MinValue;
            WarmUp();
        }
    }

    /// <summary>Updates the waiting line ("Asking Claude… 7 s") and the answer so far of a running turn.</summary>
    private void ShowProgress(Turn turn)
    {
        var seconds = (int)(DateTime.Now - turn.Started).TotalSeconds;
        if (turn.StatusBlock is { } status)
            status.Text = turn.Partial.Length == 0 ? $"{AiService.Asking()} {seconds} s" : $"✍️ Writing… {seconds} s";
        // the answer so far, formatted like the finished one (Markdown), at most every 120 ms; edit commands of a note change stay plain
        if (turn.PartialHost is { } host && turn.Rendered != turn.Partial && (DateTime.Now - turn.LastRender).TotalMilliseconds >= 120)
        {
            var atBottom = Scroll.Extent.Height - (Scroll.Offset.Y + Scroll.Viewport.Height) < 40;
            turn.Rendered = turn.Partial;
            turn.LastRender = DateTime.Now;
            host.Child = turn.Partial.Contains("@@")
                ? new TextBlock { Text = turn.Partial, TextWrapping = TextWrapping.Wrap }
                : MarkdownPreview.BuildInline(turn.Partial);
            host.IsVisible = turn.Partial.Length > 0;
            // follow the new text only while the reader is at the bottom; someone who scrolled up to read is left alone
            if (atBottom) Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }
    private void OnPartial(Turn turn, string text)
    {
        turn.Partial = text; // read on the UI thread by the heartbeat and by ShowProgress below
        Dispatcher.UIThread.Post(() => { if (turn.State == State.Pending) ShowProgress(turn); }, DispatcherPriority.Background);
    }

    // ---- notes and selection ----------------------------------------------------------------

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

    /// <summary>Forgets the conversation of a note (a new, unsaved note starts with an empty one); questions waiting in it are stopped.</summary>
    public void Forget(Guid id)
    {
        if (!_chats.Remove(id, out var list)) return;
        foreach (var turn in list) turn.Cancel?.Cancel();
        if (id != _noteId) return;
        UpdateBusy();
        Render();
    }

    public void FocusInput() => InputBox.Focus();

    /// <summary>Stops the questions of the conversation on screen that are waiting for their answer.</summary>
    public void Stop()
    {
        foreach (var turn in Turns.Where(t => t.State == State.Pending)) turn.Cancel?.Cancel();
        UpdateBusy();
    }

    private void UpdateBusy() => SendButton.Content = Running is null ? "Send" : "Stop";

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
        var typed = (InputBox.Text ?? "").Trim();
        if (typed.StartsWith('@'))
        {
            // "@word": a quick search of the apps, notes, pictures and captures, no AI involved (works while an answer is pending)
            InputBox.Text = "";
            await QuickSearchAsync(typed[1..].Trim());
            return;
        }
        if (Running is not null)
        {
            Stop();
            return;
        }
        var message = typed;
        if (message.Length == 0 || _profile.GetContext?.Invoke() is not { } ctx) return;

        var list = Turns; // the conversation this question belongs to, even if another tab is shown while it is searched
        var withNote = NoteCheck.IsChecked == true;
        var history = list.Where(t => t.State is State.Answered or State.Edit).TakeLast(4)
            .Select(t => (Cap(t.Question, 600), Cap(t.State == State.Edit ? $"(I changed the note: {t.Preface})" : t.Answer, 800)))
            .ToList();

        if (!NoteMode)
        {
            InputBox.Text = "";
            StatusText.Text = "Searching your notes and screenshots…";
            var found = await Task.Run(() => ChatRelated.Build(message, null, ""));
            var info = found.Length == 0 ? ctx.AppInfo
                : ctx.AppInfo + "\n\nFound in the user's own notes and screenshots for this question (search results; answer from them and name the note or screenshot):\n" + found;
            var shared = withNote ? Cap(ctx.Html, 60000) : null;
            await RunTurn(list, new Turn { Question = message }, (token, partial) => AiService.AskStreamAsync(AiPrompts.ChatAbout(history, message, ctx.Title, shared, info), partial, token),
                allowEdits: false);
            return;
        }

        var units = withNote ? NoteDocument.Units(NoteDocument.Parse(ctx.Html)) : null;
        var numbered = units is null ? null
            : Cap(string.Join("\n\n", units.Select((u, i) => $"[{i + 1}]\n" + NotesService.BlocksToAiMarkdown(u))), 60000);

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
        await RunTurn(list, turn, (token, partial) => AiService.AskStreamAsync(AiPrompts.Chat(history, message, numbered, units?.Count ?? 0, turn.Selection, appInfo), partial, token),
            allowEdits: numbered is not null);
    }

    /// <summary>
    /// Runs a ready-made request (an action of the AI menu: improve, shorten, summarise…) as a turn of the chat. The answer
    /// comes with <paramref name="buttons"/>; pressing one calls <paramref name="onChoice"/> with its action and the answer
    /// and shows the message it returns. False when this conversation is still waiting for an answer.
    /// </summary>
    public bool RunRequest(string heading, Func<CancellationToken, Action<string>, Task<AiResult>> ask,
        (string Action, string Label)[] buttons, Func<string, string, Task<string>> onChoice)
    {
        if (Running is not null)
        {
            StatusText.Text = "Still waiting for the AI…";
            return false;
        }
        _ = RunTurn(Turns, new Turn { Question = heading, Buttons = buttons, OnChoice = onChoice }, ask, allowEdits: false);
        return true;
    }

    private async Task RunTurn(List<Turn> list, Turn turn, Func<CancellationToken, Action<string>, Task<AiResult>> ask, bool allowEdits)
    {
        turn.Started = DateTime.Now;
        turn.Cancel = new CancellationTokenSource();
        list.Add(turn);
        StatusText.Text = "";
        if (ReferenceEquals(Turns, list))
        {
            UpdateBusy();
            Render();
        }

        var token = turn.Cancel.Token;
        var result = await ask(token, text => OnPartial(turn, text));
        turn.StatusBlock = null;
        turn.PartialHost = null;
        turn.Cancel.Dispose();
        turn.Cancel = null;
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
        _lastWarm = DateTime.MinValue; // the standby was used up: have the next one ready for a follow-up
        WarmUp();
    }

    // ---- drawing the conversation -----------------------------------------------------------

    private void Render()
    {
        Messages.Children.Clear();
        if (Turns.Count == 0)
        {
            Messages.Children.Add(new TextBlock
            {
                Text = _profile.EmptyHint + "\n\nQuick search, no AI: type @ and a word to find your apps, notes (and the words in their pictures), screenshots and work-log entries, e.g. @invoice.",
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
                turn.StatusBlock = Note(AiService.Asking());
                turn.PartialHost = new Border { IsVisible = false };
                turn.Rendered = ""; // a new host after a re-render: draw the answer so far again
                panel.Children.Add(turn.StatusBlock);
                panel.Children.Add(turn.PartialHost);
                ShowProgress(turn);
                break;
            case State.Search:
                panel.Children.Add(SearchReply(turn));
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
        var buttons = turn.Buttons;
        var onChoice = turn.OnChoice;
        if (buttons is null && !NoteMode)
        {
            buttons = _profile.AnswerActionsFor?.Invoke(turn.Answer) ?? _profile.AnswerActions ?? Array.Empty<(string, string)>();
            onChoice = _profile.OnAnswerAction;
        }
        if (buttons is not null)
        {
            foreach (var (action, label) in buttons)
                row.Children.Add(SmallButton(label, label, async () => StatusText.Text = await onChoice!(action, turn.Answer), accent: row.Children.Count == 0));
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
        => StatusText.Text = _profile.Insert?.Invoke(target, turn.Answer, turn.SelectionToken) ?? "";

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
        if (_profile.ApplyEdits?.Invoke(turn.Html, turn.Edits!) is not { } outcome) return;
        StatusText.Text = outcome.Message;
        if (!outcome.Ok) return;
        turn.Applied = true;
        turn.Before = outcome.Before;
        turn.After = outcome.After;
        Render();
    }

    private void UndoEdit(Turn turn)
    {
        if (_profile.Undo?.Invoke(turn.Before!, turn.After!) is not { } outcome) return;
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


    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        SetSelectionPreview(null);
        _profile.SelectionCleared?.Invoke();
    }
}
