## Summary

<!-- What does this change do, and why? -->

## Changes

-

## How to test

1. `dotnet run`
2.

## Screenshots

<!-- For UI changes: before/after screenshots of the dashboard, dialogs, or tray menu -->

## Checklist

- [ ] `dotnet build -c Release` passes with no new warnings
- [ ] App launches; tiles render and scripts still launch
- [ ] Tray behavior verified (hide on close, tray menu entries, Quit)
- [ ] OS-specific code paths are guarded with `OperatingSystem.IsWindows()` / `IsMacOS()`
- [ ] Tested on macOS too if the change touches launching, startup, or the tray
- [ ] README updated if user-facing behavior changed
