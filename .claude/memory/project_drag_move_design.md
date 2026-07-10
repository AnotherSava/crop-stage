---
name: Sizing frame drag-move design
description: Custom drag loop + cursor warp into the frame — why DragMove won't work, warp target math, cursor hide/restore sequence
type: project
---
The sizing-frame composite (dialog below, frame above, pinned together) is moved by dragging the dialog. The frame itself has WS_THICKFRAME for resize but no HTCAPTION, so it can't be drag-moved. This memo captures why the move path doesn't use `DragMove()` and the invariants the current loop relies on.

## Why custom drag (not DragMove)

**We warp the cursor into the frame's interior bottom-left pixel at drag start** so the user can drag the composite all the way to the bottom of the screen — without the warp, the cursor bumps the screen edge while the frame is still (dialog height + border) pixels above it.

`DragMove()` / `SC_MOVE` anchors on the mouse-down position from the message queue, so warping the cursor causes a first-tick shift equal to the warp delta. Even `WM_NCLBUTTONDOWN` with explicit `MAKELPARAM(x, y)` doesn't override that. See `~/.claude/learnings/dotnet-tray-app.md` — "DragMove anchors on mouse-down position" — for the general case. Our loop bypasses `SC_MOVE` entirely: `CaptureMouse` + `PreviewMouseMove` + `PreviewMouseLeftButtonUp` + `LostMouseCapture`, with `_dragAnchorOffset` = `warpPoint − windowScreen` tracked manually.

## Warp target math

`interiorLeftPx`, `interiorTopPx + height − 1` — the last pixel *inside* the interior, leftmost column. `interiorTop + height` (no -1) lands on the border line, one pixel outside. The coordinator's `OnDialogDragStarting` computes this via the existing `GetInteriorOriginPx` so the math stays consistent with the rest of the geometry code.

## Cursor hide/restore sequence

The full ordering matters:

1. `ShowCursor(false)` — instant Win32-level hide (before any `SetCursorPos`, which otherwise flashes the cursor along the warp).
2. `OverrideCursor = Cursors.None` — catches any cursor changes during the drag (e.g. over buttons).
3. `Opacity = 0` — dialog becomes invisible; `LocationChanged` still fires so the frame tracks the cursor.
4. `SetCursorPos(warpX, warpY)` — warp.
5. `CaptureMouse()` — route move/up events here.
6. ... drag ...
7. `ReleaseMouseCapture` (in `EndCustomDrag`).
8. Fire `DragEnded(_clickOffsetInDialog)` to the feature, which re-evaluates inside/outside placement, repositions the dialog if needed, then warps the cursor onto the dialog's *final* position via `WarpCursorIntoDialog`. `_clickOffsetInDialog = originalCursor − dialog.physicalLeft/Top` saved at step 1. The dialog-relative offset (not warp-point-relative) lands the cursor correctly even when the cascade flipped the dialog from outside-below to inside-frame or vice versa.
9. `OverrideCursor = null`.
10. `ShowCursor(true)` — balances step 1.
11. `Opacity = 1`.

`EndCustomDrag` runs from `MouseLeftButtonUp`, `LostMouseCapture`, and is safe to call multiple times (gated by `_dragging`). `ShowCursor` is a per-process counter — always balance to keep the cursor visible in other code paths.

## Inside vs outside anchoring

The dialog can be anchored either outside-below the frame (default) or inside the frame at the interior bottom-left (used when the dialog wouldn't fit below — e.g. frame's bottom hugs the screen edge). `_dialogInsideFrame` tracks the current anchor; `GetInteriorOriginPx` branches on it. The drag math (`_dragAnchorOffset`) is captured at drag start from the current anchor and stays constant for the drag — anchoring doesn't change mid-drag. After drag end, `OnDialogDragEnded` runs the placement cascade which may flip the anchor; the cursor warp happens *after* placement so it lands on the dialog's final position.

The drag is also clamped to `SystemInformation.VirtualScreen` in `SyncFrameToDialog` so the frame can't be pushed off the visible area — matches the area-select clamp on creation. When the clamp pulls the dialog back, the (hidden) cursor is also warped to the clamped frame interior bottom-left to avoid the overshoot dead zone (see global `feedback_drag_clamp_cursor_warp.md`).

## AllowsTransparency

The dialog's XAML has `AllowsTransparency="True"` specifically to make `Opacity = 0` composite correctly. Without it, the window renders as a black rectangle during drag. Side effect: ClearType is off on the dialog text — acceptable trade for the drag UX. Not purely cosmetic: see the overlay-perf note in `dotnet-tray-app.md` for when *not* to enable AllowsTransparency by default.

## Related

- `project_resize_design.md` — resize path (WS_THICKFRAME, `_suppressSync`). Different loop, different event plumbing.
- `project_sizing_frame_dpi.md` — DPI rules for positioning both windows.
