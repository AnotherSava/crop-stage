using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CropStage.Features.SizingFrame;

/// <summary>
/// Renders the sizing frame as a single transparent topmost window whose content is:
/// a <see cref="System.Windows.Controls.Border"/> drawing the outline and two <see cref="Line"/>
/// shapes forming a diagonal X (toggled on when the frame straddles the desktop edge).
///
/// Positioned via <c>SetWindowPos</c> in physical virtual-screen pixels. WPF renders content
/// in DIPs against the window's current monitor DPI; when the window crosses a DPI boundary,
/// WPF fires <c>WM_DPICHANGED</c> and rescales the window — we intentionally don't override
/// this because fighting it creates position/size feedback loops between adjacent windows.
/// </summary>
public sealed class SizingFrameBorder : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly int _thicknessPx;
    private readonly SolidColorBrush _brush;
    private Window? _window;
    private Border? _border;
    private Line? _line1;
    private Line? _line2;
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
        _window!.Show();
        // WS_EX_NOACTIVATE topmost windows can drop below other windows because they never
        // participate in foreground activation. Re-assert HWND_TOPMOST explicitly.
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Hide() => _window?.Hide();

    public void Close() => _window?.Close();

    public void SetInteriorGeometry(int interiorLeftPx, int interiorTopPx, int interiorWidthPx, int interiorHeightPx)
    {
        EnsureCreated();
        var t = _thicknessPx;
        var outerLeft = interiorLeftPx - t;
        var outerTop = interiorTopPx - t;
        var outerWidth = interiorWidthPx + 2 * t;
        var outerHeight = interiorHeightPx + 2 * t;
        SetPhysicalBounds(_window!, outerLeft, outerTop, outerWidth, outerHeight);
        UpdateContentDimensions(outerWidth, outerHeight);
    }

    /// <summary>
    /// Shows/hides the diagonal X overlay. Used as a visual "screenshot unavailable"
    /// indicator when the frame straddles the desktop edge.
    /// </summary>
    public void SetCrossVisible(bool visible)
    {
        if (_line1 == null || _line2 == null) return;
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        _line1.Visibility = vis;
        _line2.Visibility = vis;
    }

    private void UpdateContentDimensions(int outerWidthPx, int outerHeightPx)
    {
        if (_window == null || _border == null || _line1 == null || _line2 == null) return;
        // Use WPF's render DPI (not the OS's current monitor DPI) — we swallow WM_DPICHANGED
        // so WPF's render DPI stays at creation. If we used the OS monitor DPI, DIP values
        // would be multiplied by WPF's stale render DPI and produce the wrong physical size:
        // the border draws thicker than intended and bleeds into the screenshot interior.
        var dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var outerWidthDip = outerWidthPx / dpi;
        var outerHeightDip = outerHeightPx / dpi;
        var tDip = _thicknessPx / dpi;

        _border.BorderThickness = new Thickness(tDip);

        // Diagonals run corner-to-corner of the interior (inside the border).
        var x0 = tDip;
        var y0 = tDip;
        var x1 = outerWidthDip - tDip;
        var y1 = outerHeightDip - tDip;
        _line1.X1 = x0; _line1.Y1 = y0; _line1.X2 = x1; _line1.Y2 = y1;
        _line2.X1 = x1; _line2.Y1 = y0; _line2.X2 = x0; _line2.Y2 = y1;
        _line1.StrokeThickness = tDip;
        _line2.StrokeThickness = tDip;
    }

    private void EnsureCreated()
    {
        if (_created) return;

        _border = new Border
        {
            BorderBrush = _brush,
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        _line1 = new Line { Stroke = _brush, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        _line2 = new Line { Stroke = _brush, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        var grid = new Grid();
        grid.Children.Add(_border);
        grid.Children.Add(_line1);
        grid.Children.Add(_line2);

        _window = new Window
        {
            Title = "Sizing Frame",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = grid,
            Left = -10000,
            Top = -10000,
            Width = 1,
            Height = 1,
            UseLayoutRounding = true
        };
        // Force HWND creation so SetWindowPos can position the window before it's shown.
        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        // Swallow WM_DPICHANGED so WPF doesn't auto-rescale the frame to DIP-preserving bounds
        // when it crosses a DPI boundary — that briefly shrinks/stretches the frame before our
        // next SyncFrameToDialog corrects it, producing a visible flash. The frame is a solid
        // border + diagonals; keeping WPF at its original render DPI is visually imperceptible.
        HwndSource.FromHwnd(hwnd)?.AddHook(FrameWndProcHook);
        _created = true;
    }

    private static IntPtr FrameWndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED) handled = true;
        return IntPtr.Zero;
    }

    private static void SetPhysicalBounds(Window w, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Pass HWND_TOPMOST (not IntPtr.Zero + SWP_NOZORDER) so each geometry update also
        // re-asserts topmost z-order — a WS_EX_NOACTIVATE window otherwise gets demoted.
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
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
