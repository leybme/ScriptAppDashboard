using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>A quick question to the AI from anywhere (tray, main window, global hotkey). Follow-up questions see the earlier ones.</summary>
public partial class QuickAskWindow : Window
{
    private App AppRef => (App)Application.Current!;
    private readonly List<(string Question, string Answer)> _history = new();
    private OpenWindow? _foreground;
    private CancellationTokenSource? _cancel;
    private string _lastQuestion = "";

    public QuickAskWindow()
    {
        InitializeComponent();
        PromptBox.AddHandler(KeyDownEvent, PromptBox_KeyDown, RoutingStrategies.Tunnel);
        HintText.Text = "Placeholders: " + string.Join("  ", AiService.Placeholders.Select(p => p.Name)) +
                        $"  ·  Sent to: {AiService.DisplayName}";
        Closed += (_, _) => _cancel?.Cancel();
    }

    /// <summary>Brings the window up for a new question. <paramref name="foreground"/> is the window that was in front
    /// before (when opened with the hotkey), so the question can refer to it.</summary>
    public void Summon(OpenWindow? foreground)
    {
        _foreground = foreground;
        WindowCheck.IsVisible = foreground is not null;
        WindowCheck.IsChecked = false;
        if (foreground is not null)
        {
            var title = foreground.Title.Length > 60 ? foreground.Title[..60] + "…" : foreground.Title;
            WindowCheck.Content = $"Include active window ({foreground.App}: {title})";
        }
        PromptBox.Focus();
        PromptBox.SelectAll();
    }

    private void PromptBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (_cancel is null) _ = AskAsync();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_cancel is not null) _cancel.Cancel();
            else Close();
        }
        base.OnKeyDown(e);
    }

    private async void Ask_Click(object? sender, RoutedEventArgs e)
    {
        if (_cancel is not null) _cancel.Cancel();
        else await AskAsync();
    }

    private async Task AskAsync()
    {
        var question = (PromptBox.Text ?? "").Trim();
        if (question.Length == 0)
        {
            StatusText.Text = "Type a question first.";
            return;
        }

        string? clipboard = null;
        if (ClipboardCheck.IsChecked == true && Clipboard is not null)
        {
            try { clipboard = await Clipboard.GetTextAsync(); } catch { /* clipboard busy */ }
        }
        var summonedFrom = _foreground;
        var foreground = WindowCheck.IsChecked == true ? summonedFrom : null;
        var history = _history.ToList();

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        AskButton.Content = "Stop";
        CopyButton.IsEnabled = NoteButton.IsEnabled = false;
        AnswerBox.Text = "";
        StatusText.Text = AiService.Asking();
        var result = await Task.Run(async () =>
        {
            var sb = new StringBuilder();
            if (history.Count > 0)
            {
                sb.AppendLine("Earlier in this conversation:");
                foreach (var (q, a) in history.TakeLast(4)) sb.AppendLine($"Q: {q}").AppendLine($"A: {a}").AppendLine();
                sb.AppendLine("Now answer the new question.");
                sb.AppendLine();
            }
            sb.AppendLine(AiService.ExpandPlaceholders(question, summonedFrom));
            if (foreground is not null) sb.AppendLine().AppendLine($"(The window I'm working in: {foreground.App} — {foreground.Title})");
            if (!string.IsNullOrWhiteSpace(clipboard)) sb.AppendLine().AppendLine("Text from my clipboard:").AppendLine("```").AppendLine(clipboard.Trim()).AppendLine("```");
            sb.AppendLine().Append("Answer concisely; use Markdown only where it helps.");
            return await AiService.AskAsync(sb.ToString(), token);
        });

        _cancel.Dispose();
        _cancel = null;
        if (!IsVisible) return;
        AskButton.Content = "Ask";
        AnswerBox.Text = result.Text;
        CopyButton.IsEnabled = NoteButton.IsEnabled = result.Ok;
        if (result.Ok)
        {
            _history.Add((question, result.Text));
            _lastQuestion = question;
            StatusText.Text = $"Answered in {result.Elapsed.TotalSeconds:0} s" + (_history.Count > 1 ? $" · {_history.Count} questions in this chat" : "");
            PromptBox.Text = "";
            PromptBox.Focus();
        }
        else
        {
            StatusText.Text = AiService.Failed(result);
        }
    }

    private void NewChat_Click(object? sender, RoutedEventArgs e)
    {
        _history.Clear();
        AnswerBox.Text = "";
        CopyButton.IsEnabled = NoteButton.IsEnabled = false;
        StatusText.Text = "New chat.";
        PromptBox.Focus();
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(AnswerBox.Text ?? "");
        StatusText.Text = "Answer copied.";
    }

    private void SaveNote_Click(object? sender, RoutedEventArgs e)
    {
        var title = _lastQuestion.ReplaceLineEndings(" ");
        var note = AiService.AnswerToNote(title.Length > 60 ? title[..60] + "…" : title, "#ai", AnswerBox.Text ?? "",
            "> " + _lastQuestion.ReplaceLineEndings(" "));
        try
        {
            AppRef.AddNote(note);
            StatusText.Text = $"Saved as the note “{note.Title}”.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save the note: " + ex.Message;
        }
    }
}
