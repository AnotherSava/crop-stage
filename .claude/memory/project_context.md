---
name: crop-stage project context
description: What this project is, what's shipped vs planned, and the Grammarly focus-lag gotcha discovered during development
type: project
---
This project (formerly `screen-tools`) is a .NET 10 WinForms tray + WPF overlay app that replaces the old Python + AutoHotkey `tools/overlay_grid/` in the toolbox repo. Stack and structure mirror the achievement-overlay repo.

**Shipped**: sizing frame + screenshot feature (Ctrl+Shift+0). Frameless dialog with Size/Folder/Filename fields + compact toggle + big Screenshot button. Dialog sits flush below the frame; dimension changes pivot at the bottom-left corner. Frame is rendered as 4 thin opaque topmost border windows (not a single per-pixel-transparent window — that was a perf trap; see dotnet-tray-app learnings). Screenshot captures without hiding (UI is outside the capture rect) and flashes the frame red→white→red via `ColorAnimation` for feedback. ESC to dismiss is wired via Win32 `RegisterHotKey` (not WPF `PreviewKeyDown` — that's intercepted by Grammarly; see learnings).

**Planned (next)**: coordinate grid overlay (Ctrl+Shift+9 cycles hidden→dark→light). Spec at `docs/plans/grid-overlay-spec.md`.

**Why:** The old Python+AHK version couldn't support features like drag-to-resize corner handles on the frame. .NET gives a proper path for richer UI.

**How to apply:** When working here, follow the feature-folder pattern (`src/Features/<Name>/`). Shared infrastructure (AppConfig, Logger, GlobalHotkey, TrayApplicationContext) lives at `src/` root. Config schema is in `config/default.json`; runtime state (frame dimensions, last folder/filename) goes to `state.json` next to the exe. Deploy via `bash scripts/deploy.sh` which installs to the directory configured in `config/deploy.env`.

**Burned-in gotcha:** Mysterious TextBox focus lag in the dialog on 2026-04-13 turned out to be **Grammarly** running in the background and subclassing edit controls globally. If the user reports focus lag again on this project (or any other .NET UI project), ask about Grammarly first — or similar writing assistants, IMEs, password managers that hook text input — before chasing WPF/WinForms internals.
