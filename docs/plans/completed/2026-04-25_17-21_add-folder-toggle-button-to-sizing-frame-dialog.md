# Add Folder toggle button to sizing-frame dialog

## Context

Today the sizing-frame dialog always shows three rows: Size, Folder, Filename — with a Compact toggle that hides Size + Folder rows when the user wants minimal screen real estate. There is no way to keep the Size editing controls visible while hiding the file-saving widgets, even though many capture flows (especially when a default folder is already set in config) don't need the user to touch folder/filename at all.

This change adds a **Folder toggle button** to the dialog that lets the user collapse the file-saving widgets while keeping Size editing visible. As part of the change, the dialog stops thinking of itself as "expanded vs. compact" (a boolean) and is reframed as **three discrete dialog modes**. A new tray submenu lets the user pick which mode the dialog opens in each time the frame is shown.

## Dialog modes

The `_isCompact` flag is removed. The dialog has a single mode field (`DialogMode`) with three values:

| Mode               | Rows shown                  | Layout                                                                                       |
|--------------------|-----------------------------|----------------------------------------------------------------------------------------------|
| **Regular**        | Size, Folder, Filename      | `[Size][W×H][Folder*][ToFilenameOnly]` / `[Folder][folder…][Browse]` / `[Filename][filename…][Screenshot]` |
| **FilenameOnly**   | Filename                    | `[Filename][filename…][Screenshot][Folder*][ToRegular]`                                      |
| **DimensionsOnly** | Size                        | `[Size][W×H][Folder ][Screenshot]`                                                           |

`Folder*` = Folder toggle in pressed state; `Folder ` = un-pressed.

**Mode transitions** (only via UI; no implicit changes):
- Folder toggle clicked while in **Regular** or **FilenameOnly** → **DimensionsOnly**.
- Folder toggle clicked while in **DimensionsOnly** → **Regular** (always).
- `ToFilenameOnly` button clicked (visible only in Regular) → **FilenameOnly**.
- `ToRegular` button clicked (visible only in FilenameOnly) → **Regular**.

## Tray "Folder mode" submenu

New radio submenu in the tray (built in `TrayApplicationContext` next to `BuildClipboardModeMenu` / `BuildCrosshairModeMenu` at `src/TrayApplicationContext.cs:142-188`):

- **Default folder** — on each activation, open the dialog in **DimensionsOnly**. Use this when you've configured a default folder and don't need to edit it.
- **Specify destination** — on each activation, open in **Regular**.
- **Last used** — on each activation, open in whichever mode the dialog was in when last hidden.

Per user clarification: the menu choice is applied each time the frame is shown via `Show frame` or `Area select` (and the area-select hand-off `ShowAtRect`). The Folder toggle and the `ToFilenameOnly` / `ToRegular` buttons all change the in-session mode without affecting the menu value.

## Persistence model

In `state.json`:
- `folderActivationMode` (enum, default `DefaultFolder`).
- `lastDialogMode` (enum, default `Regular`) — written on `Hide`, read by the **Last used** activation policy.

No boolean compact flag anywhere in state, code, or docs.

## Files to modify

### 1. `src/Features/SizingFrame/FrameState.cs`

Add two enums:

```csharp
public enum DialogMode
{
    Regular,
    FilenameOnly,
    DimensionsOnly
}

public enum FolderActivationMode
{
    DefaultFolder,
    SpecifyDestination,
    LastUsed
}
```

Add two persisted properties to `FrameState` following the existing change-detect-and-`Save()` setter pattern (see `Width` at `FrameState.cs:43-47`):
- `FolderActivationMode FolderActivationMode { get; set; }` — default `DefaultFolder`.
- `DialogMode LastDialogMode { get; set; }` — default `Regular`.

Add corresponding `[JsonPropertyName]` fields to `FrameStateData` at `FrameState.cs:145-158`.

### 2. `src/Features/SizingFrame/SizingDialogWindow.xaml`

Restructure the grid to **5 columns** so icon buttons have dedicated slots (Compact mode now hosts an additional Folder toggle):

```
Col 0: 70                  (labels)
Col 1: Auto                (input fields)
Col 2: Auto                (icon slot A)
Col 3: Auto                (icon slot B)
Col 4: Auto                (icon slot C)
```

Element layout (visibility column lists modes the element appears in):

