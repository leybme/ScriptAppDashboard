# CLAUDE.md

Guidance for working on Toolshed360: a .NET 8 + Avalonia 11.3.2 (Fluent) tray app that launches the user's scripts, plus notes, interval timers and a work log. Targets Windows and macOS. Most features are built and tested on Windows.

## Build & run

```bash
dotnet build -c Debug                         # must finish with 0 errors / 0 warnings
dotnet run                                    # add --minimized to start in the tray only
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

- The debug exe is `bin/Debug/net8.0/Toolshed360.exe`.
- A running instance locks it, so kill `Toolshed360` before rebuilding.
- CI and the tag-triggered release live in `.github/workflows/`; README.md explains how to cut a release.

## Layout

| Path | What |
|---|---|
| `App.axaml.cs` | Startup and the tray menu (`RefreshTrayMenu`). `ShowMainWindow(tab)` shows the one main window; `ShowNotes`, `ShowTimers`, `ShowWorkLog`, `ShowSettings`, `ShowSearch`, `ShowCaptures`, `ShowNote(id)` and `ShowAskAi` just select a tab (and the chat). Holds the app-wide events `SettingsChanged` / `TimersPauseChanged` and the timer pop-ups (`NotifyTimer`). |
| `Models/ScriptItem.cs` | `ScriptItem`, `AppConfig` (all settings), `IntervalTimerItem`, `NoteItem`. |
| `Services/ConfigService.cs` | Loads and saves `config.json` in `ConfigDir` (this PC). `DataDir` is the (possibly OneDrive-synced) folder for notes, images and the work log (`DataFolder` setting, else `ConfigDir`); `timers.json` there when `SyncTimers`. `AtomicWrite` (.tmp + move). |
| `Services/LauncherService.cs`, `ProcessScanner.cs`, `StartupService.cs` | Launch scripts, scan running scripts/apps (`Scan`, `ScanApps`), auto-start. |
| `Services/WorkLogService.cs` | Records work to daily JSONL files in `worklog/` and analyses them (`Summarize`, `Search`, `TodayActiveSeconds`, `ToMarkdown`). |
| `Services/WorkHours.cs` | Working-hours rules (wraps past midnight for night shifts). |
| `Services/TimerService.cs` | 1-second ticker that runs the timers (beep / shell command / AI CLI via stdin). Supports pause and resume (`TimersPausedUntil` in the config). |
| `Services/NotesService.cs` | One file per note (`Load`, `Save(note)`, `Delete`), migration from the old `notes.json`, conflict-copy adoption, `Watch` + `ExternalChange` for other PCs' edits, pictures (`AddImage`, `ResolveImage`, `MapImages`, `DeleteUnusedImages`). Also search, `HtmlToMarkdown` / `ToMarkdown` / `ToHtml` (export). |
| `Services/ScreenCaptureService.cs` | `Pixels` (BGRA buffer: `Crop`, `ToBitmap`, `ToPng`, `FromImage`) and the screen grab: GDI `BitBlt` of the whole virtual screen plus the foreground window's rectangle (Windows), `screencapture -i` (macOS). |
| `Services/CaptureService.cs` | Captures in `captures/` of DataDir: `<id>.png` (original), `<id>.json` (`CaptureItem`), `<id>.annotated.png`. `Load`, `Create`, `Save`, `Delete`, and `Search` (annotation text > title/tags > description > window > OCR). |
| `Services/ImageIndexService.cs`, `ChatRelated.cs` | `ImageIndexService.TickAsync` (20 s timer in `App`; `SearchTextOfNote` feeds the Search tab so notes also match on their pictures' text) OCRs note pictures into `imageocr/<file>.txt` and captures with `OcrDone` false, one per tick, only after 15 s without input. `ChatRelated.Build` adds the notes / captures matching the question's keywords and the open note's picture text to each chat prompt. |
| `Services/OcrService.cs` | Windows OCR through Windows PowerShell 5.1 (WinRT, no package): lines with rectangles. `EnsureAsync` stores them in the capture. |
| `Services/CaptureAi.cs` | AI on captures: `PictureInfo` (OCR lines `L1…` + annotations `A1…` as text, because the CLI cannot see images), `Apply` (JSON `ops` add / update / delete; `"line":"L3"` places a shape on an OCR line), `ApplyDescription`, `AutoDescribeAsync`. Prompts `EditCapture` / `DescribeCapture` are in `AiPrompts`. |
| `Views/AnnotationRenderer.cs`, `AnnotationSurface.cs` | One drawing routine for the editor and the PNG export (`ExportPng`, RenderTargetBitmap); the surface does hit-testing, handles, create / move / resize / crop. |
| `Views/CaptureEditorWindow`, `CapturesView`, `CaptureOverlayWindow.cs` | The annotation editor (autosave, undo of whole states, OCR, AI), the thumbnail gallery with search, and the full-screen region picker (code only). `App.CaptureAsync`, `ShowCapture(id)`, `ShowCaptures`, `OpenCaptureFrom`. |
| `Services/ClipboardImageService.cs` | Reads a pasted picture: Win32 clipboard (`PNG` format, else `CF_DIB` → .bmp) or `osascript` on macOS. |
| `Services/NoteDocument.cs` | Note body model for the visual editor: `Parse` (HTML, incl. old `• `/`1. `/`◷` plain lines → `NoteBlock`s), `ToHtml`, `ToMarkdown`, `FromMarkdown`, and the inline markup (`**b**`, `*i*`, `` `c` ``, `[t](u)`) ↔ HTML conversion. Tables and charts (`Cells`, `Header`, `ChartType`), `GridFromText` (pasted spreadsheet cells), `ParseDelimited`. |
| `Services/ChartLayout.cs` | Lays out a chart block (bar / line / pie) as shapes with SVG path data and colour tokens, so the editor (`ChartCanvas`) and the export (`ToSvg`) draw the same picture. Also `Data` (labels + series from the cells), `ParseNumber`, `HoverAt`, and the categorical palette. |
| `Services/TimestampService.cs` | Timestamp formats: .NET date formats, or a template whose `{…}` parts are formatted (InvariantCulture). |
| `Services/AiService.cs` | Runs the AI CLI (`AskAsync`, never throws): provider `claude` → `claude -p --model <AiModel> --strict-mcp-config --no-session-persistence --tools ""` (default `claude-haiku-5-5`; the flags drop tools and MCP servers from the request, about 7 s → 3 s per call; `--bare` is faster but ignores the saved login), `custom` → `AiCommand`, `kiro` → not supported yet. Prompt on stdin (UTF-8), started in an empty temp folder. Also `{placeholder}` expansion, `ExtractJson`, `ExtractCode`, `StripOuterFence`. |
| `Services/AiPrompts.cs` | Every AI prompt (work report, timesheet, note rewrites, free-form note edits `EditNote` / `EditSelection`, minutes, title/tags, explain/generate script, `TimerChatRules` for the timers chat). |
| `Services/HotkeyService.cs` | The global "Ask AI" hotkey: `RegisterHotKey` on its own message-loop thread (Windows). |
| `Views/AiResultWindow.axaml(.cs)` | Shared dialog: runs an AI request (Stop / Try again), shows the editable answer and action buttons; returns `(action, text)`. |
| `Views/QuickAskWindow`, `Views/AiScriptWindow` | Ask AI (single instance via `App.ShowQuickAsk`), and write-a-script-with-AI. |
| `Views/NoteEditor.axaml(.cs)` | The WYSIWYG note editor (a UserControl): one row per block, each with a rendered `TextBlock` and a `TextBox` that is shown only while that block is edited. Note-wide undo / redo, the `/` block menu, `SetHtml` (undoable replace). |
| `Views/MainWindow.axaml(.cs)` | The shell, the only main window: content (`TabHost`), the tab strip, and the one `AiChatPanel` (open at start), in the order chat | strip | content (chat left, content right). The content is collapsed at start (window 452 wide; `MinWidth` is the strip's 92 so that window widths don't drift when the chat or the content is toggled; while collapsed `MaxWidth` equals chat + strip so the chat can't stretch, collapsing drops any width the user dragged on, and a maximized window is restored first: all in `ResizeFor`; the content's width is remembered in `AppConfig.ContentWidth` by `RememberContentRoom`, called before collapsing and from `FlushTabs`): a tab click calls `ShowTab` (opens it, window +800), a click on the open tab calls `ShowContent(false)`; the chat takes the width while collapsed. With the chat hidden too, `ApplyCompact` turns the window into a small dock (no title bar, `Grip` moves it, sized to the strip; the size is set in a posted call because the frame change resizes the window and turns `SizeToContent` off). The chat has no close button: **Ask AI** / Ctrl+J toggle it. `ShowTab(id)`, `Tab<T>(id)`, `ExistingTab<T>(id)`, `FlushTabs()`, `ShowChat` / `ToggleChat` (Ctrl+J). Closing hides to the tray. Tabs are created on first use and then stay alive (hidden). |
| `Views/ShellTab.cs` | Base class of a tab (`AppsView`, `NotesView`, `CapturesView`, `TimersView`, `WorkLogView`, `SearchView`, `SettingsView`; formerly windows). Provides `Shell`, `Chat`, `Host` (the window, for dialogs), `StorageProvider`, `ChatProfile`, `OnShown` (refresh) and `OnHidden` (save). There is no `Closed` event: a tab lives as long as the app. |
| `Views/AiChatPanel.axaml(.cs)`, `Views/AiChatProfile.cs` | The AI chat, one instance in `MainWindow`, shared by all tabs. The tab in front hands it an `AiChatProfile` (`Use`): texts, quick buttons, `GetContext`, answer buttons, and on the notes tab `ApplyEdits` / `Undo` / `Insert`. Conversations are kept in memory by key (note id, or the profile's fixed key) and several can wait for an answer at once. A message is answered with Markdown, or on the notes tab with `@@` edit commands (`AiPrompts.Chat`) that are shown first and applied on **Apply**. `RunRequest` runs ready-made requests (AI menu, explain script, work report) as turns. Answers stream in (`AiService.AskStreamAsync`) and are drawn as Markdown while they arrive (`MarkdownPreview.BuildInline`, at most every 120 ms; plain text when the partial holds `@@` edit commands); the chat scrolls with them only when already at the bottom; a 1 s heartbeat shows seconds and every 2 min makes sure a warm standby exists (`AiService.Warm`). |
| `Services/SearchService.cs`, `Views/AiChatPanel.Search.cs` | One search over apps (tiles), notes (title / tags / text and the OCR text of their pictures, `ImageIndexService.SearchTextOfNote`), captures and the work log: `SearchService.Run`. Used by the Search tab and by the chat's `@word` quick search (a `State.Search` turn with result rows, no AI). |
| `Views/AiPanelDock.cs` | Shows / hides the chat panel of the main window and grows / shrinks the window by its width (up to the screen). |
| `Services/CronSchedule.cs` | 5-field cron parser (`TryParse`, `Next`, `Upcoming`, local time). `IntervalTimerItem.Cron` replaces `IntervalMinutes` when set; `TimerService` skips a cron run missed by more than 2 minutes. |
| `Views/NoteTableView.cs`, `Views/NoteChartView.cs` | Table and chart blocks in the editor (`GridBlockView`): a grid of cells (rendered / edited like blocks), and a chart (title, type, `ChartCanvas`, and its data as a `NoteTableView`). |
| `Views/*` | One tab per feature (see `ShellTab`). Separate windows remain for the capture editor and its region picker, dialogs (`EditScriptWindow`, `ScanWindow`, `IconPickerWindow`, `AiScriptWindow`), `QuickAskWindow`, and the `ToastWindow.cs` corner pop-up (code only). `UiHelpers.cs` holds `Dialogs.Confirm` / `Message` / `Prompt`, which take any control as owner. |

## Data files

Settings live in `ConfigDir`: `%APPDATA%\Toolshed360`, or the folder in the `TOOLSHED360_DATA` environment variable.
Notes, images and the work log live in `DataDir`: the `DataFolder` setting (e.g. a OneDrive folder shared by several PCs), else `ConfigDir`.

- `config.json` (ConfigDir): scripts, timers and settings.
- `notes/<id>.json` (DataDir): one note per file, written atomically (.tmp + move). The body is HTML; the visual editor writes one block per line (`<p>`, `<h1-6>`, `<ul>`/`<ol>`, `<ul class="timed">` with `<span class="t">stamp</span>`, `<blockquote>`, `<pre><code>`, `<hr>`, `<p><img src="images/…"></p>` for a picture, `<table>` with one `<tr>` per line, and `<figure class="chart" data-type="bar|line|pie">` holding an optional `<figcaption>` and a `<table>` for a chart). An old single `notes.json` is split up once and renamed `notes.json.migrated`.
- `captures/` (DataDir): `<id>.png`, `<id>.json`, `<id>.annotated.png` (see `CaptureService`).
- `images/` (DataDir): pictures, referenced relative to DataDir (`images/20261011-093000-1a2b3c4d.png`).
- `timers.json` (DataDir): the timers, only when `SyncTimers` is on.
- `worklog/yyyy-MM-dd.jsonl` (DataDir; `yyyy-MM-dd.<MachineName>.jsonl` when synced): one JSON object per line, UTF-8 without a BOM. The `k` field is:
  - `s`: foreground sample (`app`, `title`, `sec`)
  - `o`: open windows + running scripts snapshot
  - `l`: launch from the dashboard
  - `t`: timer run
  - `n`: note created
- `worklog/errors.log` (ConfigDir): sampling exceptions.

## Conventions

- **UI style:**
  - Code-behind only: no MVVM or bindings.
  - XAML handles layout; lists and rows are built in C#.
  - Match the existing code: short `///` summaries, comments only for non-obvious reasons.
- **Background threads:** `TimerService.Ran`, `TimerService.PauseChanged` and the work-log sampling run on worker threads. Use `Dispatcher.UIThread.Post` before touching the UI.
- **Settings changes:** after Settings saves, `App.SettingsChanged` fires. Tabs that depend on settings subscribe in their constructor; tabs live as long as the app, so they never unsubscribe. `SaveConfig()` also rebuilds the tray menu.
- **Tabs:** a new feature is a `ShellTab` added with `AddTab` in `MainWindow`, with an `App.ShowX()` method that calls `ShowMainWindow("id")` and a tray entry. Don't use `Closed` / `Opened` / `Activated` or `Close()` in a tab (use `OnShown` / `OnHidden`; Escape or Close go back with `Shell.ShowTab`). Create the chat profile in the constructor and don't touch `Shell` there: it is set after construction.
- **Text sizes and theme:**
  - The type scale is 11 caption / 12 secondary / 13 body / 15 section and tab headers / 20 page title; a note's own text is 14 (headings 22 / 18 / 16 / 15). Body and tab-header sizes come from `Application.Resources` in `App.axaml` (`ControlContentThemeFontSize`, `TabItemHeaderFontSize`); don't add other `FontSize` values, and don't put a global `TextBlock` style (it overrides a button's inherited size).
  - `AppConfig.Theme` (`system` / `dark` / `light`) is applied by `App.ApplyTheme` at start and when settings change; Settings → General applies it at once and saves it itself. Colours must work on both: use semi-transparent greys (`#26808080`), `Foreground="Gray"` or theme resources, and check `ActualThemeVariant` for anything else (see `ToastWindow`, `NoteChartView`).
- **Accessibility:**
  - Icon-only buttons and row buttons need `AutomationProperties.Name`. It is also what UI tests use to find them.
  - Toolbar buttons also need a `ToolTip.Tip`.
- **Logging and services never throw into the UI:** work-log `Append` swallows errors, and the timer, scan and sample code catches its own exceptions.
- **Win32 P/Invoke** lives in nested `Native*` classes and is guarded with `OperatingSystem.IsWindows()`.
- **Shell-outs instead of packages:**
  - `reg` / LaunchAgents for auto-start, and `Get-CimInstance` / `ps` for scanning.
  - Don't add System.Management or Registry NuGet packages.

## Gotchas

- **Material.Icons.Avalonia stays at exactly 3.0.0.** Newer versions need Avalonia 12.
- **XAML initialisation order:** `SelectionChanged` / `TextChanged` handlers declared in XAML fire while `InitializeComponent` is still running, before later controls exist. Null-guard them (see `ApplyActionVisibility`, `UpdatePreview`).
- **Never set `Foreground = null`** to mean "theme default": it makes the text invisible. Only assign it when overriding.
- **Notes dirty state:** compare against a `_baseline` snapshot; don't use a flag set in `TextChanged`, because it fires late for programmatic edits.
- **Note editor keys:** Enter / Backspace / Delete / arrows / Tab are handled by **tunnel** `KeyDown` handlers (on each block's TextBox in `NoteEditor`, and on `HtmlBox` in the HTML view), so they run before the TextBox handles them.
- **Visual note editor:**
  - Change a block's kind with `ApplyKind` (restyles in place) rather than rebuilding the row, so focus survives.
  - **Undo is the editor's, not the TextBoxes':** a tunnel handler on `NoteEditor` takes Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z everywhere in the note (cells and stamps too). Every edit must end in `OnChanged()` (never `Changed?.Invoke()` directly), which snapshots the note (`EditorState`: HTML + row + caret); `OnChanged(typing: true)` merges quick typing in one row into one step. Undo re-applies a snapshot with `ApplyHtml`, which rebuilds only the blocks between the unchanged head and tail. `Load` starts a new history; use `SetHtml` for changes that should be undoable (AI edits, back from the HTML view).
  - The `/` menu (`SlashPopup`) opens while a paragraph holds just `/` and a filter; the TextBox keeps the focus, so its keys are taken in `SlashKey` and items apply on pointer press. `CurrentChanged` / `CurrentBlock` drive the toolbar's highlighted button.
  - `EndEdit` does nothing while the window is inactive: the window gives the focus back to the TextBox on activation, which fails if it was hidden (the caret was lost after Alt+Tab). `window.Activated` closes blocks that didn't get it back.
  - Toolbar buttons are `Focusable=False` (style in `NotesView.axaml`), so the caret and selection stay in the block. A TextBox clears its selection when it loses focus.
  - `SetText` writes `Block.Text` before `TextBox.Text`, so the late `TextChanged` sees no change.
  - Markdown shortcuts (`- `, `# `, …) are applied through `Dispatcher.UIThread.Post`, not inside `TextChanged`.
  - Anything the editor can't represent (tables, elements with `style=`, full documents, comments) becomes a `Raw` block and is written back verbatim.
- **Dirty state across views:** `NotesView.CurrentHtml()` is the editor's HTML or the HTML source. Switching views re-formats it, so the baseline is reset when the note wasn't dirty.
- **Timer rescheduling:** `TimerService.Reschedule(id)` restarts one timer only. `Resume()` clears every countdown on purpose, so timers don't all fire at once.
- **Work-log time accounting:**
  - Samples skip idle time (`GetLastInputInfo`) and the lock screen (`LockApp`).
  - Elapsed time is capped at twice the sample interval, so sleep or hibernate isn't counted.
- **AI chat panel:**
  - Outside the notes tab the profile has `NoteMode` false: its `GetContext` returns the tab's text in `Html` and its subject in `Title`, answers use `AiPrompts.ChatAbout` and never edit; it sets `AnswerActions` (or `AnswerActionsFor(answer)`) / `OnAnswerAction`, `Quick`, and `SetLabel`. The timers tab asks the model for a ```timer JSON block (`AiPrompts.TimerChatRules`) and offers **Load into editor**. A new tab makes a profile like the existing ones.
  - The host (`NotesView`) owns the note: the panel only calls `GetContext`, `ApplyEdits`, `Undo`, `Insert` of its profile. Edits carry the note's HTML from when the question was sent; `Chat_ApplyEdits` refuses when the note has changed since (the block numbers would not fit).
  - A text box drops its selection when focus leaves it, so the host captures the selection when the pointer goes down in the panel (`Pressing` → `CaptureChatSelection`), and the editor's `GotFocus` clears it again.
  - Menu-action buttons restore the selection that was current when the request started before applying (`RunAiAction`).
- **AI:**
  - Every AI feature uses the shared helpers in `AiService`: `Asking(task)` / `Failed(result)` for status text, and `AnswerToNote(title, tags, answer, intro)` to turn an answer into a note (it strips the outer code fence). Don't build these by hand.
  - Chat prompts (`AiPrompts.Chat` / `ChatAbout`) carry `FollowUpRules` and `PendingFollowUp`: the CLI gets each message with only the last four turns, so when the previous answer was a question ("what should I translate?") the prompt says the new message answers it and names the earlier request. A translation with no target language goes to the Settings answer language, else English (and asks when the text is already English). Test prompt changes from pwsh 7 by calling `AiPrompts` and `AiService.AskAsync` directly; the model varies, so run a case several times.
  - Notes made outside the notes tab (AI answers, day summaries) go through `App.AddNote`: an open notes tab keeps its own list and would overwrite `notes.json` on its next save.
  - The notes AI menu captures the selection (`NoteEditor.CaptureSelection`) in the button's `Click` before showing the flyout; the flyout takes focus and the TextBox drops its selection.
  - Answers are Markdown; `NoteDocument.FromMarkdown` turns them into blocks (Markdown tables become Table blocks, a ```` ```chart [type] ```` fence with optional `title:` / `type:` lines and CSV or a Markdown table becomes a Chart).
  - The end-of-day summary runs from a 1-minute timer in `App` (`AiDaySummaryLast` makes it once per working day).
  - Note AI actions send `NotesService.NoteToAiMarkdown` (charts as ```` ```chart ```` fences, HTML blocks verbatim), which `FromMarkdown` reads back without loss, so "Replace the note" keeps charts and HTML blocks.
  - "Ask AI to change the note…" (whole note) doesn't trust the model to repeat the note: Haiku answers with only the new part. The note goes out as numbered units (`NoteDocument.Units`: a list or quote is one unit). The model replies with `@@ replace 3[-5]` / `@@ insert after 2` / `@@ delete 4` commands, and `ParseEdits` + `ApplyEdits` apply them, keeping untouched blocks as they are. An answer without commands is added at the end.
- **Sync (data folder):**
  - Save notes one at a time with `NotesService.Save(note)`; never rewrite all notes, or two PCs overwrite each other.
  - `NotesService` remembers what it wrote, so the watcher ignores our own saves. Files whose name isn't `<id>.json` (sync conflict copies) are adopted as a separate "(conflicting copy)" note, never dropped.
  - The work log reads every PC's file for a day and sorts by time; each PC appends only to its own file.
  - Changing the data folder copies notes, images and the work log there (`CopyTo`), then restarts the app (`App.Restart`).
- **Tables and charts:**
  - A `<table>` becomes a Table block only when the editor can write it back unchanged in meaning: no attributes, no colspan, `<th>` only in the whole first row, inline content only. Anything else stays a Raw block.
  - They are `IsGrid` blocks: not `IsAtomic` (arrows go into them; Backspace after one moves into its last cell instead of deleting it), and `HasMarkup` is false. Their row has a `GridBlockView`; `FocusRow(row, caret > 0)` focuses its last cell.
  - Toolbar actions go through `ActiveTarget()` (the block's TextBox or the current cell), so bold, link, code and time work in cells.
  - A new table's cells are only in the visual tree after its ScrollViewer has a template, so `FocusCell` posts the focus once when the cell isn't attached yet.
  - Charts take at most 8 series (pie: 7 slices + "Other"); text columns are skipped. Colours come from `ChartLayout.Color` (light and dark steps); the export draws light colours.
- **Captures:**
  - Captures are not in `images/`, which `DeleteUnusedImages` cleans; "New note with image" copies the annotated PNG there.
  - `Annotation.Kind` values are in `AnnotationKind` (a `const Text` can't live inside `Annotation`, which has a `Text` property). Coordinates are pixels of the original picture; the surface scales by `Zoom`.
  - Text, arrow and callout labels: an arrow / line starts inside its label (centred on the tail); a callout's pointer starts at its box centre under the box.
  - Don't set `FormattedText.MaxTextWidth = 0` for "no wrap": the text disappears. Leave it unset.
  - `TextChanged` on boxes set while opening fires late: compare with the saved values before marking dirty. Tool buttons use `IsCheckedChanged` (guarded by `_syncingTools`) so UI Automation can select them.
  - Tools are one table (`Tools`: icon, letter key, group, hint); a letter picks a tool only outside text boxes. `IsRepeatTool` (step, highlight, redact, blur) keeps the tool after a shape; the others go back to Select (`ToolDone`). An empty Text annotation is removed when it loses the selection (it would be invisible).
  - Delete a capture with `App.DeleteCapture`: it closes the open editor through `Discard()` first, otherwise its autosave on close writes the capture back. The automatic AI title on close follows `AiAutoTitle`, like notes.
  - The gallery ignores `CaptureService.Changed` while hidden (an open editor saves about every second) and keeps decoded thumbnails by file time.
  - The default hotkey `Ctrl+Alt+C` was chosen because `Ctrl+Alt+S` and `Ctrl+Alt+A` are taken on the author's PC; `HotkeyService` has numbered slots (0 = Ask AI, 1 = capture).
  - Test with `TOOLSHED360_DATA` on generated PNGs; a test hotkey press also reaches other screenshot tools the user runs.
- **Pictures:** a picture on its own line is a `BlockKind.Image` block (`IsAtomic`, like a divider: the caret skips it, Backspace/Delete next to it remove it). The editor decodes at most 1600 px wide and disposes bitmaps on `Load` / row removal. Paste prefers text over a picture (Excel and browsers put both on the clipboard).
- **Non-ASCII in .ps1 test scripts:** characters like `◷` and `•` get mangled in .ps1 files saved without a BOM. Build them with `[char]0x25F7`.

## Testing (no unit-test project)

- **Logic:** load the built DLL from **pwsh 7**. Windows PowerShell 5.1 can't load net8 assemblies.
  `Add-Type -Path bin/Debug/net8.0/Toolshed360.dll`, then call static methods such as `NotesService.HtmlToMarkdown`, `NoteDocument.Parse` / `ToHtml` (check that the HTML round-trips) or `new WorkHours(config).Contains(...)`.
- **UI:** run the debug exe and drive it with UIAutomation from pwsh:
  - Find windows by title and buttons by AutomationProperties.Name. `x:Name` becomes the AutomationId.
  - Set text with `ValuePattern`.
  - Take screenshots with `System.Drawing` `CopyFromScreen`.
  - Avalonia `MenuItem`s in flyouts don't respond to `InvokePattern`; click them with the real mouse (`SetCursorPos` + `mouse_event`).
  - Native dialogs (save file) need `SendKeys`.
- **Demo data / screenshots:** start the exe with `TOOLSHED360_DATA` pointing at a scratch folder holding a generated `config.json`, `notes.json` and `worklog/`. Never put the user's real notes or window titles in README images. Check that the captured window really was in front (`GetForegroundWindow`) before saving the image.
- **ValuePattern caret:** after `SetValue` the caret is at 0, so send `{END}` before `{ENTER}` when testing the note editor.
- **Don't damage the user's data:**
  - Back up `config.json` before injecting test timers or settings, and restore it afterwards.
  - Remove only the test lines you added to today's `worklog/*.jsonl`; it also holds the user's real activity.
  - Delete test notes when you're done.
- **Clipboard and screenshots in tests** fail when the Windows session is locked (`LogonUI` running, `GetForegroundWindow` = 0). The common file dialog exposes only panes to UIAutomation; type a path with `SendInput` + `KEYEVENTF_UNICODE` (it bypasses a Telex input method).
- **Headless UI tests** (works while the session is locked): a scratch console project referencing `bin/Debug/net8.0/Toolshed360.dll` plus `Avalonia.Headless`, `Avalonia.Skia` and `Avalonia.Themes.Fluent` 11.3.2. Host a `NoteEditor` in a `Window`, drive it with `KeyPress` / `KeyTextInput` / `MouseDown`, and save `CaptureRenderedFrame()` as a PNG. Don't add it to the repo.
- **AI in UI tests:** each call takes 5–20 s with Haiku. Text typed with SendKeys can be rewritten by a Vietnamese (Telex) input method; set values with `ValuePattern` instead.
- **Test copies and global hotkeys:** a test copy started with `TOOLSHED360_DATA` registers the same global hotkeys (Ctrl+Alt+C capture, the Ask AI key) as the user's app and steals them while it runs. Put `"CaptureHotkey": ""` and `"AiHotkey": ""` in the scratch `config.json`, and never leave a test copy running: the user may be working in it.
- **Idle rule during tests:** automated tests produce no keyboard or mouse input, so after 5 min the work log records nothing. That is correct behaviour, not a bug.
