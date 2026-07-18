# Script App Dashboard

A small cross-platform (Windows / macOS) desktop launcher for all your Python tools, shell scripts and utilities — so you never have to hunt for the right folder again. Built with .NET 8 and Avalonia UI.

## Features

- **Tile dashboard** — every script is a clickable tile. Click to run, right-click to Run / Edit / Open Containing Folder / Remove.
- **Custom icon, path & parameters** — each tile has a name, script path, command-line parameters, working directory and an optional custom icon (.png / .jpg / .ico). Without an icon, a colored initials tile is generated.
- **Interpreter support** — auto-detects from the extension (`.py`, `.ps1`, `.sh`, `.js`, `.bat`/`.cmd`, `.exe`), or pick one explicitly, including a *custom command* (e.g. a venv's `python.exe -u`).
- **Scan running scripts** — finds script processes already running (python, node, powershell, bash, …) and lets you add them to the dashboard with one click, parameters included.
- **Hide to tray** — closing the window hides the app to the system tray / menu bar. The tray menu also lists your scripts for instant launching without opening the window.
- **Start with system** — one checkbox registers auto-start (Windows: `HKCU\...\Run` registry key; macOS: `~/Library/LaunchAgents` plist). It starts hidden in the tray (`--minimized`).
- **Keep console open** — optional per script: wraps the run in `cmd /k` (Windows) or a Terminal `.command` (macOS) so you can read the output after the script finishes.

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

The output lands in `bin/Release/net8.0/<rid>/publish/`. Tip: enable "Start with system" **after** moving the app to its final location, since the registered path is the running executable's path.

## Notes

- Config is stored at `%APPDATA%\ScriptAppDashboard\config.json` (Windows) or `~/Library/Application Support/ScriptAppDashboard/config.json` (macOS).
- Interpreters are resolved from `PATH` (`python`, `node`, `bash`, …). If a script fails to launch, set an explicit interpreter or a custom command in its Edit dialog.
- The process scan uses `Get-CimInstance Win32_Process` on Windows and `ps -axo pid,args` on macOS — no elevated rights needed for your own processes.
