---
name: Sizing frame drag-to-resize design
description: Why WS_THICKFRAME + WM_NCHITTEST for resize, how Esc hotkey is managed during resize, and the _suppressSync pattern
type: project
---
Drag-to-resize uses WS_THICKFRAME + WM_NCHITTEST (not manual mouse tracking) for native resize cursors and behavior. WPF with WindowStyle.None and ResizeMode.NoResize doesn't set WS_THICKFRAME, so it's added via SetWindowLong after window creation and re-applied after Show() since WPF may reset styles during show.

**Esc during resize:** The global Esc hotkey (RegisterHotKey) is unregistered on WM_ENTERSIZEMOVE and re-registered on WM_EXITSIZEMOVE. This is necessary because RegisterHotKey intercepts the key at the input level — it posts WM_HOTKEY instead of WM_KEYDOWN, so the system resize loop would never see Esc to cancel. Unregistering lets Esc go through normal input processing, which the system resize modal loop handles natively.

**_suppressSync pattern:** During resize, the frame window drives the dialog position (reversed from normal flow where the dialog drives the frame). A `_suppressSync` flag prevents SyncFrameToDialog from repositioning the frame in response to dialog LocationChanged/TextChanged events during resize, avoiding feedback loops. The flag is set in OnFrameResizing and cleared in its finally block.

**Cancel detection:** On WM_EXITSIZEMOVE, if final dimensions match persisted state, the resize was cancelled (Esc or right-click). The dialog is restored to its pre-resize position.
