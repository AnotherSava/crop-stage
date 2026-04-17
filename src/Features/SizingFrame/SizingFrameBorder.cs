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
    private const int GWL_STYLE = -16;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly int _thicknessPx;
    private readonly int _hitPadPx;
    private readonly SolidColorBrush _brush;
    private Window? _window;
    private Border? _hitBorder;
    private Border? _border;
    private Line? _line1;
    private Line? _line2;
    private IntPtr _hwnd;
    private bool _resizable;
    private bool _isResizing;
    private bool _created;
    private bool _disposed;

    /// <summary>Fires continuously during resize: visibleOuterLeftPx, visibleOuterBottomPx, interiorWidthPx, interiorHeightPx.</summary>
    public event Action<int, int, int, int>? FrameResizing;
    public event Action? ResizeStarted;
    public event Action? ResizeCompleted;

    public SizingFrameBorder(string colorHex, int thicknessPixels)
    {
        _thicknessPx = thicknessPixels;
        _hitPadPx = Math.Max(0, (int)Math.Ceiling((8.0 - thicknessPixels) / 2.0));
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
        ApplyResizableStyles();
        // WS_EX_NOACTIVATE topmost windows can drop below other windows because they never
        // participate in foreground activation. Re-assert HWND_TOPMOST explicitly.
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Hide() => _window?.Hide();

    public void Close() => _window?.Close();

    public void SetResizable(bool resizable)
    {
        _resizable = resizable;
        if (!_created) return;
        ApplyResizableStyles();
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public void SetInteriorGeometry(int interiorLeftPx, int interiorTopPx, int interiorWidthPx, int interiorHeightPx)
    {
        EnsureCreated();
        var t = _thicknessPx;
        var p = _hitPadPx;
        var outerLeft = interiorLeftPx - t - p;
        var outerTop = interiorTopPx - t - p;
        var outerWidth = interiorWidthPx + 2 * (t + p);
        var outerHeight = interiorHeightPx + 2 * (t + p);
        SetPhysicalBounds(_window!, outerLeft, outerTop, outerWidth, outerHeight);
        UpdateContentDimensions(outerWidth, outerHeight);
    }

    public (int width, int height) GetInteriorDimensions()
    {
        if (!_created || !GetWindowRect(_hwnd, out var rc)) return (0, 0);
        int edge = _thicknessPx + _hitPadPx;
        int w = (rc.Right - rc.Left) - 2 * edge;
        int h = (rc.Bottom - rc.Top) - 2 * edge;
        return (Math.Max(0, w), Math.Max(0, h));
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

    private void ApplyResizableStyles()
    {
        if (_hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        var style = GetWindowLong(_hwnd, GWL_STYLE);
        if (_resizable)
        {
            exStyle &= ~WS_EX_TRANSPARENT;
            style |= WS_THICKFRAME;
        }
        else
        {
            exStyle |= WS_EX_TRANSPARENT;
            style &= ~WS_THICKFRAME;
        }
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        SetWindowLong(_hwnd, GWL_STYLE, style);
    }

    private void UpdateContentDimensions(int outerWidthPx, int outerHeightPx)
    {
        if (_window == null || _hitBorder == null || _border == null || _line1 == null || _line2 == null) return;
        // Use WPF's render DPI (not the OS's current monitor DPI) — we swallow WM_DPICHANGED
        // so WPF's render DPI stays at creation. If we used the OS monitor DPI, DIP values
        // would be multiplied by WPF's stale render DPI and produce the wrong physical size:
        // the border draws thicker than intended and bleeds into the screenshot interior.
        var dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var outerWidthDip = outerWidthPx / dpi;
        var outerHeightDip = outerHeightPx / dpi;
        var tDip = _thicknessPx / dpi;
        var pDip = _hitPadPx / dpi;

        _hitBorder.BorderThickness = new Thickness(pDip + tDip + pDip);
        _border.Margin = new Thickness(pDip);
        _border.BorderThickness = new Thickness(tDip);

        // Diagonals run corner-to-corner of the interior (inside pad + border).
        var x0 = pDip + tDip;
        var y0 = pDip + tDip;
        var x1 = outerWidthDip - pDip - tDip;
        var y1 = outerHeightDip - pDip - tDip;
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
        _hitBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        var grid = new Grid();
        grid.Children.Add(_hitBorder);
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
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ApplyResizableStyles();
        // Swallow WM_DPICHANGED so WPF doesn't auto-rescale the frame to DIP-preserving bounds
        // when it crosses a DPI boundary — that briefly shrinks/stretches the frame before our
        // next SyncFrameToDialog corrects it, producing a visible flash. The frame is a solid
        // border + diagonals; keeping WPF at its original render DPI is visually imperceptible.
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProcHook);
        _created = true;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        const int WM_NCHITTEST = 0x0084;
        const int WM_SIZING = 0x0214;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;

        switch (msg)
        {
            case WM_DPICHANGED:
                handled = true;
                return IntPtr.Zero;

            case WM_NCHITTEST when _resizable:
            {
                var ht = BorderHitTest(lParam);
                if (ht != 0)
                {
                    handled = true;
                    return (IntPtr)ht;
                }
                break;
            }

            case WM_SIZING when _resizable:
                OnWmSizing(wParam, lParam);
                break;

            case WM_ENTERSIZEMOVE when _resizable:
                _isResizing = true;
                ResizeStarted?.Invoke();
                break;

            case WM_EXITSIZEMOVE when _isResizing:
                _isResizing = false;
                ResizeCompleted?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    private int BorderHitTest(IntPtr lParam)
    {
        const int HTTRANSPARENT = -1;
        const int HTLEFT = 10;
        const int HTRIGHT = 11;
        const int HTTOP = 12;
        const int HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14;
        const int HTBOTTOM = 15;
        const int HTBOTTOMLEFT = 16;
        const int HTBOTTOMRIGHT = 17;

        int mouseX = (short)(lParam.ToInt64() & 0xFFFF);
        int mouseY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (!GetWindowRect(_hwnd, out var rc)) return 0;

        int t = _thicknessPx;
        int p = _hitPadPx;
        int edgeZone = 2 * p + t;

        bool onLeft = mouseX >= rc.Left && mouseX < rc.Left + edgeZone;
        bool onRight = mouseX >= rc.Right - edgeZone && mouseX < rc.Right;
        bool onTop = mouseY >= rc.Top && mouseY < rc.Top + edgeZone;
        bool onBottom = mouseY >= rc.Bottom - edgeZone && mouseY < rc.Bottom;

        if (!onLeft && !onRight && !onTop && !onBottom)
            return HTTRANSPARENT;

        if (onTop && onLeft) return HTTOPLEFT;
        if (onTop && onRight) return HTTOPRIGHT;
        if (onBottom && onLeft) return HTBOTTOMLEFT;
        if (onBottom && onRight) return HTBOTTOMRIGHT;
        if (onLeft) return HTLEFT;
        if (onRight) return HTRIGHT;
        if (onTop) return HTTOP;
        if (onBottom) return HTBOTTOM;

        return HTTRANSPARENT;
    }

    private void OnWmSizing(IntPtr wParam, IntPtr lParam)
    {
        var rc = Marshal.PtrToStructure<RECT>(lParam);
        int edge = wParam.ToInt32();
        int tp = _thicknessPx + _hitPadPx;
        int minOuter = 50 + 2 * tp;

        if (rc.Right - rc.Left < minOuter)
        {
            if (edge is 1 or 4 or 7)
                rc.Left = rc.Right - minOuter;
            else
                rc.Right = rc.Left + minOuter;
        }
        if (rc.Bottom - rc.Top < minOuter)
        {
            if (edge is 3 or 4 or 5)
                rc.Top = rc.Bottom - minOuter;
            else
                rc.Bottom = rc.Top + minOuter;
        }

        Marshal.StructureToPtr(rc, lParam, false);

        int outerW = rc.Right - rc.Left;
        int outerH = rc.Bottom - rc.Top;
        UpdateContentDimensions(outerW, outerH);

        int p = _hitPadPx;
        FrameResizing?.Invoke(rc.Left + p, rc.Bottom - p, outerW - 2 * tp, outerH - 2 * tp);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
