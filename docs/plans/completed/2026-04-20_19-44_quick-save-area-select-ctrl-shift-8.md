# Quick-save Area Select (Ctrl+Shift+8)

## Context

Today, `Ctrl+Shift+9` opens the fullscreen area-select overlay; after the drag, the sizing frame + dialog appear at the selection rect so the user can fine-tune and click the screenshot button. For fast captures this extra dialog step is unnecessary.

Add a second, parallel mode: `Ctrl+Shift+8` runs the same drag-selection overlay, but on mouse-up it immediately captures the rect to PNG (no dialog shown), writes it to a separate user-configurable "quick-save" folder with a `YYYY-MM-DD_HH-MM-SS.png` filename, and updates the clipboard the same way the existing screenshot flow does. The original `Ctrl+Shift+9` behavior must remain unchanged.

## Files to modify

- `config/default.json` and `config/local.json` — add two new keys
- `src/AppConfig.cs` — expose the two new settings
- `src/Features/SizingFrame/SizingFrameFeature.cs` — add public `CaptureQuickSave(...)` method that reuses existing clipboard/validation helpers
- `src/Features/AreaSelect/AreaSelectFeature.cs` — add `StartQuickSave()` variant that routes `OnSelected` to the quick-save path
- `src/TrayApplicationContext.cs` — register hotkey ID 4 and dispose it

## Changes

### 1. Config (`config/default.json`, `config/local.json`)

Add two keys:

```json
"quickSaveAreaSelectShortcut": "Ctrl+Shift+8",
"quickSaveFolder": "%USERPROFILE%\\Pictures"
```

Both are optional — missing/empty means the hotkey is not registered.

### 2. `src/AppConfig.cs`

- Add `SettingsData.QuickSaveAreaSelectShortcut` and `SettingsData.QuickSaveFolder` (lines 121–149), default to `""`, with `[JsonPropertyName]` attributes matching the keys above.
- Add corresponding read-only wrappers on `AppConfig` (lines 35–43).
- No changes to `Validate()` — both settings are optional.
- Add them to the startup log line at `TrayApplicationContext.cs:53`.

### 3. `src/Features/SizingFrame/SizingFrameFeature.cs`

Add one new public method. Reuse the existing `IsRectFullyOnScreen`, `CopyToClipboard`, `ScreenshotCapture.Capture`, and `ScreenshotSaved` event — no refactor of existing flow.

```csharp
public void CaptureQuickSave(int leftPx, int topPx, int widthPx, int heightPx, string folder)
{
    if (string.IsNullOrWhiteSpace(folder))
    {
        Logger.Error("Quick-save folder is empty — cannot save");
        return;
    }
    if (!IsRectFullyOnScreen(leftPx, topPx, widthPx, heightPx))
    {
        Logger.Info("Quick-save skipped: rect is not fully on-screen");
        return;
    }

    var filename = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png";
    try
    {
        var saved = ScreenshotCapture.Capture(leftPx, topPx, widthPx, heightPx, folder, filename);
        Logger.Info($"Quick-save screenshot: '{saved}'");
        CopyToClipboard(saved);
        ScreenshotSaved?.Invoke(this, saved);
    }
    catch (Exception ex)
    {
        Logger.Error($"Quick-save screenshot failed: {ex.Message}");
    }
}
```

Key design points:
- No frame is shown (quick-save flow bypasses the UI entirely), so no `_frame.Flash()`.
- The `ScreenshotSaved` event still fires so the tray balloon confirms the save (same UX as `OnScreenshotSaved` at `TrayApplicationContext.cs:202`).
- Folder is passed in — does not touch `_state.Folder` so it can't pollute the dialog's remembered folder.
- Filename disambiguation already handled by `ScreenshotCapture.DisambiguatePath` (`ScreenshotCapture.cs:33–47`), so rapid repeated captures within the same second get `_1`, `_2` suffixes.
- Timestamp uses local time (matches user expectation; matches `_state.Filename` which is user-readable).

### 4. `src/Features/AreaSelect/AreaSelectFeature.cs`

Introduce a quick-save mode flag and a second `StartQuickSave()` entry point. The overlay drag UX is identical; only the post-selection routing changes.

