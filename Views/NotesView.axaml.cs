using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); if (_dirty) SaveCurrent(); };
        Closing += (_, _) => { _autoSave.Stop(); if (_dirty) SaveCurrent(); };
        HtmlBox.AddHandler(KeyDownEvent, HtmlBox_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ApplyTimeButton();
        var app =(App)Avalonia.Application.Current!;
        app.SettingsChanged += ApplyTimeButton;
        Editor.PictureError += message => StatusText.Text = message;
        Editor.SelectionAction += Editor_SelectionAction;
        Chat.GetContext = ChatContext;
        Chat.ApplyEdits = Chat_ApplyEdits;
        Chat.Undo = Chat_Undo;
        Chat.Insert = Chat_Insert;
        Chat.Pressing += CaptureChatSelection;
        Chat.SelectionCleared += () => _aiSelection = null;
        Chat.CloseRequested += () => ShowChat(false);
        // a new click in the note starts a new selection: what the chat captured before is out of date
        Editor.AddHandler(GotFocusEvent, (_, _) =>
        {
            if (!Chat.IsVisible) return;
            _aiSelection = null;
            Chat.SetSelectionPreview(null);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        NotesService.ExternalChange += OnExternalChange;
        Closed += (_, _) =>
        {
            app.SettingsChanged -= ApplyTimeButton;
            NotesService.ExternalChange -= OnExternalChange;
            Chat.Stop();
        };
        RefreshList(null);
    }

    // ---- pictures and other PCs' changes ----------------------------------------------------

    private async void Image_Click(object? sender, RoutedEventArgs e)
    {
        var selection = _codeView ? null : Editor.CaptureSelection();
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Insert pictures",
            AllowMultiple = true,
            FileTypeFilter = new[] { Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll },
        });
        var srcs = new List<string>();
        foreach (var file in files)
        {
            if (Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file) is not { } path) continue;
            try { srcs.Add(NotesService.AddImage(path)); }
            catch (Exception ex) { StatusText.Text = "Could not add the picture: " + ex.Message; }
        }
        if (srcs.Count == 0) return;
        if (!_codeView)
        {
            Editor.InsertImages(srcs, selection);
            return;
        }
        var text = HtmlBox.Text ?? "";
        var caret = Math.Clamp(HtmlBox.CaretIndex, 0, text.Length);
        var html = string.Concat(srcs.Select(s => "\n" + NoteDocument.ToHtml(new[] { NoteDocument.Image(s) }))) + "\n";
        HtmlBox.Text = text[..caret] + html + text[caret..];
        HtmlBox.CaretIndex = caret + html.Length;
    }

    private void Table_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) InsertHtmlBlock(NoteDocument.NewTable());
        else Editor.InsertTable();
    }

    private void Chart_Click(object? sender, RoutedEventArgs e)
    {
        if (_codeView) InsertHtmlBlock(NoteDocument.NewChart());
        else Editor.InsertChart();
    }

    /// <summary>HTML source view: inserts a block's HTML at the caret, on lines of its own.</summary>
    private void InsertHtmlBlock(NoteBlock block)
    {
        var text = HtmlBox.Text ?? "";
        var caret = Math.Clamp(HtmlBox.CaretIndex, 0, text.Length);
        var html = "\n" + NoteDocument.ToHtml(new[] { block }) + "\n";
        HtmlBox.Text = text[..caret] + html + text[caret..];
        HtmlBox.Focus();
        HtmlBox.CaretIndex = caret + html.Length;
    }

    private void OnExternalChange(Guid id, NoteItem? changed)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyExternalChange(id, changed));

    /// <summary>Another PC (through the synced folder) added, changed or deleted a note. Unsaved edits here are never
    /// thrown away: if both changed the open note, the other version is kept as a separate "conflicting copy".</summary>
    private void ApplyExternalChange(Guid id, NoteItem? changed)
    {
        var existing = _notes.FirstOrDefault(n => n.Id == id);
        var showing = _current is not null && _current.Id == id;
        if (changed is null)
        {
            if (existing is null) return;
            if (showing && _dirty)
            {
                StatusText.Text = "This note was deleted on another PC — saving keeps it.";
                return;
            }
            _notes.Remove(existing);
            if (showing)
            {
                _current = null;
                LoadEditor(null);
            }
            RefreshList(_current?.Id);
            return;
        }

        if (existing is null)
        {
            _notes.Add(changed);
        }
        else if (showing && _dirty)
        {
            var copy = new NoteItem
            {
                Title = (changed.Title.Length > 0 ? changed.Title : "Note") + " (conflicting copy)",
                Tags = changed.Tags, Html = changed.Html, Created = changed.Created, Updated = changed.Updated,
            };
            try
            {
                NotesService.Save(copy);
                _notes.Add(copy);
                StatusText.Text = $"Changed on another PC too — their version is kept as “{copy.Title}”.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Changed on another PC too, and their version could not be kept: " + ex.Message;
            }
        }
        else
        {
            existing.Title = changed.Title;
            existing.Tags = changed.Tags;
            existing.Html = changed.Html;
            existing.Created = changed.Created;
            existing.Updated = changed.Updated;
            if (showing)
            {
                LoadEditor(existing);
                StatusText.Text = $"Updated from another PC at {DateTime.Now:HH:mm}";
            }
        }
        RefreshList(_current?.Id);
    }

    private void UpdateDirty()
    {
        if (_suppress) return;
        // compare with what was last loaded/saved: TextChanged can also fire late for programmatic edits
        var changed = Snapshot() != _baseline;
        if (changed == _dirty) return;
        _dirty = changed;
        StatusText.Text = changed ? "● Unsaved changes — saving…" : "";
        if (changed) { _autoSave.Stop(); _autoSave.Start(); }
        else _autoSave.Stop();
    }

    private readonly Avalonia.Threading.DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromSeconds(3) };

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
                case Key.J: ShowChat(!Chat.IsVisible); e.Handled = true; break;
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
        if (note is null) Chat.Forget(Guid.Empty);
        Chat.SetNote(note?.Id ?? Guid.Empty);
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

        var untitled = (TitleBox.Text ?? "").Trim().Length == 0;
        var isNew = _current is null;
        var note = _current ?? new NoteItem();
        note.Title = title;
        note.Tags = (TagsBox.Text ?? "").Trim();
        note.Html = html;
        note.Updated = DateTime.Now;
        if (isNew) _notes.Add(note);
        _current = note;
        if (isNew) Chat.Adopt(Guid.Empty, note.Id);

        try
        {
            NotesService.Save(note);
            _baseline = Snapshot();
            _dirty = false;
            StatusText.Text = $"Saved {note.Updated:HH:mm:ss}";
            if (isNew) WorkLogService.LogNote(note);
            if (untitled && AppRef.Config.AiAutoTitle && NotesService.ToPlainText(html).Length >= 40) RequestAiTitle(note);
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
        var deleted = _current;
        _notes.RemoveAll(n => n.Id == deleted.Id);
        _current = null;
        try { NotesService.Delete(deleted, _notes); } catch (Exception ex) { StatusText.Text = "Could not delete: " + ex.Message; }
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

    // ---- AI ---------------------------------------------------------------------------

    private App AppRef => (App)Avalonia.Application.Current!;

    /// <summary>Notes already sent for an automatic title in this session (asked once, even if it failed).</summary>
    private static readonly HashSet<Guid> TitleAsked = new();

    private NoteEditor.EditorSelection? _aiSelection;
    private (int Start, int End, string Text)? _aiHtmlSelection;
    private bool _aiBusy;

    /// <summary>Shows a note saved elsewhere (AI answers, day summaries) in the list.</summary>
    public void AddNote(NoteItem note)
    {
        if (_notes.All(n => n.Id != note.Id)) _notes.Add(note);
        RefreshList(_current?.Id);
    }

    /// <summary>Applies an automatic AI title, unless the note got a real title meanwhile.</summary>
    public void ApplyAiTitle(Guid id, string untitled, string title, IReadOnlyList<string> tags)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (note is null || note.Title != untitled) return;
        var showing = _current?.Id == id;
        if (showing && (TitleBox.Text ?? "").Trim().Length > 0) return; // the user is typing a title
        note.Title = title;
        note.Tags = App.MergeTags(note.Tags, tags);
        try { NotesService.Save(note); } catch { return; }
        if (showing)
        {
            var wasDirty = _dirty;
            _suppress = true;
            TitleBox.Text = title;
            TagsBox.Text = App.MergeTags(TagsBox.Text ?? "", tags);
            _suppress = false;
            if (wasDirty) UpdateDirty();
            else _baseline = Snapshot();
            StatusText.Text = $"✨ AI titled this note “{title}”";
        }
        RefreshList(_current?.Id);
    }

    private async void RequestAiTitle(NoteItem note)
    {
        if (!TitleAsked.Add(note.Id)) return;
        var untitled = note.Title;
        var result = await AiService.AskAsync(AiPrompts.TitleAndTags(Cap(NotesService.HtmlToMarkdown(note.Html), 8000)));
        if (result.Ok && AiPrompts.TryParseTitle(result.Text, out var title, out var tags))
            AppRef.ApplyAiTitle(note.Id, untitled, title, tags);
    }

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + "\n…";

    /// <summary>Remembers the selection before the menu takes the focus (a TextBox drops its selection then).</summary>
    private void Ai_Click(object? sender, RoutedEventArgs e)
    {
        if (_aiBusy)
        {
            StatusText.Text = "✨ Still waiting for the AI…";
            return;
        }
        _aiSelection = null;
        _aiHtmlSelection = null;
        bool selected;
        if (_codeView)
        {
            var text = HtmlBox.Text ?? "";
            var s = Math.Clamp(Math.Min(HtmlBox.SelectionStart, HtmlBox.SelectionEnd), 0, text.Length);
            var end = Math.Clamp(Math.Max(HtmlBox.SelectionStart, HtmlBox.SelectionEnd), s, text.Length);
            if (end > s) _aiHtmlSelection = (s, end, text[s..end]);
            selected = end > s;
        }
        else
        {
            _aiSelection = Editor.CaptureSelection();
            selected = _aiSelection is { Text.Length: > 0 };
        }
        AiScopeItem.Header = selected ? "Acts on the selected text" : "Acts on the whole note (select text to change only that)";
        FlyoutBase.ShowAttachedFlyout(AiButton);
    }

    private static string ActionLabel(string action) => action switch
    {
        "ask" => "AI edit",
        "shorten" => "Shorten",
        "fix" => "Fix spelling and grammar",
        "improve" => "Improve writing",
        "chat" => "AI chat",
        _ when action.StartsWith("translate:") => "Translate to " + action["translate:".Length..],
        _ => action,
    };

    // ---- AI chat beside the note ------------------------------------------------------------

    private bool _chatGrew;

    private void Chat_Click(object? sender, RoutedEventArgs e) => ShowChat(!Chat.IsVisible);

    /// <summary>Shows or hides the chat; the window grows by the panel's width so the note keeps its room.</summary>
    private void ShowChat(bool show)
    {
        if (Chat.IsVisible == show) return;
        Chat.IsVisible = show;
        if (show)
        {
            if (WindowState == WindowState.Normal)
            {
                Width += Chat.Width + Chat.Margin.Left;
                _chatGrew = true;
            }
            Chat.SetNote(_current?.Id ?? Guid.Empty);
            Chat.FocusInput();
        }
        else if (_chatGrew)
        {
            Width = Math.Max(MinWidth, Width - Chat.Width - Chat.Margin.Left);
            _chatGrew = false;
        }
    }

    /// <summary>The selection in the note, taken when the pointer goes down in the chat (the note's text box then loses it).</summary>
    private void CaptureChatSelection()
    {
        if (_codeView || !Editor.IsKeyboardFocusWithin) return;
        var selection = Editor.CaptureSelection();
        _aiSelection = selection;
        Chat.SetSelectionPreview(selection?.Text);
    }

    private AiChatContext ChatContext()
    {
        CaptureChatSelection();
        var selection = _codeView ? null : _aiSelection;
        return new AiChatContext(CurrentHtml(), (TitleBox.Text ?? "").Trim(),
            selection is { Text.Length: > 0 } ? selection.Text : null, selection, AppInfo(), _current?.Id);
    }

    /// <summary>What the chat is told about the app: the feature guide and the current state of the AI settings, the day summary and the notes.</summary>
    private string AppInfo()
    {
        var config = AppRef.Config;
        static string OnOff(bool on) => on ? "on" : "off";
        var summaries = _notes.Where(n => n.Tags.Contains("#summary", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Created).Take(3).Select(n => $"“{n.Title}” ({n.Created:yyyy-MM-dd HH:mm})").ToList();
        var lines = new List<string>
        {
            AiPrompts.AppGuide,
            "",
            "Current state:",
            $"- AI: {AiService.DisplayName}; answer language: {(AiService.Language.Length > 0 ? AiService.Language : "follows the content")}.",
            $"- AI auto title: {OnOff(config.AiAutoTitle)}. AI day summary: {OnOff(config.AiDaySummary)}"
                + (config.AiDaySummaryLast is { } last ? $", last written for the day ending {last:yyyy-MM-dd HH:mm}." : ", not written yet."),
            $"- Work log: {OnOff(config.WorkLogEnabled)}; working hours {config.WorkHoursStart}–{config.WorkHoursEnd}; active today {WorkLogService.FormatDuration(WorkLogService.TodayActiveSeconds())}.",
            $"- Notes: {_notes.Count}. Newest day summaries: {(summaries.Count > 0 ? string.Join("; ", summaries) : "none yet")}.",
        };
        if (config.WorkLogEnabled)
        {
            try
            {
                var today = WorkLogService.Summarize(DateTime.Today, DateTime.Today, new WorkHours(config));
                var md = WorkLogService.ToMarkdown(today);
                lines.Add("");
                lines.Add("Today's work log (what the user worked on, from the foreground-window samples):");
                lines.Add(md.Length > 4000 ? md[..4000] + "\n…" : md);
            }
            catch { /* the work log is optional context */ }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void SetNoteHtml(string html)
    {
        if (_codeView)
        {
            HtmlBox.Text = html;
            return;
        }
        Editor.Load(html);
        UpdateDirty();
    }

    private AiChatOutcome Chat_ApplyEdits(string sentHtml, List<NoteDocument.NoteEdit> edits)
    {
        var before = CurrentHtml();
        if (before != sentHtml)
            return new AiChatOutcome(false, "The note changed since the AI answered, so the block numbers no longer fit. Ask again.");
        var units = NoteDocument.Units(NoteDocument.Parse(before));
        SetNoteHtml(NoteDocument.ToHtml(NoteDocument.ApplyEdits(units, edits)));
        var after = CurrentHtml();
        if (after == before) return new AiChatOutcome(false, "The AI's commands changed nothing in the note.");
        return new AiChatOutcome(true, "✨ Applied — press Ctrl+S to keep it, or Undo in the chat.", before, after);
    }

    private AiChatOutcome Chat_Undo(string before, string after)
    {
        if (CurrentHtml() != after)
            return new AiChatOutcome(false, "The note changed after this was applied, so it can't be undone from here.");
        SetNoteHtml(before);
        return new AiChatOutcome(true, "Put the note back.");
    }

    private string Chat_Insert(string target, string markdown, object? token)
    {
        _aiSelection = token as NoteEditor.EditorSelection;
        _aiHtmlSelection = null;
        ApplyAiResult("chat", target, markdown, (TitleBox.Text ?? "").Trim());
        return StatusText.Text ?? "";
    }

    private void AiAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } && !_aiBusy) RunAiAction(action);
    }

    /// <summary>An AI action picked from the right-click menu of a block, on the text selected there.</summary>
    private void Editor_SelectionAction(string action, NoteEditor.EditorSelection selection)
    {
        if (_aiBusy)
        {
            StatusText.Text = "✨ Still waiting for the AI…";
            return;
        }
        _aiSelection = selection;
        _aiHtmlSelection = null;
        RunAiAction(action);
    }

    private async void RunAiAction(string action)
    {
        var selection = _codeView ? _aiHtmlSelection?.Text : _aiSelection is { Text.Length: > 0 } sel ? sel.Text : null;
        // charts and HTML blocks survive the round trip, so "Replace the note" keeps them
        var note = Cap(NotesService.NoteToAiMarkdown(CurrentHtml()), 60000);
        var title = (TitleBox.Text ?? "").Trim();
        if (action == "ask")
        {
            AskInChat();
            return;
        }
        if (selection is null && note.Trim().Length == 0)
        {
            StatusText.Text = "This note is empty — write something first.";
            return;
        }
        if (action == "title")
        {
            await SuggestTitleAsync(note);
            return;
        }

        string heading, prompt;
        (string, string)[] buttons;
        switch (action)
        {
            case "explain":
                if (selection is null)
                {
                    StatusText.Text = "Select a word or sentence first.";
                    return;
                }
                heading = "Explain — " + (selection.Length > 50 ? selection[..50] + "…" : selection).ReplaceLineEndings(" ");
                prompt = AiPrompts.ExplainForLearner(selection, Cap(_aiSelection?.BlockText ?? "", 1000));
                buttons = new[] { ("insert", "Insert below") };
                break;
            case "continue":
                heading = "Continue writing";
                var from = _aiSelection?.BlockText is { Length: > 0 } block ? block : note[Math.Max(0, note.Length - 600)..];
                prompt = AiPrompts.Continue(note, from);
                buttons = new[] { ("insert", _codeView || _aiSelection is null ? "Add at the end" : "Insert after the paragraph") };
                break;
            case "summary":
                heading = "Summary of the note";
                prompt = AiPrompts.Summarize(note);
                buttons = new[] { ("top", "Insert at the top"), ("end", "Add at the end") };
                break;
            case "todos":
                heading = "To-dos in the note";
                prompt = AiPrompts.Todos(note);
                buttons = new[] { ("end", "Add at the end"), ("top", "Insert at the top") };
                break;
            case "minutes":
                heading = "Meeting minutes";
                prompt = AiPrompts.Minutes(note, title);
                buttons = new[] { ("new", "Save as a new note"), ("replace", "Replace this note") };
                break;
            default:
                heading = ActionLabel(action) + (selection is null ? " — whole note" : " — selected text");
                prompt = AiPrompts.Rewrite(action, selection ?? note, wholeNote: selection is null);
                buttons = selection is null
                    ? new[] { ("replace", "Replace the note"), ("new", "Save as a new note") }
                    : new[] { ("selection", "Replace the selection"), ("insert", "Insert below") };
                break;
        }

        // the answer appears in the chat beside the note; its buttons act on the selection as it was now
        var selectionNow = _aiSelection;
        var htmlSelectionNow = _aiHtmlSelection;
        ShowChat(true);
        Chat.RunRequest(heading, ct => AiService.AskAsync(prompt, ct), buttons, async (choice, text) =>
        {
            _aiSelection = selectionNow;
            _aiHtmlSelection = htmlSelectionNow;
            var answer = AiService.StripOuterFence(text);
            if (choice == "replace" && LooksTruncated(note, answer)
                && !await Dialogs.Confirm(this, "Replace the note?",
                    "The AI's answer is much shorter than the note, so replacing would remove most of it. "
                    + "To keep the note, choose Cancel and copy what you need from the chat.", "Replace"))
                return "Cancelled.";
            ApplyAiResult(action, choice, answer, title);
            return StatusText.Text ?? "";
        });
    }

    /// <summary>Opens the side chat and focuses its input (the main window's "Ask AI" button).</summary>
    public void OpenChat()
    {
        ShowChat(true);
        Chat.FocusInput();
    }

    /// <summary>"Ask AI to change the note…": the chat opens with the selection (if any) as context, ready for the instruction.</summary>
    private void AskInChat()
    {
        var selection = _aiSelection;
        ShowChat(true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // the note's text box took the focus back when the menu closed, which forgets the selection: restore it
            _aiSelection = selection;
            Chat.SetSelectionPreview(selection?.Text);
            Chat.FocusInput();
        });
    }

    /// <summary>An answer meant to replace the note keeps less than half of its words (it probably holds only the new part).</summary>
    private static bool LooksTruncated(string note, string answer)
    {
        static int Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var before = Words(note);
        return before >= 20 && Words(answer) < before / 2;
    }

    private void ApplyAiResult(string action, string choice, string text, string title)
    {
        if (action == "todos" && text.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "The AI found no to-dos in this note.";
            return;
        }
        var blocks = NoteDocument.FromMarkdown(text);
        if (choice is "top" or "end")
            blocks.Insert(0, new NoteBlock { Kind = BlockKind.Heading, Level = 2, Text = action == "todos" ? "To-do" : "Summary" });

        switch (choice)
        {
            case "selection":
                if (_codeView && _aiHtmlSelection is { } h && h.End <= (HtmlBox.Text ?? "").Length && HtmlBox.Text![h.Start..h.End] == h.Text)
                {
                    HtmlBox.Text = HtmlBox.Text[..h.Start] + text + HtmlBox.Text[h.End..];
                }
                else if (!_codeView && _aiSelection is not null && blocks.Any(b => b.IsGrid || b.Kind is BlockKind.Code or BlockKind.Raw))
                {
                    // a chart, table or code block can't sit inside a paragraph: take the selection out, add the blocks below
                    if (!Editor.ReplaceSelection(_aiSelection, ""))
                    {
                        StatusText.Text = "The selected text changed meanwhile — use “Copy” in the AI window instead.";
                        return;
                    }
                    Editor.InsertBlocks(blocks, _aiSelection);
                }
                else if (_codeView || _aiSelection is null || !Editor.ReplaceSelection(_aiSelection, text))
                {
                    StatusText.Text = "The selected text changed meanwhile — use “Copy” in the AI window instead.";
                    return;
                }
                break;
            case "insert" when !_codeView:
                Editor.InsertBlocks(blocks, _aiSelection);
                break;
            case "top" when !_codeView:
                Editor.InsertBlocks(blocks, atStart: true);
                break;
            case "end" when !_codeView:
                Editor.InsertBlocks(blocks);
                break;
            case "top":
                HtmlBox.Text = NoteDocument.ToHtml(blocks) + "\n" + HtmlBox.Text;
                break;
            case "insert" or "end":
                HtmlBox.Text = (HtmlBox.Text ?? "").TrimEnd() + "\n" + NoteDocument.ToHtml(blocks);
                break;
            case "replace":
                if (_codeView) HtmlBox.Text = NoteDocument.ToHtml(blocks);
                else
                {
                    Editor.Load(NoteDocument.ToHtml(blocks));
                    UpdateDirty();
                }
                break;
            case "new":
                var note = new NoteItem
                {
                    Title = action == "minutes" ? $"Minutes — {(title.Length > 0 ? title : DateTime.Now.ToString("yyyy-MM-dd"))}"
                        : $"{(title.Length > 0 ? title : "Note")} ({ActionLabel(action)})",
                    Tags = (TagsBox.Text ?? "").Trim(),
                    Html = NoteDocument.ToHtml(blocks),
                };
                try
                {
                    AppRef.AddNote(note);
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Could not save the new note: " + ex.Message;
                    return;
                }
                SelectNote(note.Id);
                StatusText.Text = $"✨ Saved as the new note “{note.Title}”.";
                return;
        }
        StatusText.Text = "✨ AI text added — press Ctrl+S to keep it.";
    }

    private async System.Threading.Tasks.Task SuggestTitleAsync(string note)
    {
        var target = _current;
        _aiBusy = true;
        StatusText.Text = AiService.Asking("write a title and tags");
        try
        {
            var result = await AiService.AskAsync(AiPrompts.TitleAndTags(Cap(note, 8000)));
            if (_current != target)
            {
                StatusText.Text = "Another note was opened meanwhile — the suggested title was not applied.";
                return;
            }
            if (!result.Ok || !AiPrompts.TryParseTitle(result.Text, out var title, out var tags))
            {
                StatusText.Text = "AI: " + (result.Ok ? "no usable title in the answer." : result.Text.ReplaceLineEndings(" "));
                return;
            }
            TitleBox.Text = title;
            TagsBox.Text = App.MergeTags(TagsBox.Text ?? "", tags);
            UpdateDirty();
            StatusText.Text = "✨ Title and tags suggested — press Ctrl+S to keep them.";
        }
        finally
        {
            _aiBusy = false;
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
