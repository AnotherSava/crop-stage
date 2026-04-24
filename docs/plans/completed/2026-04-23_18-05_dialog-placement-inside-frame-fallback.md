---
name: Dialog placement fallback to inside-frame compact
description: Add a third tier to the dialog placement cascade — when neither expanded nor compact dialog fits below the frame, place the compact dialog inside the frame at its interior bottom-left, instead of recentering the whole composite.
---

# Dialog placement: inside-frame fallback

## Context

Today the sizing dialog is always anchored just below the frame (`dialog.Top = frame.Outer.Bottom`, `dialog.Left = frame.Outer.Left`). `PlaceAtSavedPosition` cascades:

1. Try expanded dialog below → use it if it fits on screen.
2. Try compact dialog below → use it if it fits.
3. Else `PlaceDialogAndFrameCentered()` — re-centers the entire frame+dialog composite, which moves the user's frame.

The recenter step is jarring whenever the frame's bottom edge sits too close to the screen's bottom (or taskbar) to fit even a compact dialog beneath it — most often on area-select handoff that hugs the bottom of the screen, or on a deliberate downward drag/resize. We want to keep the frame where the user put it and instead show a compact dialog **inside** the frame, with its bottom-left anchored to the frame interior's bottom-left corner.

Re-evaluation must run after every event that can change the frame's footprint relative to the screen: initial show from saved state, area-select handoff, drag-to-move end, and drag-to-resize end.

## Approach

Extend the existing cascade in `PlaceAtSavedPosition` with a new tier (compact-inside) and ensure the cascade is invoked at drag-end and resize-end too. Centering remains only as the very last fallback (e.g. frame not on-screen at all).

### 1. New cascade tier in `PlaceAtSavedPosition`

`src/Features/SizingFrame/SizingFrameFeature.cs:269-307`

Add a parameter `bool allowExpand` (default `true`). When `false`, the expanded-outside tier is skipped and the cascade starts at compact-outside — used by move/resize re-evaluation so that a dialog that was already compact does not auto-promote back to expanded just because there's room. Initial show and area-select handoff keep the default (`true`).

Order, in priority:

1. **Expanded outside** — current behavior, gated by `allowExpand && !_dialog.IsCompact`'s prior state. Dialog rect = `(interiorLeft - border, interiorTop + interiorHeight + border, expandedW, expandedH)`. Use if `frameOnScreen && dialogOnScreen`.
   - For move/resize: only attempted if dialog was expanded before the operation (caller passes `allowExpand: wasExpandedBefore`).
   - For initial show / area-select handoff: always attempted (caller passes `allowExpand: true`).
2. **Compact outside** — current behavior. Same anchor, compact dimensions.
3. **Compact inside** *(new)* — Dialog rect = `(interiorLeft, interiorTop + interiorHeight - compactH, compactW, compactH)`. Use if `frameOnScreen` (no separate "fits inside" gate — see Notes below).
4. **Centered fallback** — only when `!frameOnScreen`. Existing `PlaceDialogAndFrameCentered()`.

Add a sibling helper to `PlaceAtPosition` (`SizingFrameFeature.cs:300-307`):

```
PlaceAtPositionInside(interiorLeftPx, interiorTopPx, dpi):
    var compactHpx = round(MeasureDialogFresh().H * dpi);  // already in compact
    var dialogLeftPx = interiorLeftPx;
    var dialogTopPx  = interiorTopPx + _state.Height - compactHpx;
    _dialog.Left = dialogLeftPx / dpi;
    _dialog.Top  = dialogTopPx  / dpi;
    _pendingDialogPhysicalLeft = dialogLeftPx;
    _pendingDialogPhysicalTop  = dialogTopPx;
    _hasPendingDialogPosition  = true;
    UpdateFrameGeometry(interiorLeftPx, interiorTopPx, _state.Width, _state.Height);
```

Both `PlaceAtPosition` and `PlaceAtPositionInside` write `_state.Left/Top` as the frame interior origin — unchanged contract for state persistence.

### 2. Re-run cascade after drag-to-move

Today, dialog drags fire `OnDialogLocationChanged` → `SyncFrameToDialog` (`SizingFrameFeature.cs:388`), which keeps the frame anchored to the dialog continuously. There is no per-drag-end signal currently used by the feature. After the user releases:

- Capture `wasExpanded = !_dialog.IsCompact` before any placement work.
- Compute the implied frame interior origin from the dialog's final position via the existing `GetInteriorOriginPx(_state.Height)` (`SizingFrameFeature.cs:429-436`).
- Save into `_state.Left/_state.Top`.
- Call `PlaceAtSavedPosition(_state.Left.Value, _state.Top.Value, allowExpand: wasExpanded)` — runs the cascade. If it was compact before, it stays compact (outside if it fits, else inside).
- Then `ShowWindowsAtCurrentState()` to apply `SetWindowPos` with corrected DPI.

Expose a public `IsCompact` getter on `SizingDialogWindow` (the field `_isCompact` already exists at `SizingDialogWindow.xaml.cs:18`).

