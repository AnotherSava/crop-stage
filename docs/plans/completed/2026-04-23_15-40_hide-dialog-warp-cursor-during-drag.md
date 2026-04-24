# Hide dialog and warp cursor during drag

## Context

The sizing frame is a composite of two topmost windows pinned together:
- `SizingFrameBorder` — transparent resize-only frame (WS_THICKFRAME, no caption, can't be moved on its own).
- `SizingDialogWindow` — the frameless WPF dialog sitting flush at the frame's outer-bottom-left corner. `DragMove()` on the dialog is the only way to *move* the composite, and the frame follows via the `LocationChanged` → `SyncFrameToDialog` path.

Today, while the user drags the composite to a new position, the dialog stays attached at bottom-left and fully visible, which adds visual clutter over the target area the user is trying to drop onto. The change: hide the dialog for the duration of the drag, show it again on mouse-button-up, frame continues to follow the cursor throughout.

## Approach

Wrap `DragMove()` in `try`/`finally` inside `SizingDialogWindow.OnMouseLeftButtonDown` and toggle `Opacity`:

- `Opacity = 0` right before `DragMove()` — visually hides the dialog without calling `ShowWindow(SW_HIDE)`, so the Windows modal move loop (SC_MOVE) keeps running and `WM_MOVE` / `LocationChanged` keep firing throughout the drag.
- `DragMove()` is synchronous and returns on mouse-up.
- `finally { Opacity = 1; }` — always restores, even if the OS cancels the move loop (Alt-Tab, Win+D, lock screen).

Why not `Hide()` / `Visibility.Hidden`: both call `ShowWindow(SW_HIDE)`, which breaks the active SC_MOVE modal loop and the drag stops.

Why keep it local to the dialog (not a coordinator event): `DragMove()` is already encapsulated there and the lifetime is one synchronous call — exposing drag-start/end events just to toggle opacity is overkill. `SizingFrameFeature` already gets what it needs via `LocationChanged` → `SyncFrameToDialog()`.

The existing `catch (InvalidOperationException)` (for the "mouse released before DragMove ran" race) must stay *inside* the `try` so the outer `finally` always restores opacity. Nested structure:

```csharp
try
{
    Opacity = 0;
    try { DragMove(); }
    catch (InvalidOperationException) { /* mouse released before DragMove ran */ }
}
finally { Opacity = 1; }
```

Double-click still routes to `ToggleCompactMode()` — no opacity change on that branch.

## Files

- `src/Features/SizingFrame/SizingDialogWindow.xaml.cs` — modify `OnMouseLeftButtonDown` (currently lines 45–61). Only file touched.

No changes needed in `SizingFrameBorder.cs` or `SizingFrameFeature.cs`; the existing `LocationChanged` → `SyncFrameToDialog` path already keeps the frame glued to the dialog's HWND position, independent of opacity.

## Verification

1. `dotnet build src/CropStage.csproj -c Release` — confirm clean build.
2. Deploy via the `/deploy` skill (per auto-deploy memory).
3. Manual smoke test, covering these cases:
   - **Drag the dialog** (grab any non-input element — label, border, background): dialog goes invisible immediately, the frame follows the cursor, on mouse-up the dialog reappears attached to the frame's bottom-left at the new position.
   - **Mouse-down then release without moving** (no drag): dialog should not flicker to invisible. `DragMove()` throws `InvalidOperationException` in this case — verify opacity returns to 1 cleanly (nested `finally`).
   - **Double-click on non-input area**: toggles compact/expanded mode as before; opacity stays at 1.
   - **Resize via frame border**: unrelated path (WM_ENTERSIZEMOVE → `OnFrameResizing`), dialog should stay visible and track the corner as before — confirm no regression.
   - **Alt-Tab or Win+D mid-drag**: on returning, the dialog must not be stuck invisible. `finally` should have restored it.
   - **Cross-monitor drag** (different DPI): dialog reappears correctly positioned at the frame's bottom-left on the destination monitor.
4. `dotnet test tests/CropStage.Tests.csproj` — confirm no test regressions.
