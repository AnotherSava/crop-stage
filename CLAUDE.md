# CLAUDE.md

## What This Is

C# WinForms tray app with WPF overlay windows — Windows-only utilities for pixel-precise layout work. Flat-ish `src/` layout with per-feature subfolders under `src/Features/`.

Stack: .NET 10, WinForms tray + WPF overlay windows.

## Build & Test

```
dotnet build src/CropStage.csproj -c Release
dotnet test tests/CropStage.Tests.csproj
```

## Layout

```
src/
  CropStage.csproj
  Program.cs                  — entry point, single-instance mutex
  TrayApplicationContext.cs   — composition root, wires features together
  Logger.cs
  AppConfig.cs                — config.json next to exe; validated, throws on error
  AppUtilities.cs             — icon loading, DPI helpers
  GlobalHotkey.cs             — RegisterHotKey wrapper
  Assets/icon.ico
  Features/
    SizingFrame/              — draggable frame + screenshot feature
```

## Key Patterns

- **Config + state**: `config.json` (shipped, user-tweakable hotkeys/colors) and `state.json` (runtime per-user state — last-used frame position/dimensions, folder, filename, and tray preferences like "Hide with Esc" and "Drag to Resize") both live next to the exe.
- **Startup validation**: bad config → `TaskDialog` with expandable log → `Environment.Exit(1)`.
- **Feature folders**: each feature owns its windows, XAML, coordinator class. Shared infrastructure (AppConfig, Logger, GlobalHotkey) stays at `src/` root.
