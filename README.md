# Script App Dashboard

A small cross-platform (Windows / macOS) desktop app for the people who live in scripts. It launches all your Python tools, shell scripts and utilities from one dashboard, and it also keeps quick notes, runs interval timers and logs where your working time goes. Built with .NET 8 and Avalonia UI.

![Script App Dashboard main window](docs/images/screenshotApp.png)

## Features

### Dashboard

- **Tile dashboard:** every script is a clickable tile. Click to run; right-click to Run, Edit, Open Containing Folder or Remove.
- **Custom icon, path and parameters:** each tile has a name, script path, command-line parameters, working directory and an optional icon. Pick one of 7,000+ searchable **Material Design Icons** (Apache 2.0, via the MIT-licensed `Material.Icons.Avalonia` package) or use your own .png / .jpg / .ico. Without an icon, a coloured initials tile is generated.
- **Interpreter support:** detected from the extension (`.py`, `.ps1`, `.sh`, `.js`, `.bat`/`.cmd`, `.exe`). You can also pick one explicitly, including a *custom command* such as a venv's `python.exe -u`.
- **Scan running scripts and apps:** finds scripts that are already running (python, node, powershell, bash, …) and open apps, and adds them to the dashboard with one click, parameters included.
- **Hide to tray:** closing the window hides the app to the system tray or menu bar. The tray menu lists your scripts, so you can launch them without opening the window.
- **Start with the system:** one checkbox registers auto-start. It uses the `HKCU\...\Run` registry key on Windows and a `~/Library/LaunchAgents` plist on macOS. The app starts hidden in the tray (`--minimized`).
- **Keep console open:** an optional per-script setting. It wraps the run in `cmd /k` on Windows, or a Terminal `.command` file on macOS, so you can read the output after the script finishes.

![Scan running scripts window](docs/images/screenshotScanRunningScripts.png)

### Notes with a visual editor

Write notes the way you would in a word processor. Headings, **bold**, *italic*, links, lists and code show formatted as you type. Only the line you are editing shows its formatting marks, Obsidian-style, so you can still fix them by hand.

![Notes window with the visual editor](docs/images/screenshotNotes.png)

- **Toolbar:**
  - Bold (Ctrl+B), Italic (Ctrl+I), Link (Ctrl+K) and inline code.
  - Headings (click H again for a smaller one), quote, bulleted and numbered lists.
  - **Time bullets:** each new item is stamped with the current date and time when you press Enter. Handy for a running work log.
