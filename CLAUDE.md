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
| `Services/ConfigService.cs` | Loads and saves `config.json` in `ConfigDir` (%APPDATA%\ScriptAppDashboard). |
| `Services/LauncherService.cs`, `ProcessScanner.cs`, `StartupService.cs` | Launch scripts, scan running scripts/apps (`Scan`, `ScanApps`), auto-start. |
| `Services/WorkLogService.cs` | Records work to daily JSONL files in `worklog/` and analyses them (`Summarize`, `Search`, `TodayActiveSeconds`, `ToMarkdown`). |
| `Services/WorkHours.cs` | Working-hours rules (wraps past midnight for night shifts). |
| `Services/TimerService.cs` | 1-second ticker that runs the timers (beep / shell command / AI CLI via stdin). Supports pause and resume (`TimersPausedUntil` in the config). |
| `Services/NotesService.cs` | Reads and writes `notes.json`. Also holds search, `RenderLists` (list lines → HTML for preview), `HtmlToMarkdown` / `ToMarkdown` (export). |
| `Services/TimestampService.cs` | Timestamp formats: .NET date formats, or a template whose `{…}` parts are formatted (InvariantCulture). |
| `Views/*` | One window per feature. `ToastWindow.cs` is the corner pop-up, built in code only. `UiHelpers.cs` holds `Dialogs.Confirm`. |

## Data files

All under `%APPDATA%\ScriptAppDashboard`:

- `config.json`: scripts, timers and settings.
- `notes.json`: notes. Written atomically via a `.tmp` file then a move; an unreadable file is first backed up as `.bad`.
- `worklog/yyyy-MM-dd.jsonl`: one JSON object per line, UTF-8 without a BOM. The `k` field is:
  - `s`: foreground sample (`app`, `title`, `sec`)
  - `o`: open windows + running scripts snapshot
  - `l`: launch from the dashboard
  - `t`: timer run
  - `n`: note created
- `worklog/errors.log`: sampling exceptions.

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
- **Note editor Enter key:** list continuation (bullets, numbers, `◷ time · text` time bullets) uses a **tunnel** `KeyDown` handler on `HtmlBox`, so it runs before the TextBox handles Enter.
- **Timer rescheduling:** `TimerService.Reschedule(id)` restarts one timer only. `Resume()` clears every countdown on purpose, so timers don't all fire at once.
- **Work-log time accounting:**
  - Samples skip idle time (`GetLastInputInfo`) and the lock screen (`LockApp`).
  - Elapsed time is capped at twice the sample interval, so sleep or hibernate isn't counted.
- **Non-ASCII in .ps1 test scripts:** characters like `◷` and `•` get mangled in .ps1 files saved without a BOM. Build them with `[char]0x25F7`.

## Testing (no unit-test project)

- **Logic:** load the built DLL from **pwsh 7**. Windows PowerShell 5.1 can't load net8 assemblies.
  `Add-Type -Path bin/Debug/net8.0/ScriptAppDashboard.dll`, then call static methods such as `NotesService.HtmlToMarkdown` or `new WorkHours(config).Contains(...)`.
- **UI:** run the debug exe and drive it with UIAutomation from pwsh:
  - Find windows by title and buttons by AutomationProperties.Name. `x:Name` becomes the AutomationId.
  - Set text with `ValuePattern`.
  - Take screenshots with `System.Drawing` `CopyFromScreen`.
  - Avalonia `MenuItem`s in flyouts don't respond to `InvokePattern`; click them with the real mouse (`SetCursorPos` + `mouse_event`).
  - Native dialogs (save file) need `SendKeys`.
- **Don't damage the user's data:**
  - Back up `config.json` before injecting test timers or settings, and restore it afterwards.
  - Remove only the test lines you added to today's `worklog/*.jsonl`; it also holds the user's real activity.
  - Delete test notes when you're done.
- **Idle rule during tests:** automated tests produce no keyboard or mouse input, so after 5 min the work log records nothing. That is correct behaviour, not a bug.
