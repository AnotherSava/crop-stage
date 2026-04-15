using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CropStage.Features.SizingFrame;

/// <summary>
/// Renders a rectangular frame border as 4 thin opaque topmost windows (top, bottom, left, right).
///
/// Intentionally avoids <c>AllowsTransparency="True"</c>: a per-pixel-transparent (layered) window
/// covering the frame interior forces DWM to recomposite ~1MP of alpha on every topmost-Z-order
/// disturbance, which — critically — includes focus changes between other topmost windows.
/// Four small opaque windows participate in normal Z-order without the composition cost.
///
/// Edges are positioned via <c>SetWindowPos</c> in physical virtual-screen pixels (bypassing
/// WPF's DIP system) so they align pixel-exact even when the frame spans monitors with
/// different DPI — WPF's per-window DIP rounding otherwise leaves visible gaps at corners.
/// </summary>
public sealed class SizingFrameBorder : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Window[] _edges = new Window[4];
    private readonly int _thicknessPx;
    private readonly SolidColorBrush _brush;
    private bool _created;
    private bool _disposed;

    public SizingFrameBorder(string colorHex, int thicknessPixels)
    {
        _thicknessPx = thicknessPixels;
        var color = Colors.Red;
        if (AppConfig.TryParseHexColor(colorHex, out var rgb))
            color = Color.FromRgb(rgb.R, rgb.G, rgb.B);
        // Not frozen — needs to be animatable for Flash().
        _brush = new SolidColorBrush(color);
    }

    /// <summary>
    /// Smoothly fades the border to white and back. Used as screenshot feedback
    /// so the frame stays visible throughout (no hide/show jank).
    /// </summary>
    public void Flash()
    {
        if (!_created) return;
        var anim = new ColorAnimation
        {
            To = Colors.White,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    public void Show()
    {
        EnsureCreated();
        foreach (var w in _edges) w.Show();
    }

    public void Hide()
    {
        if (!_created) return;
        foreach (var w in _edges) w.Hide();
    }

    public void Close()
    {
        if (!_created) return;
        foreach (var w in _edges) w.Close();
    }

    public void SetInteriorGeometry(int interiorLeftPx, int interiorTopPx, int interiorWidthPx, int interiorHeightPx)
    {
        EnsureCreated();
        var t = _thicknessPx;
        var outerLeft = interiorLeftPx - t;
        var outerTop = interiorTopPx - t;
        var outerWidth = interiorWidthPx + 2 * t;

        SetPhysicalBounds(_edges[0], outerLeft, outerTop, outerWidth, t);                       // top
        SetPhysicalBounds(_edges[1], outerLeft, interiorTopPx + interiorHeightPx, outerWidth, t); // bottom
        SetPhysicalBounds(_edges[2], outerLeft, interiorTopPx, t, interiorHeightPx);             // left
        SetPhysicalBounds(_edges[3], interiorLeftPx + interiorWidthPx, interiorTopPx, t, interiorHeightPx); // right
    }

    private void EnsureCreated()
    {
        if (_created) return;
        for (var i = 0; i < 4; i++)
        {
            var w = new Window
            {
                Title = "Sizing Frame Edge",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = _brush,
                Width = _thicknessPx,
                Height = _thicknessPx,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                UseLayoutRounding = true
            };
            // Force HWND creation so SetWindowPos can position the window
            // before it's shown (prevents a flash at Left=-10000 on Show()).
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            _edges[i] = w;
        }
        _created = true;
    }

    private static void SetPhysicalBounds(Window w, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