- **Markdown shortcuts at the start of a line:**
  - `- ` for a bullet and `1. ` for a numbered item.
  - `# ` / `## ` / `### ` for headings.
  - `> ` for a quote, ```` ``` ```` for a code block and `---` for a divider.
- **Keyboard:**
  - Enter continues a list, and Enter on an empty item ends it.
  - Tab / Shift+Tab indents and outdents list items.
  - Backspace at the start of a line joins it to the line above.
  - The arrow keys move between lines.
  - Pasting several lines creates one block per line, and list and heading prefixes are recognised.
- **Links:** Ctrl+click a link to open it.
- **Edit HTML:** notes are stored as clean HTML. **Edit HTML** shows the source; anything the visual editor can't represent, such as tables or styled elements, is kept as an HTML block.
- **Search and tags:** search by title, tags or text. Ctrl+S saves, Ctrl+N starts a new note, Ctrl+F searches.
- **Export:** export one note or all matching notes as Markdown, export a note as a standalone HTML file, or preview it in the browser.

The paragraph being edited shows its formatting marks:

![Editing a paragraph shows its formatting marks](docs/images/screenshotNotesEditing.png)

### Interval timers

Repeat a reminder every 30 seconds to every few hours. A timer can:

- beep,
- run a shell command, or
- send a prompt to an AI command-line tool that reads stdin, such as `claude -p`.

A small pop-up in the screen corner shows your message and the output. It doesn't steal focus and closes by itself. You can pause all timers from the window or the tray for 30 minutes, 1 hour, 2 hours, until tomorrow morning or until you resume. Templates get you started with an eye break, a stretch reminder, an AI check-in or a scheduled command.

![Interval timers](docs/images/screenshotTimers.png)

![Timer pop-up](docs/images/screenshotToast.png)

### Work log

When turned on, the app records which window you work in about every 30 seconds:

- the app and window title, such as the browser tab or the file in your editor,
- scripts you launch from the dashboard,
- timer runs and new notes.

Idle time (5 minutes without input by default) and the lock screen are not counted. The Work Log window shows time per app, per window or browser tab, per hour of the day and per day for today, the last 7 days or the last 30 days. You can limit it to your working hours (for example 09:00–18:00, Mon–Fri; night shifts are supported) and copy the summary as Markdown. The status bar shows today's active time.

![Work log](docs/images/screenshotWorkLog.png)

Everything stays on your machine, in daily JSON-lines files.

### Search everything

One search box (Ctrl+F in the main window) looks through notes and the work log together. Results are grouped by day, with the time spent in matching windows.

![Search notes and the work log](docs/images/screenshotSearch.png)

### Copy time

Many tasks need the current time pasted into a file or a ticket. **Copy time** in the main window or the tray copies it in your chosen format. Use a .NET date format or a template such as `[{yyyy-MM-dd HH:mm}]`, and pick a preset from Settings. The same format is used by the **Time** button in notes, and the button can be switched off.

![Timestamp settings](docs/images/screenshotSettingsTimestamp.png)

## Run

```
dotnet run
```

## Publish a self-contained app

```
# Windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# macOS (Apple Silicon / Intel)
dotnet publish -c Release -r osx-arm64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
```

The output lands in `bin/Release/net8.0/<rid>/publish/`. Turn on "Start with the system" only **after** moving the app to its final location, because the registered path is the path of the running executable.

## Download

Ready-made builds for Windows (x64) and macOS (Intel and Apple Silicon) are on the [Releases](https://github.com/leybme/ScriptAppDashboard/releases) page.

- Unzip and run `ScriptAppDashboard.exe` (Windows) or `ScriptAppDashboard` (macOS). No installation is needed.
- The binaries are unsigned:
  - On Windows, SmartScreen may warn the first time. Choose "More info → Run anyway".
  - On macOS, Gatekeeper warns on first launch. Right-click → Open, or run `xattr -d com.apple.quarantine ScriptAppDashboard`.

## CI and automated releases

GitHub Actions workflows live in `.github/workflows/`:

- **CI** (`ci.yml`): every push to `main` and every pull request builds the app in Release mode on Windows and macOS runners.
- **Release** (`release.yml`): pushing a version tag builds self-contained single-file binaries for `win-x64`, `osx-x64` and `osx-arm64`. It zips them and publishes a GitHub Release with auto-generated notes and the three zips attached. The tag, minus the `v`, becomes the assembly version.

To cut a release:

```
git tag v1.2.0
git push origin v1.2.0
```

The pull request template in `.github/PULL_REQUEST_TEMPLATE.md` pre-fills every new PR with a summary, test and checklist structure.

## Where your data lives

- **Location:** settings, scripts, timers, notes and the work log are stored in:
  - `%APPDATA%\ScriptAppDashboard` on Windows,
  - `~/Library/Application Support/ScriptAppDashboard` on macOS.

  Settings → General shows the folder and has an **Open folder** button.
- **Files in that folder:**
  - `config.json`: settings, scripts and timers.
  - `notes.json`: notes.
  - `worklog/yyyy-MM-dd.jsonl`: the work log, one JSON object per line.
- **Using another folder:** set the `SCRIPTAPPDASHBOARD_DATA` environment variable to keep the data elsewhere, for a portable install or a demo.

## Notes

- Interpreters are found on `PATH` (`python`, `node`, `bash`, …). If a script fails to launch, set an explicit interpreter or a custom command in its Edit dialog.
- The process scan uses `Get-CimInstance Win32_Process` on Windows and `ps -axo pid,args` on macOS. It doesn't need elevated rights for your own processes.
- Window-title logging uses Win32 APIs, so the work log records foreground windows on Windows only. The rest of the app works on both platforms.

## Author

**Y Nguyen** ([@leybme](https://github.com/leybme))
