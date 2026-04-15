# Grid Overlay — Spec for Next Feature

Ported from the Python + AHK handoff notes. The sizing-frame + screenshot feature is already implemented (see `src/Features/SizingFrame/`); this doc covers the coordinate-grid overlay feature that will land next.

---

## Purpose

A screen overlay that draws a labeled coordinate grid across all monitors, letting you read pixel positions directly off the screen. Toggleable, click-through, doesn't touch wallpaper state.

## Feature Catalog

- **Per-monitor rendering**: each monitor gets its own grid sized to its exact resolution (no scaling, 1:1 pixel mapping). Critical — a single grid spanning the virtual screen and tiled misaligns between monitors of different sizes.
- **Two color variants**:
  - `light` — light-colored lines for dark wallpapers (default)
  - `dark` — dark-colored lines for light wallpapers
- **Cycle hotkey** (`Ctrl+Shift+9`, configurable): `hidden → dark → light → hidden`. Four-state cycle, one hotkey.
- **Click-through**: mouse events pass through the overlay.
- **Always on top**: stays above other windows but below true topmost UI (tooltips, some overlays).
- **Non-destructive**: doesn't touch wallpaper state. Spotlight/Slideshow/Picture modes keep running underneath.

## Grid Rendering Parameters

Four line tiers plus labels:

| Tier  | Default interval | Default line weight | Purpose                 |
|-------|------------------|---------------------|-------------------------|
| minor | 20 px            | 1 px                | Subtle reference        |
| major | 100 px           | 1 px                | Main readable gridlines |
| super | 500 px           | 2 px                | Extra-visible at distance |
| axis  | (x=0, y=0)       | 2 px                | Screen origin markers   |

**Labels**: numeric coordinates at every major-line intersection along the top row (`100`, `200`, …) and left column. Arial 18pt. 1px shadow offset for readability on variable backgrounds.

## Color Palettes (reference)

**Light variant** (for dark wallpapers) — blue-gray family:
- Minor: `[44, 56, 78]` — barely visible
- Major: `[92, 121, 170]` — main readable tier
- Super: `[139, 168, 206]` — stands out at 500-pixel intervals
- Axis: `[160, 198, 255]`
- Text: `[220, 235, 255]` with black shadow

**Dark variant** (for light wallpapers):
- Minor: `[170, 180, 200]`
- Major: `[90, 110, 145]`
- Super: `[45, 70, 110]`
- Axis: `[20, 50, 105]`
- Text: `[25, 35, 60]` with near-white shadow

The minor/major/super pattern: minor is the **most subtle** (least contrast), super is the **most contrast**. Major intervals punch through the minor lines for easy scanning.

## Config Additions (proposed)

Extend `config.json`:

```json
{
  "gridCycleShortcut": "Ctrl+Shift+9",
  "grid": {
    "minorStep": 20,
    "majorStep": 100,
    "superStep": 500,
    "fontName": "Arial",
    "fontSize": 18,
    "variants": {
      "light": {
        "minor": "#2C384E",
        "major": "#5C79AA",
        "super": "#8BA8CE",
        "axis":  "#A0C6FF",
        "text":  "#DCEBFF",
        "textShadow": "#000000"
      },
      "dark": {
        "minor": "#AAB4C8",
        "major": "#5A6E91",
        "super": "#2D466E",
        "axis":  "#143269",
        "text":  "#19233C",
        "textShadow": "#F5F5FF"
      }
    }
  }
}
```

## Target Architecture

### Real transparency, drop chroma-keying
WPF `Window` with `AllowsTransparency="True"`, `Background="Transparent"`, `WindowStyle="None"` gives per-pixel alpha natively. Draw grid lines directly on a `DrawingVisual` with proper alpha — no intermediate PNG needed.

### One window per monitor
Create one `GridOverlayWindow` per monitor at app startup, positioned by `Screen.AllScreens` bounds. Toggle visibility via `Show()`/`Hide()`. Needs DPI normalization (see `AppUtilities.GetPrimaryDpiScale()`).

### Grid cycle UX
Three-state toggle (`hidden → dark → light → hidden`) with a single hotkey is surprisingly natural. Don't over-engineer with a "pick variant" menu — one hotkey cycles.

### Click-through
- Set `IsHitTestVisible="False"` on the Window
- Also set `WS_EX_TRANSPARENT` via P/Invoke on the handle after `SourceInitialized` for true input transparency (same pattern as `SizingFrameWindow.xaml.cs`)

## Suggested Layout

```
src/Features/GridOverlay/
├── GridOverlayFeature.cs     — coordinator, cycle state, per-monitor windows
├── GridOverlayWindow.xaml(.cs) — per-monitor transparent window, click-through
├── GridRenderer.cs            — draws grid via DrawingVisual / DrawingContext
└── GridConfig.cs              — parses grid section of config.json
```

## Hard-Won Lessons

### Don't touch wallpaper state
Early Python versions tried to swap the wallpaper itself (set Spotlight image as "previous", switch to a grid image, restore on toggle). Multiple traps:

- `BackgroundType=3` (Spotlight indicator) isn't reliably written by Windows 11
- `IDesktopWallpaper::SetWallpaper` per-monitor breaks Spotlight in a way that's hard to undo programmatically
- Writing registry flags + broadcasting `WM_SETTINGCHANGE` does NOT reliably restart the Content Delivery Manager state machine

**Takeaway**: for a display overlay, use an actual overlay window. Never modify wallpaper state.

### Cross-monitor tile mode is useless for coordinate work
Tiling from virtual-screen `(0,0)` on multi-monitor setups with different-sized monitors bleeds across monitor boundaries. Hence per-monitor rendering with one grid per monitor resolution.

### DPI scaling
- Grid must be in physical pixels, not DIPs
- Use actual device pixel bounds via `Screen.AllScreens[i].Bounds` rather than `SystemParameters` (which uses primary monitor's DPI as coordinate basis)

## Hotkeys (final layout)

| Hotkey         | Action                                              | Status      |
|----------------|-----------------------------------------------------|-------------|
| `Ctrl+Shift+0` | Toggle sizing frame + dialog                       | ✅ implemented |
| `Ctrl+Shift+9` | Cycle grid overlay: hidden → dark → light → hidden | planned     |

## Acceptance Criteria

1. `Ctrl+Shift+9` cycles grid overlay across **all** monitors (not just primary), correctly sized and aligned to each monitor's pixel grid.
2. Grid overlay has **real transparency** (no chroma-key artifacts).
3. Grid variants (light/dark) configurable via `config.json`.
4. App doesn't interfere with wallpaper, Spotlight, or any existing Windows personalization state.

## Reference (old implementation, removed)

The original Python + AutoHotkey v2 implementation lived at `D:/projects/toolbox/tools/overlay_grid/`. Removed after the port. Git history of `D:/projects/toolbox` preserves it if needed.

Key files in the old implementation:
- `src/grid.py` — grid rendering (dataclasses `GridSpec`, `GridColors`; function `render_grid`)
- `src/main.py` — CLI: `--width`, `--height`, `--background`, `--variant`
- `scripts/overlay.ahk` — hotkey/UI/screenshot logic (behavior spec for hotkey and UX choices)
- `config/config.json` — reference config
