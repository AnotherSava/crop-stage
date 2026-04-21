# Quick-save filenames: append dominant window title

## Context

The quick-save area-select flow (Ctrl+Shift+8, commit `bf098e4`) currently writes screenshots with a bare timestamp as the filename — `2026-04-20_14-35-42.png`. That makes filenames hard to scan when reviewing a folder of captures: you can't tell which app a screenshot came from without opening it.

Goal: append the title of the app window that occupies the largest *visible* area inside the captured rectangle, so filenames look like `2026-04-20_14-35-42 - Google Chrome.png`. If no suitable window is found, fall back to the current timestamp-only format.

User-chosen design decisions:
- **Occlusion-aware**: iterate windows in Z-order, count only pixels actually visible in the capture (not rect overlap alone).
- **Always on**: no tray toggle — the feature applies whenever quick-save fires.
- **Format**: `{timestamp} - {title}.png`, macOS-screenshot style.
- **Max title length**: 60 chars (truncate mid-word, no ellipsis).

## Approach

### 1. New file: `src/Features/SizingFrame/TopWindowFinder.cs`

Static utility that, given a capture rectangle in physical screen pixels, enumerates top-level windows in Z-order and returns the raw title of the one with the largest *visible* area inside the rect, or `null`.

Public API:

```csharp
public static class TopWindowFinder
{
    public static string? FindDominantTitle(int x, int y, int width, int height);
}
```