Wire-up: add a `DragEnded` event on `SizingDialogWindow` (mirror of existing `DragStarting`) raised from `EndCustomDrag` (`SizingDialogWindow.xaml.cs:141-153`), and handle it in `SizingFrameFeature` next to the existing `DragStarting` subscription. Guard with `_suppressSync = true` around the placement call so the LocationChanged events emitted by repositioning don't recursively retrigger `SyncFrameToDialog`.

### 3. Re-run cascade after drag-to-resize

`OnFrameResizeCompleted` (`SizingFrameFeature.cs:501-528`) already updates `_state.Width/_state.Height` and calls `SyncFrameToDialog()` at the tail. Capture `wasExpanded` at the start of the handler (before any field/visibility updates that could flip mode), then replace the trailing `SyncFrameToDialog()` with:

```
if (_state.Left.HasValue && _state.Top.HasValue)
    PlaceAtSavedPosition(_state.Left.Value, _state.Top.Value, allowExpand: wasExpanded);
```

Note: when the user resizes from the bottom edge, `_state.Left/Top` (interior top-left) doesn't change — only height does — so the cascade gets the correct anchor. When they resize from the top edge, `OnFrameResizing` will need to keep `_state.Top` in sync (verify during implementation; if missing, add it).

### 4. Area-select handoff and initial show

`ShowAtRect` (`SizingFrameFeature.cs:122-133`) and `Show` (`SizingFrameFeature.cs:109-120`) both already route through `PlaceAtSavedPosition`, so they get the new tier for free.

### 5. Hide dialog during screenshot when it sits inside the frame

When the dialog is placed inside the frame, it overlaps the screenshot capture region. The existing capture flow hides only the frame border before grabbing pixels. Add: hide the dialog too whenever capture runs (regardless of inside/outside, since the dialog is `Topmost`). Locate the screenshot path (search for `CaptureQuickSave` and the regular capture entry in `SizingFrameFeature.cs`); insert dialog `Hide`/restore around the capture call.

## Notes / decisions

- **No "fits inside" gate.** If the frame is small enough that the compact dialog overflows the interior, we still place it at the interior bottom-left and let it overflow. Reason: the user explicitly said "in other cases draw it in compact form inside" — a hard fallback. Re-centering would defeat the whole point of this change.
- **`Bounds` vs `WorkingArea`.** Existing fit check `IsRectFullyOnScreen` uses `Screen.Bounds` (full monitor, including taskbar area). Keep as-is for this change — matches existing behavior and the user's wording ("bottom screen border"). If dialogs end up under the taskbar in practice, switch to `WorkingArea` in a follow-up.
- **"Current form" interpretation.** For move/resize re-evaluation, "current form" = whatever the dialog was in immediately before the operation (captured as `wasExpanded` in the handler). A dialog that the user toggled to compact stays compact through subsequent moves/resizes — it does not silently re-expand just because there's now room below. For initial show / area-select handoff there is no "before" state, so we default to trying expanded first (existing behavior). No new persisted "preferred form" state — the current `_isCompact` field on the dialog is the source of truth.
- **Centering still exists** for the case where the implied frame position is fully off-screen (e.g. resolution change between sessions). Do not remove `PlaceDialogAndFrameCentered`.

## Critical files

- `src/Features/SizingFrame/SizingFrameFeature.cs` — cascade body, drag-end wiring, resize-end change, screenshot hide.
- `src/Features/SizingFrame/SizingDialogWindow.xaml.cs` — emit new `DragEnded` event from `EndCustomDrag`.

No XAML changes. No changes to `SizingFrameBorder.cs` or `state.json` schema.

## Verification

End-to-end, after `dotnet build src/CropStage.csproj -c Release` + deploy:

1. **Below-screen-bottom area-select.** Trigger area-select, drag a small selection that hugs the bottom 50px of the screen. Confirm the dialog appears in compact mode at the selection's interior bottom-left, not centered.
2. **Drag frame down.** With a normally-placed frame, drag the dialog handle so the frame's bottom edge ends up within ~30px of the screen bottom. Release. Confirm dialog flips to compact-inside without the frame snapping back to center.
3. **Drag frame back up — preserves compact.** Reverse the drag — drag away from the bottom. Confirm the dialog stays compact-outside (does NOT auto-expand). Then double-click the title to expand, drag again — confirm it now stays expanded as long as it fits.
4. **Resize from top edge into bottom region.** Resize the frame so its bottom edge approaches the screen bottom. Release. Confirm cascade fires.
5. **Restart with bottom-hugging saved state.** Manually edit `state.json` so `Top + Height` is within ~10px of screen bottom. Restart app, open the frame. Confirm compact-inside placement on initial show.
6. **Screenshot with dialog inside.** In the compact-inside state, click Screenshot. Confirm the saved image contains only the frame interior contents — no dialog chrome.
7. **Multi-monitor.** Repeat (1) and (2) on a non-primary monitor with different DPI. Confirm placement math is correct (no offset by DPI ratio).
8. **Tiny frame.** Drag-resize the frame smaller than the compact dialog's footprint. Confirm dialog is placed inside (overflowing) rather than triggering a recenter.
