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
/// </summary>
public sealed class SizingFrameBorder : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Window[] _edges = new Window[4];
    private readonly double _thickness;
    private readonly SolidColorBrush _brush;
    private bool _created;
    private bool _disposed;

    public SizingFrameBorder(string colorHex, int thicknessPixels)
    {
        var dpi = AppUtilities.GetPrimaryDpiScale();
        _thickness = thicknessPixels / dpi;
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

    public void SetInteriorGeometry(double interiorLeft, double interiorTop, double interiorWidth, double interiorHeight)
    {
        EnsureCreated();
        var t = _thickness;
        var outerLeft = interiorLeft - t;
        var outerTop = interiorTop - t;
        var outerWidth = interiorWidth + 2 * t;

        SetBounds(_edges[0], outerLeft, outerTop, outerWidth, t);                       // top
        SetBounds(_edges[1], outerLeft, interiorTop + interiorHeight, outerWidth, t);   // bottom
        SetBounds(_edges[2], outerLeft, interiorTop, t, interiorHeight);                // left
        SetBounds(_edges[3], interiorLeft + interiorWidth, interiorTop, t, interiorHeight); // right
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
                Width = _thickness,
                Height = _thickness,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                UseLayoutRounding = true
            };
            w.SourceInitialized += OnSourceInitialized;
            _edges[i] = w;
        }
        _created = true;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private static void SetBounds(Window w, double x, double y, double width, double height)
    {
        if (w.Left != x) w.Left = x;
        if (w.Top != y) w.Top = y;
        if (w.Width != width) w.Width = width;
        if (w.Height != height) w.Height = height;
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
}