P/Invoke surface (all via `user32.dll` / `dwmapi.dll` / `gdi32.dll`):
- `EnumWindows(EnumWindowsProc, IntPtr)` — Z-order top-to-bottom traversal.
- `IsWindowVisible(hwnd)`, `IsIconic(hwnd)` — visibility gates.
- `GetWindowRect(hwnd, out RECT)` — fallback bounds.
- `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ...)` — preferred bounds that exclude the invisible drop-shadow margin on Win10/11 (so adjacent windows don't incorrectly "overlap" by ~7 shadow pixels).
- `DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ...)` — skip UWP ghost windows.
- `GetWindowTextLengthW`, `GetWindowTextW` — title extraction.
- `GetClassNameW` — filter shell classes.
- `GetWindowThreadProcessId` — filter our own process.
- `CreateRectRgn`, `CombineRgn`, `GetRegionData`, `DeleteObject` — Z-order occlusion math.

Filter list (a window is a candidate iff **all** hold):
- Visible, not minimized, not cloaked.
- Non-empty title (`GetWindowTextLengthW > 0`).
- Process ID ≠ `Process.GetCurrentProcess().Id` (guards against the sizing frame or area-select overlay if still in Z-order).
- Class name not in `{ "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow" }` — filters desktop, taskbars, notification overflow, and bare UWP cores.
- Bounds (from `DWMWA_EXTENDED_FRAME_BOUNDS`, falling back to `GetWindowRect`) have positive area.

Algorithm:
1. Build the capture `RECT` and an `availableRgn = CreateRectRgn(capture)`.
2. Accumulate candidates into a list during the `EnumWindows` callback (keeping Z-order).
3. For each candidate (topmost first):
   - `windowRgn = CreateRectRgn(window ∩ capture)`. If empty, skip.
   - `visibleRgn = windowRgn ∩ availableRgn` (via `CombineRgn(RGN_AND)` into a scratch region).
   - `area = sum of (r.right-r.left)*(r.bottom-r.top) over rects in GetRegionData(visibleRgn)`.
   - If `area > bestArea`: record `(title, area)`.
   - `availableRgn = availableRgn − windowRgn` (via `CombineRgn(RGN_DIFF)`). Bail out early when `availableRgn` is empty.
4. Return the best title, or `null` if no candidate had non-zero visible area.

All GDI region handles released in a `try/finally`. `EnumWindows` callback must not throw (swallow any per-window exception, log at `Logger.Warn`).

### 2. Modify `src/Features/SizingFrame/SizingFrameFeature.cs`

Replace the one-line filename build at `CaptureQuickSave` (currently line 574) with:

```csharp
var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
var title = TopWindowFinder.FindDominantTitle(leftPx, topPx, widthPx, heightPx);
var filename = BuildQuickSaveFilename(timestamp, title);
```

Add two private static helpers on `SizingFrameFeature` (or a tiny new static class if you prefer — your call at implementation time):

```csharp
private const int MaxTitleChars = 60;

private static string BuildQuickSaveFilename(string timestamp, string? title)
{
    var cleaned = SanitizeForFilename(title);
    return string.IsNullOrEmpty(cleaned)
        ? $"{timestamp}.png"
        : $"{timestamp} - {cleaned}.png";
}

private static string SanitizeForFilename(string? title)
{
    if (string.IsNullOrWhiteSpace(title)) return "";
    var invalid = Path.GetInvalidFileNameChars();
    var sb = new StringBuilder(title.Length);
    foreach (var c in title)
    {
        if (char.IsControl(c)) continue;
        if (Array.IndexOf(invalid, c) >= 0) continue;
        sb.Append(c);
    }
    var collapsed = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    if (collapsed.Length > MaxTitleChars) collapsed = collapsed.Substring(0, MaxTitleChars).TrimEnd();
    return collapsed;
}
```

`Path.GetInvalidFileNameChars()` already includes `<>:"/\|?*` and the control-char range on Windows; the explicit `char.IsControl` skip is belt-and-braces for clarity. `ScreenshotCapture.DisambiguatePath` (existing, at `ScreenshotCapture.cs:33`) handles collisions if two quick-saves land in the same second with the same title.

### 3. Existing code to reuse — no changes needed

- `ScreenshotCapture.Capture` (`src/Features/SizingFrame/ScreenshotCapture.cs:14`) already takes the filename and disambiguates — no changes.
- The clipboard/log path in `CaptureQuickSave` after the filename is built is unchanged.
- No `AppConfig` / `FrameState` changes (always-on, no toggle).
- `AppUtilities.cs` is untouched — the new P/Invoke surface lives with its only consumer.

## Critical files

- `src/Features/SizingFrame/TopWindowFinder.cs` — **new**
- `src/Features/SizingFrame/SizingFrameFeature.cs:560-586` — edit `CaptureQuickSave`, add `BuildQuickSaveFilename` + `SanitizeForFilename` helpers, add `using System.Text;` and `using System.Text.RegularExpressions;` if not present.

## Tests

Add unit tests in `tests/CropStage.Tests.csproj` for the pure filename logic (the P/Invoke path is best verified manually):

- `SanitizeForFilename`:
  - `null` / empty / whitespace → `""`.
  - `"Google Chrome"` → `"Google Chrome"` (spaces preserved).
  - `"foo<bar>:baz?"` → `"foobarbaz"` (invalid chars stripped).
  - String with tabs and runs of spaces → single-spaced.
  - 200-char input → 60-char output, trailing space trimmed.
  - Control chars (e.g. ``) stripped.
- `BuildQuickSaveFilename`:
  - `(ts, null)` → `"{ts}.png"`.
  - `(ts, "  ")` → `"{ts}.png"`.
  - `(ts, "Chrome")` → `"{ts} - Chrome.png"`.
  - `(ts, "bad<>chars")` → `"{ts} - badchars.png"`.

Test visibility: if `SanitizeForFilename` / `BuildQuickSaveFilename` stay private on `SizingFrameFeature`, expose them via `internal` + `InternalsVisibleTo("CropStage.Tests")` (check whether the test csproj already uses this — if not, put the helpers on a new small `internal static class QuickSaveFilename` in `src/Features/SizingFrame/` to keep them unit-testable without broadening SizingFrameFeature's surface). Per CLAUDE.md rule: do not add production logic solely to enable tests — these helpers are genuine production logic, the visibility tweak is the only concession.

## Verification

End-to-end smoke test (after `dotnet build src/CropStage.csproj -c Release` and deploy per memory `feedback_auto_deploy.md`):

1. Open Chrome maximized on monitor 1; open VS Code sized to cover the right half of Chrome.
2. Press Ctrl+Shift+8, drag a rect entirely over visible Chrome area → saved file name contains the Chrome window title.
3. Drag a rect entirely over VS Code → file name contains the VS Code window title.
4. Drag a rect that spans both windows, with VS Code covering most of the rect → file name reflects VS Code (Z-order: topmost wins when it covers more visible pixels).
5. Drag a rect that spans both, with Chrome visible in most of the rect → file name reflects Chrome (even though VS Code is on top elsewhere).
6. Minimize all apps, drag over empty desktop → file name is bare timestamp (Progman/WorkerW filtered).
7. Open a Chrome tab with an extremely long title → file name is truncated to 60 title chars, still ends with `.png`, no invalid characters.
8. Pick a title containing `:` or `|` (browser tab with "Section: Topic | Site") → file name has those chars stripped; no Windows-save error.
9. Quick-save twice in the same second on the same app → `DisambiguatePath` appends `_1` as before.

Logs to sanity-check at `%LOCALAPPDATA%\CropStage` (or wherever `Logger` writes) — the existing `Logger.Info($"Quick-save screenshot: '{saved}'")` line will show the final path for each attempt. No new log lines needed beyond that; `TopWindowFinder` can stay silent on the happy path and only `Logger.Warn` on P/Invoke failures.
