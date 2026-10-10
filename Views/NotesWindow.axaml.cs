using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ScriptAppDashboard.Models;
using ScriptAppDashboard.Services;

namespace ScriptAppDashboard.Views;

public partial class NotesWindow : Window
{
    private readonly List<NoteItem> _notes = NotesService.Load();
    private NoteItem? _current;
    private bool _dirty;
    private bool _suppress;
    private bool _codeView; // editing the HTML source instead of the visual editor

    public NotesWindow()
    {
        InitializeComponent();
        _baseline = Snapshot();
        SearchBox.TextChanged += (_, _) => RefreshList(_current?.Id);
        TitleBox.TextChanged += (_, _) => UpdateDirty();
        TagsBox.TextChanged += (_, _) => UpdateDirty();
        HtmlBox.TextChanged += (_, _) => UpdateDirty();
        Editor.Changed += UpdateDirty;
        TitleBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_codeView) HtmlBox.Focus();
            else Editor.FocusStart();
        };
        Closing += (_, _) => { if (_dirty) SaveCurrent(); };
        HtmlBox.AddHandler(KeyDownEvent, HtmlBox_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ApplyTimeButton();
        var app =(App)Avalonia.Application.Current!;
        app.SettingsChanged += ApplyTimeButton;
        Closed += (_, _) => app.SettingsChanged -= ApplyTimeButton;
        RefreshList(null);
    }

    private void UpdateDirty()
    {
        if (_suppress) return;
        // compare with what was last loaded/saved: TextChanged can also fire late for programmatic edits
        var changed = Snapshot() != _baseline;
        if (changed == _dirty) return;
        _dirty = changed;
        StatusText.Text = changed ? "● Unsaved changes" : "";
    }

    private string _baseline = "";

    private string Snapshot() => $"{TitleBox.Text}\u0001{TagsBox.Text}\u0001{CurrentHtml()}";

    /// <summary>The note body as shown: the visual editor's HTML, or the source being edited.</summary>
    private string CurrentHtml() => _codeView ? HtmlBox.Text ?? "" : Editor.ToHtml();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.S: SaveCurrent(); e.Handled = true; break;
                case Key.N: NewNote(); e.Handled = true; break;
                case Key.F: SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; break;
                case Key.B: Bold_Click(null, e); e.Handled = true; break;
                case Key.I: Italic_Click(null, e); e.Handled = true; break;
                case Key.K: Link_Click(null, e); e.Handled = true; break;
            }
        }
        base.OnKeyDown(e);
    }

    // ---- formatting toolbar: acts on the visual editor, or wraps the HTML source in tags ----

    private void Wrap(string open, string close, string placeholder)
    {
        var text = HtmlBox.Text ?? "";
        var start = Math.Min(HtmlBox.SelectionStart, HtmlBox.SelectionEnd);
        var end = Math.Max(HtmlBox.SelectionStart, HtmlBox.SelectionEnd);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        var selected = end > start ? text[start..end] : placeholder;
        HtmlBox.Text = text[..start] + open + selected + close + text[end..];
        HtmlBox.Focus();
        HtmlBox.SelectionStart = start + open.Length;
        HtmlBox.SelectionEnd = start + open.Length + selected.Length;
    }

    private void Time_Click(object? sender, RoutedEventArgs e)
    {
        var stamp = TimestampService.Now(((App)Avalonia.Application.Current!).Config.TimestampFormat);
        if (!_codeView)
        {
            Editor.InsertText(stamp);
            return;
        }
        var text = HtmlBox.Text ?? "";
        var start = Math.Clamp(Math.Min(HtmlBox.SelectionStart, HtmlBox.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(HtmlBox.SelectionStart, HtmlBox.SelectionEnd), start, text.Length);
        HtmlBox.Text = text[..start] + stamp + text[end..];
        HtmlBox.Focus();
        HtmlBox.CaretIndex = start + stamp.Length;
    }

    private void ApplyTimeButton()
        => TimeButton.IsVisible = ((App)Avalonia.Application.Current!).Config.TimestampButtonEnabled;

    private void Bold_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) Wrap("<b>", "</b>", "bold");
        else Editor.ToggleInline("**", "bold");
    }

    private void Italic_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) Wrap("<i>", "</i>", "italic");
        else Editor.ToggleInline("*", "italic");
    }

    private void Heading_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) Wrap("<h3>", "</h3>", "Heading");
        else Editor.CycleHeading();
    }

    private void Quote_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) Wrap("<blockquote>", "</blockquote>", "quote");
        else Editor.ToggleBlock(BlockKind.Quote);
    }

    private void Code_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) Wrap("<code>", "</code>", "code");
        else Editor.CodeButton();
    }

    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) Wrap("<a href=\"https://\">", "</a>", "link text");
        else Editor.InsertLink();
    }

    // ---- list lines: "• x", "1. x", "◷ 2026-10-10 14:30 · x"; Enter continues them ----

    private static readonly Regex BulletLine = new(@"^(\s*)([-*•])\s(.*)$");
    private static readonly Regex NumberLine = new(@"^(\s*)(\d+)([.)])\s(.*)$");
    private static readonly Regex TimedLine = new(@"^(\s*)◷ (.+?) · (.*)$");
    private static readonly Regex AnyPrefix = new(@"^\s*(?:[-*•]\s|\d+[.)]\s|◷ .+? · )");

    private static string TimeStamp()
        => TimestampService.Now(((App)Avalonia.Application.Current!).Config.TimeBulletFormat);

    private static string Prefix(string kind, int index) => kind switch
    {
        "num" => $"{index}. ",
        "timed" => $"◷ {TimeStamp()} · ",
        _ => "• ",
    };

    private void Bullets_Click(object? sender, RoutedEventArgs e) => ApplyList("bullet");
    private void Numbers_Click(object? sender, RoutedEventArgs e) => ApplyList("num");
    private void Timed_Click(object? sender, RoutedEventArgs e) => ApplyList("timed");

    /// <summary>
    /// Visual editor: toggles the current block to that list. HTML source: prefixes every selected line
    /// (or the caret's line) with the list marker, replacing any existing marker.
    /// </summary>
    private void ApplyList(string kind)
    {
        if (!_codeView)
        {
            Editor.ToggleBlock(kind switch { "num" => BlockKind.Number, "timed" => BlockKind.Timed, _ => BlockKind.Bullet });
            return;
        }
        var text = HtmlBox.Text ?? "";
        var selStart = Math.Clamp(Math.Min(HtmlBox.SelectionStart, HtmlBox.SelectionEnd), 0, text.Length);
        var selEnd = Math.Clamp(Math.Max(HtmlBox.SelectionStart, HtmlBox.SelectionEnd), selStart, text.Length);
        if (selStart == selEnd) selStart = selEnd = Math.Clamp(HtmlBox.CaretIndex, 0, text.Length);

        var blockStart = selStart == 0 ? 0 : text.LastIndexOf('\n', selStart - 1) + 1;
        var nl = text.IndexOf('\n', selEnd);
        var blockEnd = nl < 0 ? text.Length : nl;

        var lines = text[blockStart..blockEnd].Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            lines[i] = Prefix(kind, i + 1) + AnyPrefix.Replace(line, "");
        }

        var block = string.Join(Environment.NewLine, lines);
        HtmlBox.Text = text[..blockStart] + block + text[blockEnd..];
        HtmlBox.Focus();
        HtmlBox.CaretIndex = blockStart + block.Length;
    }

    /// <summary>Enter on a list line starts the next item (new number / new time stamp); Enter on an empty item ends the list.</summary>
    private void HtmlBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (HtmlBox.SelectionStart != HtmlBox.SelectionEnd) return;

        var text = HtmlBox.Text ?? "";
        var caret = Math.Clamp(HtmlBox.CaretIndex, 0, text.Length);
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var before = text[lineStart..caret].TrimEnd('\r');

        string next;
        bool emptyItem;
        Match m;
        if ((m = TimedLine.Match(before)).Success)
        {
            emptyItem = m.Groups[3].Value.Length == 0;
            next = $"{m.Groups[1].Value}◷ {TimeStamp()} · ";
        }
        else if ((m = NumberLine.Match(before)).Success)
        {
            emptyItem = m.Groups[4].Value.Length == 0;
            next = $"{m.Groups[1].Value}{int.Parse(m.Groups[2].Value) + 1}{m.Groups[3].Value} ";
        }
        else if ((m = BulletLine.Match(before)).Success)
        {
            emptyItem = m.Groups[3].Value.Length == 0;
            next = $"{m.Groups[1].Value}{m.Groups[2].Value} ";
        }
        else
        {
            return; // not a list line — normal Enter
        }

        e.Handled = true;
        if (emptyItem)
        {
            // second Enter on an empty item: drop the marker and leave the list
            HtmlBox.Text = text[..lineStart] + text[caret..];
            HtmlBox.CaretIndex = lineStart;
        }
        else
        {
            var insert = Environment.NewLine + next;
            HtmlBox.Text = text[..caret] + insert + text[caret..];
            HtmlBox.CaretIndex = caret + insert.Length;
        }
    }

    /// <summary>Starts a blank note (saving the one being edited first).</summary>
    public void NewNote()
    {
        if (_dirty) SaveCurrent();
        _current = null;
        LoadEditor(null);
        NoteList.SelectedItem = null;
        TitleBox.Focus();
    }

    private void New_Click(object? sender, RoutedEventArgs e) => NewNote();

    private void RefreshList(Guid? select)
    {
        _suppress = true;
        try
        {
            NoteList.Items.Clear();
            var matches = NotesService.Search(_notes, SearchBox.Text ?? "").ToList();
            ListBoxItem? toSelect = null;
            foreach (var note in matches)
            {
                var snippet = NotesService.ToPlainText(note.Html);
                var panel = new StackPanel { Spacing = 1 };
                panel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(note.Title) ? "(untitled)" : note.Title,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = snippet.Length > 70 ? snippet[..70] + "…" : snippet,
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"{note.Updated:yyyy-MM-dd HH:mm}   {note.Tags}".TrimEnd(),
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var item = new ListBoxItem { Content = panel, Tag = note };
                NoteList.Items.Add(item);
                if (note.Id == select) toSelect = item;
            }
            NoteList.SelectedItem = toSelect;
            CountText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? $"{_notes.Count} note(s)"
                : $"{matches.Count} of {_notes.Count} note(s) match";
            ExportListMenu.Header = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? "All notes as Markdown…"
                : $"The {matches.Count} matching note(s) as Markdown…";
            EmptyHint.IsVisible = matches.Count == 0;
            EmptyHint.Text = _notes.Count == 0
                ? "No notes yet.\n\nPress “New note” (Ctrl+N) and jot something down — you can search it later."
                : "No notes match your search.";
        }
        finally
        {
            _suppress = false;
        }
    }

    private void NoteList_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (NoteList.SelectedItem is not ListBoxItem { Tag: NoteItem note }) return;
        if (_current?.Id == note.Id) return;
        if (_dirty) SaveCurrent(refresh: false);
        _current = note;
        LoadEditor(note);
    }

    private void LoadEditor(NoteItem? note)
    {
        _suppress = true;
        TitleBox.Text = note?.Title ?? "";
        TagsBox.Text = note?.Tags ?? "";
        if (_codeView) HtmlBox.Text = note?.Html ?? "";
        else Editor.Load(note?.Html ?? "");
        _suppress = false;
        _baseline = Snapshot();
        _dirty = false;
        StatusText.Text = note is null ? "New note" : $"Created {note.Created:yyyy-MM-dd HH:mm}";
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => SaveCurrent();

    private void SaveCurrent(bool refresh = true)
    {
        var html = CurrentHtml();
        var title = (TitleBox.Text ?? "").Trim();
        if (title.Length == 0 && html.Trim().Length == 0 && _current is null)
        {
            StatusText.Text = "Nothing to save.";
            return;
        }
        if (title.Length == 0)
        {
            var text = NotesService.ToPlainText(html);
            title = text.Length > 40 ? text[..40] + "…" : text;
        }

        var isNew = _current is null;
        var note = _current ?? new NoteItem();
        note.Title = title;
        note.Tags = (TagsBox.Text ?? "").Trim();
        note.Html = html;
        note.Updated = DateTime.Now;
        if (isNew) _notes.Add(note);
        _current = note;

        try
        {
            NotesService.Save(_notes);
            _baseline = Snapshot();
            _dirty = false;
            StatusText.Text = $"Saved {note.Updated:HH:mm:ss}";
            if (isNew) WorkLogService.LogNote(note);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
        }

        if (refresh) RefreshList(note.Id);
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            LoadEditor(null);
            return;
        }
        if (!await Dialogs.Confirm(this, "Delete note", $"Delete “{_current.Title}”? This cannot be undone.", "Delete"))
            return;
        _notes.RemoveAll(n => n.Id == _current.Id);
        _current = null;
        try { NotesService.Save(_notes); } catch (Exception ex) { StatusText.Text = "Could not save: " + ex.Message; }
        LoadEditor(null);
        RefreshList(null);
    }

    /// <summary>Opens a note in the editor (clearing a search that would hide it).</summary>
    public void SelectNote(Guid id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note is null) return;
        if (_dirty) SaveCurrent(refresh: false);
        if (!NotesService.Search(new[] { note }, SearchBox.Text ?? "").Any()) SearchBox.Text = "";
        _current = note;
        LoadEditor(note);
        RefreshList(note.Id);
    }

    // ---- Markdown export ----

    /// <summary>The note on screen, including unsaved edits.</summary>
    private NoteItem EditorNote() => new()
    {
        Id = _current?.Id ?? Guid.NewGuid(),
        Title = (TitleBox.Text ?? "").Trim(),
        Tags = (TagsBox.Text ?? "").Trim(),
        Html = CurrentHtml(),
        Created = _current?.Created ?? DateTime.Now,
        Updated = _current?.Updated ?? DateTime.Now,
    };

    private async void ExportCurrent_Click(object? sender, RoutedEventArgs e)
    {
        var note = EditorNote();
        if (note.Title.Length == 0 && note.Html.Trim().Length == 0)
        {
            StatusText.Text = "This note is empty — nothing to export.";
            return;
        }
        await ExportAsync(NotesService.SafeFileName(note.Title, "note") + ".md", NotesService.ToMarkdown(note), "1 note");
    }

    private async void ExportHtml_Click(object? sender, RoutedEventArgs e)
    {
        var note = EditorNote();
        if (note.Title.Length == 0 && note.Html.Trim().Length == 0)
        {
            StatusText.Text = "This note is empty — nothing to export.";
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export as HTML",
                SuggestedFileName = NotesService.SafeFileName(note.Title, "note") + ".html",
                DefaultExtension = "html",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("HTML") { Patterns = new[] { "*.html" } },
                },
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(NotesService.ToHtml(note));
            StatusText.Text = $"Exported 1 note to {file.Name}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not export: " + ex.Message;
        }
    }

    private async void ExportList_Click(object? sender, RoutedEventArgs e)
    {
        if (_dirty) SaveCurrent();
        // export what the list shows: everything, or just the search matches
        var notes = NotesService.Search(_notes, SearchBox.Text ?? "").ToList();
        if (notes.Count == 0)
        {
            StatusText.Text = "No notes to export.";
            return;
        }
        await ExportAsync($"notes-{DateTime.Now:yyyy-MM-dd}.md", NotesService.ToMarkdown(notes), $"{notes.Count} note(s)");
    }

    private async void CopyMarkdown_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(NotesService.ToMarkdown(EditorNote()));
        StatusText.Text = "Copied as Markdown.";
    }

    private async System.Threading.Tasks.Task ExportAsync(string suggestedName, string markdown, string what)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export as Markdown",
                SuggestedFileName = suggestedName,
                DefaultExtension = "md",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } },
                },
            });
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                await writer.WriteAsync(markdown);
            StatusText.Text = $"Exported {what} to {file.Name}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not export: " + ex.Message;
        }
    }

    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var preview = new NoteItem
            {
                Id = _current?.Id ?? Guid.NewGuid(),
                Title = TitleBox.Text ?? "",
                Html = CurrentHtml(),
            };
            Process.Start(new ProcessStartInfo { FileName = NotesService.WritePreview(preview), UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open the preview: " + ex.Message;
        }
    }

    private void CodeToggle_Click(object? sender, RoutedEventArgs e)
    {
        var wasDirty = _dirty;
        _suppress = true;
        if (_codeView) Editor.Load(HtmlBox.Text ?? "");
        else HtmlBox.Text = Editor.ToHtml();
        _suppress = false;
        _codeView = !_codeView;
        HtmlBox.IsVisible = _codeView;
        Editor.IsVisible = !_codeView;
        CodeToggleButton.Content = _codeView ? "Visual editor" : "Edit HTML";
        // switching views can re-format the HTML; that alone is not an edit
        if (!wasDirty) _baseline = Snapshot();
        if (_codeView) HtmlBox.Focus();
        else Editor.FocusStart();
    }
}
