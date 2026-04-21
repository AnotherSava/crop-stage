# Config submenu + tray menu sentence case + guideline update

## Context

Three grouped changes:

1. **Config submenu** — replace the single tray item "Open Config Location" with a "Config" submenu containing "Open config file", "Open config location", and "Reload config". Reload re-reads `config.json` without an app restart (the user previously asked whether config was cached — this makes live edits possible).
2. **Sentence case pass on the tray menu** — the project inconsistently mixes Title Case ("Show Frame") with sentence case ("Hide with Esc"). The user has decided on sentence case as the convention. Convert every tray menu label accordingly, preserving proper nouns and acronyms.
3. **Guideline update** — persist the UI-text convention so future work (in this project and others) defaults to sentence case without re-litigating the decision.

## Files to modify

- `src/AppConfig.cs` — add `Reload()`.
- `src/Features/SizingFrame/SizingFrameFeature.cs` — add `HideForReload()` so a reload refreshes the frame border color/thickness on next show.
- `src/TrayApplicationContext.cs` — Config submenu, reload coordinator, all menu labels to sentence case.
- `~/.claude/CLAUDE.md` — add a UI text style rule under "Code Style".
- `~/.claude/memory/feedback_sentence_case_ui.md` — new feedback memory.
- `~/.claude/memory/MEMORY.md` — index the new memory.