- Add field `private bool _quickSave;` and a constructor-injected `_config.QuickSaveFolder` lookup at use time (not cached — keeps one source of truth in `AppConfig`).
- Add `public void StartQuickSave()` that mirrors `Start()` but sets `_quickSave = true` before showing the overlay. Extract shared body into a private `StartInternal(bool quickSave)` to avoid duplicating the Esc-hotkey + overlay setup (lines 33–63).
- In `OnSelected` (line 65): if `_quickSave`, call `_frameFeature.CaptureQuickSave(leftPx, topPx, widthPx, heightPx, _config.QuickSaveFolder)`; else call the existing `_frameFeature.ShowAtRect(...)`.
- Reset `_quickSave = false` in `Teardown()`.

### 5. `src/TrayApplicationContext.cs`

- Add field `private GlobalHotkey? _quickSaveAreaSelectHotkey;` next to the other hotkey fields (line 21–22).
- After the existing area-select hotkey block (lines 76–81), register hotkey ID 4:

```csharp
if (!string.IsNullOrWhiteSpace(_config.QuickSaveAreaSelectShortcut))
{
    _quickSaveAreaSelectHotkey = new GlobalHotkey(4, _config.QuickSaveAreaSelectShortcut, () => _areaSelectFeature.StartQuickSave());
    if (!_quickSaveAreaSelectHotkey.IsRegistered)
        Logger.Warn($"Could not register quick-save area-select hotkey '{_config.QuickSaveAreaSelectShortcut}' — may already be in use");
}
```

- Dispose it alongside `_areaSelectHotkey` (line 244).
- Extend the startup log line (line 53) to include the two new settings.

#### Tray menu entries

Add two new menu items directly below `toggleFrameItem` (line 85–88), following the same pattern:

```csharp
var areaSelectItem = new ToolStripMenuItem("Area Select");
if (_areaSelectHotkey?.IsRegistered == true)
    areaSelectItem.ShortcutKeyDisplayString = _config.AreaSelectShortcut;
areaSelectItem.Click += (_, _) => _areaSelectFeature.Start();

var quickSaveAreaSelectItem = new ToolStripMenuItem("Quick-save Area Select");
if (_quickSaveAreaSelectHotkey?.IsRegistered == true)
    quickSaveAreaSelectItem.ShortcutKeyDisplayString = _config.QuickSaveAreaSelectShortcut;
quickSaveAreaSelectItem.Click += (_, _) => _areaSelectFeature.StartQuickSave();
```

Insert both into the `ContextMenuStrip.Items.AddRange` call (lines 135–147) immediately after `toggleFrameItem`, before `hideWithEscItem`.

The existing `toggleFrameItem` text already toggles dynamically between "Show Frame" and "Hide Frame" based on `_frameFeature.IsVisible` via the `ContextMenuStrip.Opening` handler (line 134) — preserved as-is, used as the reference pattern here.

## Out of scope

- No new `state.json` entries — quick-save folder is a pure config setting, and the timestamp filename is always auto-generated.
- No changes to the existing `Ctrl+Shift+9` flow — it continues to show the dialog.

## Verification

1. Build: `dotnet build src/CropStage.csproj -c Release`
2. Deploy and launch.
3. **Mode 2 (new)**: press `Ctrl+Shift+8`, drag a rectangle on-screen, release.
   - A `YYYY-MM-DD_HH-MM-SS.png` file appears in the configured `quickSaveFolder`.
   - No sizing-frame dialog appears.
   - Clipboard contains the image (or path, or nothing, matching current `ClipboardMode`).
   - Tray balloon shows "Saved: <path>".
4. **Mode 2 rapid repeat**: trigger twice within the same second → second file gets `_1` suffix.
5. **Mode 2 edge cases**:
   - Press `Esc` mid-selection → overlay dismisses, no file saved (Esc hotkey path is shared with existing `Start()`).
   - Drag a rect that spills off a monitor → log records "rect is not fully on-screen", no file saved.
   - Leave `quickSaveAreaSelectShortcut` empty in config → hotkey is not registered, no warnings; `Ctrl+Shift+9` still works.
   - Leave `quickSaveFolder` empty but shortcut set → log records "Quick-save folder is empty", no crash.
6. **Regression — Mode 1**: press `Ctrl+Shift+9`, drag a rect → dialog + frame appear at rect as before, remembered folder/filename unchanged.
7. **Regression — screenshot button**: `PrintScreen` (or the dialog screenshot button) still writes to `_state.Folder` using `_state.Filename`.
8. **Tray menu**: right-click tray icon → "Area Select" and "Quick-save Area Select" entries appear below the dynamic Show/Hide Frame entry, each showing their bound shortcut, and clicking either triggers the corresponding flow.