| Row | Col | Element                | Visible in                | Notes                                                            |
|-----|-----|------------------------|---------------------------|------------------------------------------------------------------|
| 0   | 0   | `SizeLabel`            | Regular, DimensionsOnly   |                                                                  |
| 0   | 1   | `SizeFields`           | Regular, DimensionsOnly   |                                                                  |
| 0   | 2   | `FolderToggleRow0`     | Regular, DimensionsOnly   | **new** `ToggleButton`, Segoe MDL2 `&#xE188;` (folder)           |
| 0   | 3   | `ToFilenameOnlyButton` | Regular only              | renamed from `ExpandedToggleButton`; same `&#xE70E;` glyph       |
| 0   | 4   | `ScreenshotButtonRow0` | DimensionsOnly only       | **new** primary-blue button, `&#xE114;`, fires `ScreenshotRequested` |
| 1   | 0   | `FolderLabel`          | Regular only              |                                                                  |
| 1   | 1   | `FolderBox`            | Regular only              |                                                                  |
| 1   | 2   | `BrowseButton`         | Regular only              |                                                                  |
| 2   | 0   | `FilenameLabel`        | Regular, FilenameOnly     |                                                                  |
| 2   | 1   | `FilenameBox`          | Regular, FilenameOnly     |                                                                  |
| 2   | 2   | `ScreenshotButton`     | Regular, FilenameOnly     | unchanged                                                        |
| 2   | 3   | `FolderToggleRow2`     | FilenameOnly only         | **new** second instance of Folder toggle                         |
| 2   | 4   | `ToRegularButton`      | FilenameOnly only         | renamed from `CompactToggleButton`; same `&#xE70D;` glyph; moved from Col 3 to Col 4 |

Two `FolderToggle*` instances follow the same twin-button pattern as today's `ExpandedToggleButton` / `CompactToggleButton` at `SizingDialogWindow.xaml:128-136, 156-165`. Both wire to one handler in code-behind so they always reflect the same logical state.

**New `ToggleIconButton` style** (added next to `GhostButton` at `SizingDialogWindow.xaml:81-105`):
- Same template skeleton as `GhostButton` (transparent background, hover `#E8E8E8`, pressed `#D8D8D8`).
- Adds an `IsChecked=True` trigger that sets background to `#C8C8C8` and `BorderBrush #A0A0A0` (1px) so the pressed state reads as inset.

**`FilenameBox.Width` adjustment**: `FilenameOnly` now hosts an extra 32px toggle + 6px margin in Row 2. Drop the constant currently used in compact mode (`FilenameBoxCompactWidth = 154` at `SizingDialogWindow.xaml.cs:30`) to ~`120` so the dialog's overall width in `FilenameOnly` stays close to today's footprint. Rename the constant pair to `FilenameBoxRegularWidth` / `FilenameBoxFilenameOnlyWidth`.

### 3. `src/Features/SizingFrame/SizingDialogWindow.xaml.cs`

Drop `_isCompact`, `ToggleCompactMode`, `SetCompactMode`, `ApplyCompactVisuals`, and the `CompactModeChanged` event. Replace with:

```csharp
private DialogMode _mode = DialogMode.Regular;

public DialogMode Mode => _mode;
public event EventHandler? ModeChanged;

public void SetMode(DialogMode mode)
{
    if (_mode == mode) return;
    ApplyLayout(mode);
}

private void OnFolderToggleClicked()
{
    var target = _mode == DialogMode.DimensionsOnly ? DialogMode.Regular : DialogMode.DimensionsOnly;
    ApplyLayout(target);
    ModeChanged?.Invoke(this, EventArgs.Empty);
}

private void OnToFilenameOnlyClicked() { ApplyLayout(DialogMode.FilenameOnly); ModeChanged?.Invoke(this, EventArgs.Empty); }
private void OnToRegularClicked()      { ApplyLayout(DialogMode.Regular);      ModeChanged?.Invoke(this, EventArgs.Empty); }
```

`ApplyLayout(DialogMode mode)` sets `_mode`, sets the visibility of every element from the table above based on the target mode, syncs `IsChecked` on both `FolderToggleRow0` / `FolderToggleRow2` instances, and adjusts `FilenameBox.Width`.

Wire button events in the constructor (replaces the current lines at `SizingDialogWindow.xaml.cs:66-67`):
```csharp
FolderToggleRow0.Click += (_, _) => OnFolderToggleClicked();
FolderToggleRow2.Click += (_, _) => OnFolderToggleClicked();
ToFilenameOnlyButton.Click += (_, _) => OnToFilenameOnlyClicked();
ToRegularButton.Click += (_, _) => OnToRegularClicked();
ScreenshotButtonRow0.Click += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);
```

Update the double-click drag handler at `SizingDialogWindow.xaml.cs:75-92`: today's double-click toggles compact. Re-wire it to cycle Regular ↔ FilenameOnly so existing muscle memory still works (skip if currently in DimensionsOnly).

### 4. `src/Features/SizingFrame/SizingFrameFeature.cs`

**Add** the activation-mode property mirroring the `ClipboardMode` pattern at `SizingFrameFeature.cs:61-65`:

```csharp
public FolderActivationMode FolderActivationMode
{
    get => _state.FolderActivationMode;
    set => _state.FolderActivationMode = value;
}
```

**Replace** `OnCompactModeChanged` (currently at `SizingFrameFeature.cs:394-402`) with `OnModeChanged` — same body (invalidate cached dialog dimensions, re-sync frame). Subscribe `_dialog.ModeChanged += OnModeChanged` in `EnsureWindows` (replacing the line at `SizingFrameFeature.cs:209`).