No changes to `config.json`, `README.md` (hotkey/config tables don't reference menu labels), `GlobalHotkey`, or `SizingFrameBorder`.

## Part A — Config submenu and reload

### A1. `src/AppConfig.cs` — add `Reload()`

```csharp
public void Reload()
{
    _settings = Load(_settingsFilePath);
}
```

`Load()` already throws on validation failure — caller handles. Because every consumer reads `_config.X` via property getters at use time (verified), a single atomic swap propagates the new values to every holder.

### A2. `src/Features/SizingFrame/SizingFrameFeature.cs` — add `HideForReload()`

`SizingFrameBorder` captures color/thickness into private fields at construction (`_brush`, `_thicknessPx`), so a config change to those values won't affect a live window. `SizingDialogWindow` similarly caches the screenshot shortcut label. Dropping both on reload forces `EnsureWindows()` to rebuild them with current config values on the next show.

```csharp
/// <summary>
/// Hides the frame and drops cached windows so the next Show() rebuilds them
/// with current config values (border color/thickness, dialog shortcut label).
/// Used by the tray's Reload Config action.
/// </summary>
public void HideForReload()
{
    if (_visible) Hide();
    _dialog?.Close();
    _dialog = null;
    _frame?.Close();
    _frame = null;
}
```

If the frame wasn't visible at reload, this is a no-op beyond clearing cached windows.

### A3. `src/TrayApplicationContext.cs` — submenu + reload coordinator

**Promote locals to fields** so reload can re-register/refresh them:
- Remove `readonly` from `_frameHotkey` (currently line 17) so it can be reassigned.
- Add fields `_toggleFrameItem`, `_areaSelectItem`, `_quickSaveAreaSelectItem` (for shortcut-label refresh).

**Extract hotkey registration** — move the existing inline block (lines 65–86) into a private `ReregisterHotkeys()` method that disposes existing hotkeys before recreating them. Call from the constructor and from `ReloadConfig()`. Logic copied verbatim from current lines 65–86.

**Config submenu builder**:

```csharp
private ToolStripMenuItem BuildConfigMenu()
{
    var parent = new ToolStripMenuItem("Config");

    var openFileItem = new ToolStripMenuItem("Open config file");
    openFileItem.Click += (_, _) => OpenConfigFile();

    var openLocationItem = new ToolStripMenuItem("Open config location");
    openLocationItem.Click += (_, _) => OpenConfigLocation();

    var reloadItem = new ToolStripMenuItem("Reload config");
    reloadItem.Click += (_, _) => ReloadConfig();

    parent.DropDownItems.AddRange(new ToolStripItem[] { openFileItem, openLocationItem, reloadItem });
    return parent;
}
```

Replace the inline `openConfigItem` block (current lines 132–140) and its entry in the `ContextMenuStrip.Items.AddRange` call (current line ~160) with `BuildConfigMenu()`.

**Three action methods**:

```csharp
private static void OpenConfigFile()
{
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    if (!File.Exists(settingsPath)) return;
    try { Process.Start(new ProcessStartInfo(settingsPath) { UseShellExecute = true }); }
    catch (Exception ex) { Logger.Warn($"Could not open config file: {ex.Message}"); }
}

private static void OpenConfigLocation()
{
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    if (File.Exists(settingsPath))
        Process.Start("explorer.exe", $"/select,\"{settingsPath}\"");
    else
        Process.Start("explorer.exe", AppContext.BaseDirectory);
}

private void ReloadConfig()
{
    try
    {
        _config.Reload();
    }
    catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
    {
        Logger.Error($"Reload failed: {ex.Message}");
        _trayIcon.ShowBalloonTip(5000, "Crop Stage — reload failed", ex.Message, ToolTipIcon.Error);
        return;
    }

    Logger.Info("Config reloaded");
    ReregisterHotkeys();
    RefreshMenuShortcutLabels();
    _frameFeature.HideForReload();
    _trayIcon.ShowBalloonTip(2000, "Crop Stage", "Config reloaded", ToolTipIcon.Info);
}

private void RefreshMenuShortcutLabels()
{
    _toggleFrameItem.ShortcutKeyDisplayString = _frameHotkey?.IsRegistered == true ? _config.FrameToggleShortcut : "";
    _areaSelectItem.ShortcutKeyDisplayString = _areaSelectHotkey?.IsRegistered == true ? _config.AreaSelectShortcut : "";
    _quickSaveAreaSelectItem.ShortcutKeyDisplayString = _quickSaveAreaSelectHotkey?.IsRegistered == true ? _config.QuickSaveAreaSelectShortcut : "";
}
```

On validation failure, old `_settings` remain in place (the throw happens before the assignment in `Load()`), so hotkeys/colors are unchanged and the error balloon guides the user.

If the frame was visible at reload: it vanishes. User re-opens with `Ctrl+Shift+0` or area-select. Trade-off: avoids the complexity of live-updating a rendered WPF brush. Reload is rare enough that this is acceptable.

## Part B — Tray menu sentence case

### Convention

**Sentence case**: capitalize only the first word of the label. Keep proper nouns ("Windows") and abbreviations/key names ("Esc") capitalized. Hyphenated compounds lowercase the second element ("Quick-save", not "Quick-Save").

### Labels to change in `src/TrayApplicationContext.cs`

| Line | Before | After |
|------|--------|-------|
| 93   | `"Show Frame"` | `"Show frame"` |
| 98   | `"Area Select"` | `"Area select"` |
| 103  | `"Quick-save Area Select"` | `"Quick-save area select"` |
| 115  | `"Drag to Resize"` | `"Drag to resize"` |
| 152  | `"Hide Frame" : "Show Frame"` (dynamic toggle) | `"Hide frame" : "Show frame"` |
| 176  | `"Copy to Clipboard"` (submenu parent) | `"Copy to clipboard"` |
| 200  | `"Area Select Crosshair"` (submenu parent) | `"Area select crosshair"` |

### Labels left unchanged (already sentence case or proper-noun/acronym rule applies)

- `"Hide with Esc"` — Esc is a key abbreviation, stays capitalized.
- `"Start with Windows"` — Windows is a proper noun.
- `"Image"`, `"Path"`, `"Nothing"` — single words.
- `"None"`, `"1st point"`, `"Both points"` — already sentence case.
- `"Exit"` — single word.

### New Config submenu (Part A) — sentence case from the start

`"Config"`, `"Open config file"`, `"Open config location"`, `"Reload config"`.

## Part C — Persist the convention

### C1. New feedback memory: `~/.claude/memory/feedback_sentence_case_ui.md`

```markdown
---
name: UI text in sentence case
description: Default to sentence case for all user-facing UI labels (menu items, buttons, dialog titles, tooltips); reserve Title Case for proper nouns and acronyms only.
type: feedback
---

Use sentence case for user-facing UI text. Capitalize only the first word and proper nouns/acronyms.

**Examples**
- "Open config file" not "Open Config File"
- "Reload config" not "Reload Config"
- "Hide with Esc" — Esc is an acronym, keep capitalized
- "Start with Windows" — Windows is a proper noun
- "Quick-save area select" — hyphenated compounds lowercase the second element

**Why:** User prefers modern UX convention used by macOS (since Big Sur), Material Design, GitHub, and most contemporary web apps. Less visually shouty, easier to localize, and more consistent with surrounding prose. User made this decision explicitly and asked that future work default to it.

**How to apply:** Any time you write or edit a user-visible string — tray menus, buttons, dialog titles, balloon notifications, tooltips, settings labels — default to sentence case. When modifying a codebase that mixes conventions, realign affected labels in the same change. Only keep Title Case when required for proper nouns (product names, OS names) or established acronyms (Esc, URL, API).
```

### C2. Index in `~/.claude/memory/MEMORY.md`

Add under the existing list:
```
- [UI text sentence case](feedback_sentence_case_ui.md) — default to sentence case for user-facing UI labels
```

### C3. Global `~/.claude/CLAUDE.md`

Add under `## Code Style` a new subsection:

```markdown
### UI Text Casing

Default to sentence case for user-facing UI strings (menu items, buttons, dialog titles, tooltips, notifications). Capitalize only the first word and proper nouns/acronyms. Examples: "Open config file", "Hide with Esc", "Start with Windows". See `~/.claude/memory/feedback_sentence_case_ui.md` for rationale and edge cases.
```

Project-level `CLAUDE.md` does not need a duplicate note — the global rule is loaded for every project.

## Out of scope

- **Sizing dialog window text** (labels, buttons inside the WPF `SizingDialogWindow`) — not touched here. The user said "menu"; the dialog is separate UI. Can be tackled as a follow-up once this convention lands.
- **No file-watcher auto-reload** — explicit user action only.
- **No live recoloring of a visible frame** — frame is closed on reload; user re-opens.
- **No `state.json` reset** — user-persisted geometry/folder/filename survive reload by design.

## Verification

### Config submenu + reload

1. Build: `dotnet build src/CropStage.csproj -c Release`, deploy, launch.
2. Right-click tray → "Config" submenu exists with three sentence-case items; top-level "Open Config Location" is gone.
3. **Open config file** → `config.json` opens in the default `.json` handler.
4. **Open config location** → Explorer opens with the file selected (unchanged behavior).
5. **Reload — hotkey change**: edit `frameToggleShortcut` to `Ctrl+Shift+7`, Reload. Balloon "Config reloaded" appears. `Ctrl+Shift+0` no longer toggles, `Ctrl+Shift+7` does. "Show frame" menu label shows "Ctrl+Shift+7".
6. **Reload — border color**: with frame visible, change `frameBorderColor`, Reload. Frame vanishes (expected). `Ctrl+Shift+7` re-shows it with the new color.
7. **Reload — invalid JSON**: syntax-error the file, Reload → error balloon. Old hotkeys still work. Fix and Reload → success balloon.
8. **Reload — invalid value**: `frameBorderColor = "notahex"`, Reload → validation-error balloon; old settings intact.
9. **Reload — quick-save hotkey cleared**: `quickSaveAreaSelectShortcut = ""`, Reload. `Ctrl+Shift+8` stops working; menu item's shortcut label is blank.

### Sentence case

10. Open tray menu and all submenus. Every label reads in sentence case except "Hide with Esc" and "Start with Windows" (acronym + proper noun exceptions).
11. Toggle frame on, reopen menu → dynamic label reads "Hide frame" (not "Hide Frame").

### Guideline persistence

12. `~/.claude/memory/feedback_sentence_case_ui.md` exists with the above content; `MEMORY.md` links to it.
13. `~/.claude/CLAUDE.md` contains the new "UI Text Casing" subsection under "Code Style".
