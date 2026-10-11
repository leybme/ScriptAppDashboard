# CLAUDE.md

Guidance for working on Script App Dashboard: a .NET 8 + Avalonia 11.3.2 (Fluent) tray app that launches the user's scripts, plus notes, interval timers and a work log. Targets Windows and macOS. Most features are built and tested on Windows.

## Build & run

```bash
dotnet build -c Debug                         # must finish with 0 errors / 0 warnings
dotnet run                                    # add --minimized to start in the tray only
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

- The debug exe is `bin/Debug/net8.0/ScriptAppDashboard.exe`.
- A running instance locks it, so kill `ScriptAppDashboard` before rebuilding.
- CI and the tag-triggered release live in `.github/workflows/`; README.md explains how to cut a release.

## Layout

| Path | What |
|---|---|
| `App.axaml.cs` | Startup and the tray menu (`RefreshTrayMenu`). Opens every window as a single instance (`ShowNotes`, `ShowTimers`, `ShowWorkLog`, `ShowSettings`, `ShowSearch`, `ShowNote(id)`). Holds the app-wide events `SettingsChanged` / `TimersPauseChanged` and the timer pop-ups (`NotifyTimer`). |
| `Models/ScriptItem.cs` | `ScriptItem`, `AppConfig` (all settings), `IntervalTimerItem`, `NoteItem`. |
| `Services/ConfigService.cs` | Loads and saves `config.json` in `ConfigDir` (this PC). `DataDir` is the (possibly OneDrive-synced) folder for notes, images and the work log (`DataFolder` setting, else `ConfigDir`); `timers.json` there when `SyncTimers`. `AtomicWrite` (.tmp + move). |
| `Services/LauncherService.cs`, `ProcessScanner.cs`, `StartupService.cs` | Launch scripts, scan running scripts/apps (`Scan`, `ScanApps`), auto-start. |
| `Services/WorkLogService.cs` | Records work to daily JSONL files in `worklog/` and analyses them (`Summarize`, `Search`, `TodayActiveSeconds`, `ToMarkdown`). |
| `Services/WorkHours.cs` | Working-hours rules (wraps past midnight for night shifts). |
| `Services/TimerService.cs` | 1-second ticker that runs the timers (beep / shell command / AI CLI via stdin). Supports pause and resume (`TimersPausedUntil` in the config). |
| `Services/NotesService.cs` | One file per note (`Load`, `Save(note)`, `Delete`), migration from the old `notes.json`, conflict-copy adoption, `Watch` + `ExternalChange` for other PCs' edits, pictures (`AddImage`, `ResolveImage`, `MapImages`, `DeleteUnusedImages`). Also search, `HtmlToMarkdown` / `ToMarkdown` / `ToHtml` (export). |
| `Services/ClipboardImageService.cs` | Reads a pasted picture: Win32 clipboard (`PNG` format, else `CF_DIB` → .bmp) or `osascript` on macOS. |
| `Services/NoteDocument.cs` | Note body model for the visual editor: `Parse` (HTML, incl. old `• `/`1. `/`◷` plain lines → `NoteBlock`s), `ToHtml`, `ToMarkdown`, and the inline markup (`**b**`, `*i*`, `` `c` ``, `[t](u)`) ↔ HTML conversion. |
| `Services/TimestampService.cs` | Timestamp formats: .NET date formats, or a template whose `{…}` parts are formatted (InvariantCulture). |
| `Services/AiService.cs` | Runs the AI CLI (`AskAsync`, never throws): provider `claude` → `claude -p --model <AiModel>` (default `claude-haiku-5-5`), `custom` → `AiCommand`, `kiro` → not supported yet. Prompt on stdin (UTF-8), started in an empty temp folder. Also `{placeholder}` expansion, `ExtractJson`, `ExtractCode`, `StripOuterFence`. |
| `Services/AiPrompts.cs` | Every AI prompt (work report, timesheet, note rewrites, minutes, title/tags, explain/generate script, timer from text). |
| `Services/HotkeyService.cs` | The global "Ask AI" hotkey: `RegisterHotKey` on its own message-loop thread (Windows). |
| `Views/AiResultWindow.axaml(.cs)` | Shared dialog: runs an AI request (Stop / Try again), shows the editable answer and action buttons; returns `(action, text)`. |
| `Views/QuickAskWindow`, `Views/AiScriptWindow` | Ask AI (single instance via `App.ShowQuickAsk`), and write-a-script-with-AI. |
| `Views/NoteEditor.axaml(.cs)` | The WYSIWYG note editor (a UserControl): one row per block, each with a rendered `TextBlock` and a `TextBox` that is shown only while that block is edited. |
| `Views/*` | One window per feature. `ToastWindow.cs` is the corner pop-up, built in code only. `UiHelpers.cs` holds `Dialogs.Confirm`. |

## Data files

Settings live in `ConfigDir`: `%APPDATA%\ScriptAppDashboard`, or the folder in the `SCRIPTAPPDASHBOARD_DATA` environment variable.
Notes, images and the work log live in `DataDir`: the `DataFolder` setting (e.g. a OneDrive folder shared by several PCs), else `ConfigDir`.

- `config.json` (ConfigDir): scripts, timers and settings.
- `notes/<id>.json` (DataDir): one note per file, written atomically (.tmp + move). The body is HTML; the visual editor writes one block per line (`<p>`, `<h1-6>`, `<ul>`/`<ol>`, `<ul class="timed">` with `<span class="t">stamp</span>`, `<blockquote>`, `<pre><code>`, `<hr>`, and `<p><img src="images/…"></p>` for a picture). An old single `notes.json` is split up once and renamed `notes.json.migrated`.
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
- **Settings changes:** after Settings saves, `App.SettingsChanged` fires. Windows that depend on settings subscribe in their constructor and unsubscribe in `Closed`. `SaveConfig()` also rebuilds the tray menu.
- **Single-instance windows:** a new window goes through an `App.ShowX()` method, gets a tray entry, and gets a main-window button if it is used often.
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
  - Change a block's kind with `ApplyKind` (restyles in place) rather than rebuilding the row, so focus and undo survive.
  - Toolbar buttons are `Focusable=False` (style in `NotesWindow.axaml`), so the caret and selection stay in the block. A TextBox clears its selection when it loses focus.
  - `SetText` writes `Block.Text` before `TextBox.Text`, so the late `TextChanged` sees no change.
  - Markdown shortcuts (`- `, `# `, …) are applied through `Dispatcher.UIThread.Post`, not inside `TextChanged`.
  - Anything the editor can't represent (tables, elements with `style=`, full documents, comments) becomes a `Raw` block and is written back verbatim.
- **Dirty state across views:** `NotesWindow.CurrentHtml()` is the editor's HTML or the HTML source. Switching views re-formats it, so the baseline is reset when the note wasn't dirty.
- **Timer rescheduling:** `TimerService.Reschedule(id)` restarts one timer only. `Resume()` clears every countdown on purpose, so timers don't all fire at once.
- **Work-log time accounting:**
  - Samples skip idle time (`GetLastInputInfo`) and the lock screen (`LockApp`).
  - Elapsed time is capped at twice the sample interval, so sleep or hibernate isn't counted.
- **AI:**
  - Notes made outside the notes window (AI answers, day summaries) go through `App.AddNote`: an open `NotesWindow` keeps its own list and would overwrite `notes.json` on its next save.
  - The notes AI menu captures the selection (`NoteEditor.CaptureSelection`) in the button's `Click` before showing the flyout; the flyout takes focus and the TextBox drops its selection.
  - Answers are Markdown; `NoteDocument.FromMarkdown` turns them into blocks (tables become Raw `<table>` blocks).
  - The end-of-day summary runs from a 1-minute timer in `App` (`AiDaySummaryLast` makes it once per working day).
- **Sync (data folder):**
  - Save notes one at a time with `NotesService.Save(note)`; never rewrite all notes, or two PCs overwrite each other.
  - `NotesService` remembers what it wrote, so the watcher ignores our own saves. Files whose name isn't `<id>.json` (sync conflict copies) are adopted as a separate "(conflicting copy)" note, never dropped.
  - The work log reads every PC's file for a day and sorts by time; each PC appends only to its own file.
  - Changing the data folder copies notes, images and the work log there (`CopyTo`), then restarts the app (`App.Restart`).
- **Pictures:** a picture on its own line is a `BlockKind.Image` block (`IsAtomic`, like a divider: the caret skips it, Backspace/Delete next to it remove it). The editor decodes at most 1600 px wide and disposes bitmaps on `Load` / row removal. Paste prefers text over a picture (Excel and browsers put both on the clipboard).
- **Non-ASCII in .ps1 test scripts:** characters like `◷` and `•` get mangled in .ps1 files saved without a BOM. Build them with `[char]0x25F7`.

## Testing (no unit-test project)

- **Logic:** load the built DLL from **pwsh 7**. Windows PowerShell 5.1 can't load net8 assemblies.
  `Add-Type -Path bin/Debug/net8.0/ScriptAppDashboard.dll`, then call static methods such as `NotesService.HtmlToMarkdown`, `NoteDocument.Parse` / `ToHtml` (check that the HTML round-trips) or `new WorkHours(config).Contains(...)`.
- **UI:** run the debug exe and drive it with UIAutomation from pwsh:
  - Find windows by title and buttons by AutomationProperties.Name. `x:Name` becomes the AutomationId.
  - Set text with `ValuePattern`.
  - Take screenshots with `System.Drawing` `CopyFromScreen`.
  - Avalonia `MenuItem`s in flyouts don't respond to `InvokePattern`; click them with the real mouse (`SetCursorPos` + `mouse_event`).
  - Native dialogs (save file) need `SendKeys`.
- **Demo data / screenshots:** start the exe with `SCRIPTAPPDASHBOARD_DATA` pointing at a scratch folder holding a generated `config.json`, `notes.json` and `worklog/`. Never put the user's real notes or window titles in README images. Check that the captured window really was in front (`GetForegroundWindow`) before saving the image.
- **ValuePattern caret:** after `SetValue` the caret is at 0, so send `{END}` before `{ENTER}` when testing the note editor.
- **Don't damage the user's data:**
  - Back up `config.json` before injecting test timers or settings, and restore it afterwards.
  - Remove only the test lines you added to today's `worklog/*.jsonl`; it also holds the user's real activity.
  - Delete test notes when you're done.
- **Clipboard and screenshots in tests** fail when the Windows session is locked (`LogonUI` running, `GetForegroundWindow` = 0). The common file dialog exposes only panes to UIAutomation; type a path with `SendInput` + `KEYEVENTF_UNICODE` (it bypasses a Telex input method).
- **AI in UI tests:** each call takes 5–20 s with Haiku. `AiResultWindow` dialogs are owned windows, so look for them among the owner's child windows too. Text typed with SendKeys can be rewritten by a Vietnamese (Telex) input method; set values with `ValuePattern` instead.
- **Idle rule during tests:** automated tests produce no keyboard or mouse input, so after 5 min the work log records nothing. That is correct behaviour, not a bug.