**Apply the activation policy** in `ShowWindowsAtCurrentState` at `SizingFrameFeature.cs:141-154`, before `_dialog.Show()`:

```csharp
var initialMode = _state.FolderActivationMode switch
{
    FolderActivationMode.DefaultFolder      => DialogMode.DimensionsOnly,
    FolderActivationMode.SpecifyDestination => DialogMode.Regular,
    FolderActivationMode.LastUsed           => _state.LastDialogMode,
    _                                       => DialogMode.Regular,
};
_dialog!.SetMode(initialMode);
```

**Persist mode in `Hide`** at `SizingFrameFeature.cs:156-166`:

```csharp
if (_dialog != null) _state.LastDialogMode = _dialog.Mode;
```

### 5. `src/TrayApplicationContext.cs`

Add `BuildFolderActivationModeMenu()` following the patterns at lines 142–188. Three sub-items: "Default folder", "Specify destination", "Last used", each setting `_frameFeature.FolderActivationMode` and calling `Refresh()` to update checkmarks. Insert the new submenu in the tray menu construction at `TrayApplicationContext.cs:121-135`, between `crosshairItem` and the separator.

## Critical files

- `src/Features/SizingFrame/SizingDialogWindow.xaml` — grid restructure, new toggle/screenshot buttons, new `ToggleIconButton` style, button renames
- `src/Features/SizingFrame/SizingDialogWindow.xaml.cs` — drop `_isCompact`, introduce `DialogMode`, rewrite layout application
- `src/Features/SizingFrame/SizingFrameFeature.cs` — activation policy in `ShowWindowsAtCurrentState`, persist on `Hide`, rename `OnCompactModeChanged` → `OnModeChanged`
- `src/Features/SizingFrame/FrameState.cs` — two new enums + two persisted fields
- `src/TrayApplicationContext.cs` — new tray submenu

## Reused infrastructure

- Twin-instance toggle pattern: `ExpandedToggleButton` / `CompactToggleButton` at `SizingDialogWindow.xaml:128-136, 156-165`
- Tray radio submenu pattern: `BuildClipboardModeMenu` (`TrayApplicationContext.cs:142-164`) and `BuildCrosshairModeMenu` (`TrayApplicationContext.cs:166-188`)
- State persistence pattern: `FrameState` setters at `FrameState.cs:43-97` with `Save()` on change
- Geometry re-sync after layout change: existing `OnCompactModeChanged` body at `SizingFrameFeature.cs:394-402` is reused as `OnModeChanged`
- Dialog re-measurement: `MeasureDialogFresh` at `SizingFrameFeature.cs:323-332` already handles re-measure after `Visibility=Collapsed` children change

## Verification

Build and deploy via the existing flow:

```
dotnet build src/CropStage.csproj -c Release
```

Manual checks (each starts from a deleted `state.json` so defaults are exercised):

1. **Default startup**: launch app, press frame hotkey. With default `FolderActivationMode = DefaultFolder`, the dialog opens in **DimensionsOnly**. Click `ScreenshotButtonRow0` — file saves to the configured default folder.
2. **DimensionsOnly → Regular**: click the (un-pressed) Folder toggle. Dialog grows to **Regular** (Folder + Filename rows visible, Folder toggle pressed, `ToFilenameOnly` icon visible).
3. **Regular → FilenameOnly**: click `ToFilenameOnly` (the `&#xE70E;` icon). Dialog collapses to **FilenameOnly**. Verify the Folder toggle (still pressed) appears between Screenshot and `ToRegular`.
4. **FilenameOnly → DimensionsOnly → Regular** (Folder-toggle path always returns to Regular): click Folder toggle in FilenameOnly — switches to DimensionsOnly. Click Folder toggle again — switches to Regular (not back to FilenameOnly).
5. **FilenameOnly → Regular**: from FilenameOnly, click `ToRegular` (`&#xE70D;`). Dialog returns to **Regular**.
6. **Tray menu — Specify destination**: open tray → Folder mode → "Specify destination". Hide and re-show frame — opens in **Regular**.
7. **Tray menu — Last used**: switch menu to "Last used". Put dialog in `FilenameOnly`, hide, re-show — opens in **FilenameOnly**. Put in `DimensionsOnly`, hide, re-show — opens in **DimensionsOnly**. Close and relaunch app — `state.json` should persist `folderActivationMode` and `lastDialogMode`.
8. **Area-select handoff**: trigger `Area select` from tray. After dragging a region, the sizing dialog appears — verify the activation policy applies.
9. **Frame geometry**: in each mode, drag the dialog around. Frame stays flush with the dialog regardless of layout (re-measurement after `ModeChanged` makes this work).
10. **Multi-monitor / DPI**: drag the dialog to a different-DPI monitor and switch modes. Geometry stays accurate.
11. **Double-click drag area**: double-click on a label/border — should toggle Regular ↔ FilenameOnly (no-op in DimensionsOnly).
