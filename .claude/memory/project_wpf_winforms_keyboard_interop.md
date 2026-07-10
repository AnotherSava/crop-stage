---
name: WPF dialog needs ElementHost.EnableModelessKeyboardInterop in WinForms tray app
description: When a WPF Window is shown modelessly from a WinForms Application.Run pump (tray app pattern), text input silently fails — KeyDown fires but TextInput doesn't, so letters/digits never appear in textboxes (Backspace/Delete still work)
type: project
---
The Crop Stage tray app uses `System.Windows.Forms.Application.Run(new TrayApplicationContext())` for the message pump and shows a WPF dialog modelessly via `_dialog.Show()`. In this exact hybrid configuration, WPF's input system never receives `WM_CHAR` for the dialog — `WM_KEYDOWN` reaches it (KeyDown event fires) but text input is silently dropped. Symptom: typing letters/digits in the dialog's textboxes does nothing, while Backspace/Delete work (because TextBox handles those in KeyDown).

Fix: after `_dialog.Show()`, call:

```csharp
System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_dialog);
```

This installs a WinForms `IMessageFilter` that forwards keyboard messages into WPF's `ComponentDispatcher` so the WPF input system processes them correctly. See `src/Features/SizingFrame/SizingFrameFeature.cs` `Show()`.

**Why:** WPF's `HwndKeyboardInputProvider` expects `WM_CHAR` to flow through WPF's input pipeline. When the host is a WinForms pump, that pipeline isn't wired up automatically; WinForms's `TranslateMessage` does post `WM_CHAR` to the queue, but WPF's input system requires the message filter bridge to recognize it as text input. Without the bridge, the `WM_CHAR` is never converted into a `TextInput` event.

**How to apply:**
- Required for any WPF Window opened modelessly from a WinForms `Application.Run` pump (tray apps, hybrid apps, sometimes shell extensions).
- Call once per WPF Window after `Show()`, before `Activate()`.
- Diagnostic for "is it this bug?": add `WidthBox.PreviewKeyDown` and `WidthBox.PreviewTextInput` log handlers. If KeyDown fires for letters but PreviewTextInput never does, this is the bug — don't waste time blaming Grammarly, TSF (`InputMethod.IsInputMethodEnabled`), keyboard hooks, or other apps.
- Same pattern likely applies to any future WPF dialog added to this app (e.g. the planned grid overlay's settings dialog).
