# Crop Stage

Windows tray app with overlay utilities for pixel-precise layout work.

## Features

- **Sizing frame + screenshot** — draggable rectangle with configurable dimensions and one-click screenshot of its interior. Useful for cropping reference shots to exact sizes.
- **Coordinate grid overlay** *(planned)* — per-monitor labeled grid for reading pixel positions directly off the screen.

## Build

```
dotnet build src/CropStage.csproj -c Release
dotnet test tests/CropStage.Tests.csproj
```

## Hotkeys

| Hotkey | Action |
|--------|--------|
| `Ctrl+Shift+0` | Toggle sizing frame + dialog |
| `PrintScreen` | Take screenshot of frame interior |
| `Ctrl+Shift+9` | Click-drag to select an area; frame appears at the drawn rectangle |
| `Ctrl+Shift+8` | Click-drag to select an area and immediately save it to the quick-save folder with a timestamped filename (no dialog) |

Configurable in `config.json` next to the exe.

## Configuration

Edit `config.json` next to the exe. Available options:

| Key | Default | Description |
|-----|---------|-------------|
| `frameToggleShortcut` | `Ctrl+Shift+0` | Hotkey to toggle the sizing frame |
| `screenshotShortcut` | `PrintScreen` | Hotkey to capture the frame interior |
| `areaSelectShortcut` | `Ctrl+Shift+9` | Hotkey to enter click-drag area selection |
| `quickSaveAreaSelectShortcut` | `Ctrl+Shift+8` | Hotkey for quick-save area selection (captures immediately, no dialog) |
| `frameBorderColor` | `#FF0000` | Frame border color (hex) |
| `frameBorderThickness` | `2` | Frame border width in pixels |
| `defaultFrameWidth` | `1280` | Initial frame width in pixels |
| `defaultFrameHeight` | `800` | Initial frame height in pixels |
| `defaultScreenshotFolder` | `%USERPROFILE%\Pictures` | Default save folder (supports env vars) |
| `defaultScreenshotFilename` | `frame.png` | Default filename |
| `quickSaveFolder` | `%USERPROFILE%\Pictures` | Folder for quick-save captures; filenames are `YYYY-MM-DD_HH-MM-SS.png` (supports env vars) |

## License

MIT — see [LICENSE](LICENSE).
