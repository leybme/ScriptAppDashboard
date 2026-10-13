using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>
/// Asks the AI and shows the answer in an editable box, with buttons for what to do with it
/// ("Replace selection", "Save as note", …). Returns the chosen action and the (edited) text, or null.
/// </summary>
public partial class AiResultWindow : Window
{
    private readonly Func<CancellationToken, Task<AiResult>> _ask;
    private readonly List<Button> _actions = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Button _stopButton = new() { Content = "Stop" };
    private CancellationTokenSource? _cancel;
    private DateTime _started;
    private (string Action, string Text)? _result;

    public AiResultWindow() : this("AI", _ => Task.FromResult(new AiResult(false, "", TimeSpan.Zero))) { }

    public AiResultWindow(string heading, Func<CancellationToken, Task<AiResult>> ask, params (string Action, string Label)[] actions)
    {
        InitializeComponent();
        _ask = ask;
        Title = heading;
        HeadingText.Text = heading;

        _stopButton.Click += (_, _) => _cancel?.Cancel();
        ButtonHost.Children.Insert(0, _stopButton);
        for (var i = 0; i < actions.Length; i++)
        {
            var (action, label) = actions[i];
            var button = new Button { Content = label, IsEnabled = false };
            if (i == 0) button.Classes.Add("accent");
            button.Click += (_, _) =>
            {
                _result = (action, ResultBox.Text ?? "");
                Close();
            };
            _actions.Add(button);
            ButtonHost.Children.Add(button);
        }
        var close = new Button { Content = actions.Length == 0 ? "Close" : "Cancel" };
        close.Click += (_, _) => Close();
        ButtonHost.Children.Add(close);

        _clock.Tick += (_, _) => StatusText.Text = $"Asking {AiService.DisplayName}…  {(int)(DateTime.Now - _started).TotalSeconds} s";
        Opened += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            _cancel?.Cancel();
            SpeechService.Stop();
            _clock.Stop();
        };
    }

    /// <summary>Shows the dialog and waits for the user's choice.</summary>
    public static async Task<(string Action, string Text)?> Show(Window owner, string heading,
        Func<CancellationToken, Task<AiResult>> ask, params (string Action, string Label)[] actions)
    {
        var window = new AiResultWindow(heading, ask, actions);
        await window.ShowDialog(owner);
        return window._result;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private async Task RunAsync()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        SetBusy(true);
        ResultBox.Text = "";
        AiResult result;
        try
        {
            result = await _ask(_cancel.Token);
        }
        catch (Exception ex)
        {
            result = new AiResult(false, ex.Message, TimeSpan.Zero);
        }
        if (!IsVisible) return;
        SetBusy(false);
        ResultBox.Text = result.Text;
        foreach (var button in _actions) button.IsEnabled = result.Ok;
        CopyButton.IsEnabled = result.Ok;
        SpeakButton.IsEnabled = result.Ok;
        EditButton.IsEnabled = result.Ok;
        EditButton.IsVisible = result.Ok;
        ShowPreview(result.Ok);
        StatusText.Text = result.Ok ? $"Answered in {result.Elapsed.TotalSeconds:0} s." : "The AI request failed — see the message above.";
    }

    private void SetBusy(bool busy)
    {
        _started = DateTime.Now;
        _stopButton.IsVisible = busy;
        RetryButton.IsEnabled = !busy;
        ResultBox.IsReadOnly = busy;
        ResultBox.Watermark = busy ? "Waiting for the answer…" : null;
        if (busy)
        {
            ShowPreview(false);
            EditButton.IsVisible = false;
            foreach (var button in _actions) button.IsEnabled = false;
            CopyButton.IsEnabled = false;
            SpeakButton.IsEnabled = false;
            StatusText.Text = $"Asking {AiService.DisplayName}…";
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await RunAsync();

    /// <summary>Shows the answer formatted (read-only) or as editable Markdown text.</summary>
    private void ShowPreview(bool preview)
    {
        PreviewHost.Content = preview ? MarkdownPreview.Build(ResultBox.Text ?? "") : null;
        PreviewHost.IsVisible = preview;
        ResultBox.IsVisible = !preview;
        EditButton.Content = preview ? "✎ Edit" : "Preview";
        if (!preview && ResultBox.IsEnabled) ResultBox.Focus();
    }

    private void Edit_Click(object? sender, RoutedEventArgs e) => ShowPreview(!PreviewHost.IsVisible);

    private void Speak_Click(object? sender, RoutedEventArgs e)
    {
        if (SpeechService.IsSpeaking)
        {
            SpeechService.Stop();
            return;
        }
        var text = ResultBox.SelectedText is { Length: > 0 } picked ? picked : ResultBox.Text ?? "";
        // Markdown marks would be read as symbols
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[*`#|]|^\s*-\s+|^\s*-{3,}\s*$", " ", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!SpeechService.Speak(text)) StatusText.Text = "No speech voice is available on this system.";
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(ResultBox.Text ?? "");
        StatusText.Text = "Copied to the clipboard.";
    }
}
