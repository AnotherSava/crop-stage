using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CropStage.Features.SizingFrame;

/// <summary>
/// Finds the top-level window whose on-screen, Z-order-aware visible area inside a
/// given capture rectangle is largest. Used to label quick-save screenshots with the
/// app whose pixels dominate the shot.
/// </summary>
public static class TopWindowFinder
{
    private static readonly HashSet<string> IgnoredClassNames = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    public static string? FindDominantLabel(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        var capture = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        var ownPid = (uint)Process.GetCurrentProcess().Id;

        var candidates = new List<Candidate>();
        var callback = new EnumWindowsProc((hwnd, _) =>
        {
            try
            {
                if (TryGetCandidate(hwnd, ownPid, capture, out var entry))
                    candidates.Add(entry);
            }
            catch (Exception ex)
            {
                Logger.Warn($"TopWindowFinder: skipping window {hwnd.ToInt64():X} due to {ex.GetType().Name}: {ex.Message}");
            }
            return true;
        });

        if (!EnumWindows(callback, IntPtr.Zero))
            Logger.Warn($"TopWindowFinder: EnumWindows failed, last error {Marshal.GetLastWin32Error()}");

        GC.KeepAlive(callback);

        if (candidates.Count == 0) return null;

        var winner = PickBestByVisibleArea(candidates, capture);
        if (winner == null) return null;

        var appName = TryGetProcessName(winner.Pid);
        return string.IsNullOrEmpty(appName) ? winner.Title : $"{appName} - {winner.Title}";
    }

    private static bool TryGetCandidate(IntPtr hwnd, uint ownPid, RECT capture, out Candidate entry)
    {
        entry = default!;

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == ownPid) return false;

        var className = GetClassName(hwnd);
        if (className != null && IgnoredClassNames.Contains(className)) return false;

        var titleLen = GetWindowTextLengthW(hwnd);
        if (titleLen <= 0) return false;

        var title = GetWindowText(hwnd, titleLen);
        if (string.IsNullOrWhiteSpace(title)) return false;

        var bounds = GetVisualBounds(hwnd);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;

        var clipped = Intersect(bounds, capture);
        if (clipped.Right <= clipped.Left || clipped.Bottom <= clipped.Top) return false;

        entry = new Candidate(hwnd, bounds, title, pid);
        return true;
    }

    private static Candidate? PickBestByVisibleArea(List<Candidate> candidates, RECT capture)
    {
        var availableRgn = CreateRectRgn(capture.Left, capture.Top, capture.Right, capture.Bottom);
        var scratchRgn = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Candidate? best = null;
            long bestArea = 0;

            foreach (var c in candidates)
            {
                var clipped = Intersect(c.Bounds, capture);
                if (clipped.Right <= clipped.Left || clipped.Bottom <= clipped.Top) continue;

                var windowRgn = CreateRectRgn(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom);
                try
                {
                    if (CombineRgn(scratchRgn, windowRgn, availableRgn, RGN_AND) == NULLREGION) continue;

                    var area = RegionArea(scratchRgn);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = c;
                    }

                    if (CombineRgn(availableRgn, availableRgn, windowRgn, RGN_DIFF) == NULLREGION) break;
                }
                finally
                {
                    DeleteObject(windowRgn);
                }
            }

            return bestArea > 0 ? best : null;
        }
        finally
        {
            DeleteObject(availableRgn);
            DeleteObject(scratchRgn);
        }
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);

            try
            {
                var description = proc.MainModule?.FileVersionInfo?.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                    return description.Trim();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // MainModule access denied (protected/system process, arch mismatch) — fall back below.
            }

            return proc.ProcessName ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    private sealed record Candidate(IntPtr Hwnd, RECT Bounds, string Title, uint Pid);

    private static RECT GetVisualBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmBounds, Marshal.SizeOf<RECT>()) == 0)
            return dwmBounds;

        return GetWindowRect(hwnd, out var rect) ? rect : default;
    }

    private static RECT Intersect(RECT a, RECT b) => new()
    {
        Left = Math.Max(a.Left, b.Left),
        Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right),
        Bottom = Math.Min(a.Bottom, b.Bottom),
    };

    private static string? GetClassName(IntPtr hwnd)
    {
        var buf = new StringBuilder(256);
        var len = GetClassNameW(hwnd, buf, buf.Capacity);
        return len > 0 ? buf.ToString(0, len) : null;
    }

    private static string GetWindowText(IntPtr hwnd, int length)
    {
        var buf = new StringBuilder(length + 1);
        var copied = GetWindowTextW(hwnd, buf, buf.Capacity);
        return copied > 0 ? buf.ToString(0, copied) : "";
    }

    private static long RegionArea(IntPtr rgn)
    {
        var dataSize = GetRegionData(rgn, 0, IntPtr.Zero);
        if (dataSize == 0) return 0;

        var buffer = Marshal.AllocHGlobal((int)dataSize);
        try
        {
            if (GetRegionData(rgn, dataSize, buffer) == 0) return 0;

            var header = Marshal.PtrToStructure<RGNDATAHEADER>(buffer);
            var rectPtr = IntPtr.Add(buffer, Marshal.SizeOf<RGNDATAHEADER>());
            long area = 0;
            for (var i = 0; i < header.nCount; i++)
            {
                var r = Marshal.PtrToStructure<RECT>(IntPtr.Add(rectPtr, i * Marshal.SizeOf<RECT>()));
                area += (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            }
            return area;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RGNDATAHEADER
    {
        public uint dwSize;
        public uint iType;
        public uint nCount;
        public uint nRgnSize;
        public RECT rcBound;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int RGN_AND = 1;
    private const int RGN_DIFF = 4;
    private const int NULLREGION = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int cbAttr);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int cbAttr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dst, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint GetRegionData(IntPtr rgn, uint dwCount, IntPtr lpRgnData);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
