using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

/// <summary>All captures as a grid of thumbnails, searchable by the words written on them, their title / tags /
/// description, the window they came from and the text found in the pictures.</summary>
public partial class CapturesView : ShellTab
{
    private App AppRef => (App)Application.Current!;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _version;

    public CapturesView()
    {
        InitializeComponent();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await ReloadAsync();
        };
        QueryBox.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        Action<Guid> changed = _ => Dispatcher.UIThread.Post(() => { _debounce.Stop(); _debounce.Start(); });
        CaptureService.Changed += changed;
        RegionButton.IsVisible = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        ScreenButton.IsVisible = OperatingSystem.IsWindows();
        _chatProfile = new AiChatProfile
        {
            Watermark = "Ask about your screenshots… (Enter sends, Shift+Enter is a new line)",
            EmptyHint = "Ask about your captures:\n\n• “Find the screenshot with the error about the license”\n• “Which captures did I take of the invoice page?”\n\n" +
                        "The AI also searches the words found in your pictures.",
            ShareText = "Send my capture list",
            ShareTip = "The AI sees each capture's title, tags, description and the window it came from. Untick to chat without sharing it.",
            GetContext = () => new AiChatContext(CapturesText(), "the screenshots (captures)", null, null, AiPrompts.AppGuide),
            AnswerActions = new[] { ("note", "Save as note") },
            OnAnswerAction = (_, text) => Task.FromResult(SaveAnswer("Captures", "#captures #ai", text)),
        };
        _chatProfile.SetLabel("Captures");
    }

    private readonly AiChatProfile _chatProfile;

    public override AiChatProfile? ChatProfile => _chatProfile;

    public override void OnShown() => _ = ReloadAsync();

    /// <summary>What the chat is told: one line per capture (the newest 80).</summary>
    private static string CapturesText()
    {
        var lines = CaptureService.Load().OrderByDescending(c => c.Created).Take(80).Select(c =>
            $"- {c.Created:yyyy-MM-dd HH:mm} “{(c.Title.Length > 0 ? c.Title : "(untitled)")}”" +
            (c.Tags.Length > 0 ? $" {c.Tags}" : "") +
            (c.SourceApp.Length > 0 ? $" — from {c.SourceApp}" : "") +
            (c.Description.Length > 0 ? $" — {c.Description}" : ""));
        return string.Join("\n", lines);
    }

    private string SaveAnswer(string title, string tags, string text)
    {
        var note = AiService.AnswerToNote(title, tags, text);
        try
        {
            AppRef.AddNote(note);
            return $"Saved as the note “{note.Title}”.";
        }
        catch (Exception ex)
        {
            return "Could not save the note: " + ex.Message;
        }
    }

    public void FocusSearch()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private async Task ReloadAsync()
    {
        var version = ++_version;
        var query = (QueryBox.Text ?? "").Trim();
        var (items, total) = await Task.Run(() =>
        {
            var all = CaptureService.Load();
            var list = query.Length == 0
                ? all.Select(c => new CaptureHit(c, "", "", 0)).ToList()
                : CaptureService.Search(all, query);
            return (list, all.Count);
        });
        if (version != _version) return;

        Cards.Children.Clear();
        SummaryText.Text = total == 0
            ? $"No captures yet. Press {(string.IsNullOrWhiteSpace(AppRef.Config.CaptureHotkey) ? "“Capture region”" : AppRef.Config.CaptureHotkey)} anywhere, or use the buttons above."
            : query.Length == 0 ? $"{total} capture(s)."
            : $"{items.Count} of {total} capture(s) match.";
        foreach (var hit in items.Take(300))
            Cards.Children.Add(Card(hit, query));
    }

    private Control Card(CaptureHit hit, string query)
    {
        var c = hit.Capture;
        var image = new Image { Stretch = Stretch.UniformToFill, Height = 140, Width = 240 };
        var thumb = new Border
        {
            Child = image,
            Width = 240,
            Height = 140,
            ClipToBounds = true,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        };
        _ = LoadThumbnailAsync(image, CaptureService.BestImage(c.Id));

        var panel = new StackPanel { Spacing = 2, Width = 240 };
        panel.Children.Add(thumb);
        panel.Children.Add(new TextBlock { Text = CaptureService.DisplayTitle(c), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
        var detail = hit.Snippet.Length > 0 ? $"{hit.Where}: {hit.Snippet}"
            : c.Annotations.Any(a => a.Text.Length > 0) ? CaptureService.AnnotationText(c).Replace('\n', ' ')
            : c.Description;
        if (detail.Length > 0)
            panel.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{c.Created:yyyy-MM-dd HH:mm}   {c.Tags}".TrimEnd(), FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });

        var button = new Button
        {
            Content = panel,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Avalonia.Automation.AutomationProperties.SetName(button, "Open capture " + CaptureService.DisplayTitle(c));
        ToolTip.SetTip(button, "Open in the editor");
        button.Click += (_, _) => AppRef.ShowCapture(c.Id);
        return button;
    }

    private static async Task LoadThumbnailAsync(Image image, string path)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 480);
            });
            image.Source = bitmap;
        }
        catch
        {
            // missing or still being synced: the card shows an empty frame
        }
    }

    private async void Region_Click(object? sender, RoutedEventArgs e)
    {
        await AppRef.CaptureAsync(); // hides and restores every window itself
    }

    private async void Screen_Click(object? sender, RoutedEventArgs e)
    {
        await AppRef.CaptureAsync(fullScreen: true);
    }

    /// <summary>Annotate a picture file.</summary>
    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Open a picture to annotate",
                AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Pictures") { Patterns = NotesService.ImageExtensions.Select(x => "*" + x).ToArray() } },
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            AppRef.OpenCaptureFrom(Pixels.FromImage(stream), new OpenWindow("", files[0].Name));
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Could not open the picture: " + ex.Message;
        }
    }

    /// <summary>Annotate the picture on the clipboard (a copied image, a screenshot from another tool).</summary>
    private void Paste_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ClipboardImageService.TryGetImage() is not { } clip)
            {
                SummaryText.Text = "There is no picture on the clipboard.";
                return;
            }
            using var ms = new MemoryStream(clip.Data);
            AppRef.OpenCaptureFrom(Pixels.FromImage(ms), null);
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Could not use the clipboard picture: " + ex.Message;
        }
    }
}
