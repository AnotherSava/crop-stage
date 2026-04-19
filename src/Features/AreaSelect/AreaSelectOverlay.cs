using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CropStage.Features.SizingFrame;

namespace CropStage.Features.AreaSelect;

/// <summary>
/// Fullscreen borderless WinForms overlay spanning the virtual screen. The form is
/// fully opaque and shows a frozen screenshot of the desktop taken at hotkey time as
/// its background — this looks identical to a real transparent overlay while avoiding
/// the transparency path entirely. Layered-window transparency via LWA_COLORKEY was
/// tried and confirmed unusable: DWM treats colorkey pixels as hit-test-transparent
/// even for foreground windows with SetCapture (verified via diagnostic logging),
/// which gated mouse events to the visible crosshair pixels only.
/// </summary>
public sealed class AreaSelectOverlay : Form
{
    private const int MinDragPx = 3;
    private const int SRCCOPY = 0x00CC0020;

    private readonly Pen _borderPen;
    private readonly Pen _crosshairPen;
    private readonly int _borderThickness;
    private readonly CrosshairMode _crosshairMode;
    private readonly Cursor _crossCursor;
    private Bitmap? _backdrop;
    // Native GDI handles for fast BitBlt backdrop composition. DrawImage from a
    // managed Bitmap has per-call GDI+ marshaling overhead; BitBlt from a pre-selected
    // HBITMAP in a compatible DC is a raw kernel blit.
    private IntPtr _memDc;
    private IntPtr _backdropHBitmap;
    private IntPtr _oldBitmap;

    private int _cursorX;
    private int _cursorY;
    private int _monLeft, _monRight, _monTop, _monBottom;
    private bool _haveCrosshair;

    private bool _dragging;
    private Point _dragStartClient;
    private Rectangle _selectionRectClient;
    private bool _haveSelection;
    private bool _rightCaptured;
    private bool _settled;

    public event Action<int, int, int, int>? Selected;
    public event Action? Cancelled;

    public AreaSelectOverlay(AppConfig config, CrosshairMode crosshairMode)
    {
        var color = Color.Red;
        if (AppConfig.TryParseHexColor(config.FrameBorderColor, out var rgb))
            color = Color.FromArgb(rgb.R, rgb.G, rgb.B);
        _borderThickness = Math.Max(1, config.FrameBorderThickness);
        _borderPen = new Pen(color, _borderThickness);
        _crosshairPen = new Pen(color, 1);
        _crosshairMode = crosshairMode;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        // Built-in Cursors.Cross is a monochrome XOR-inverted cursor — over red pixels
        // it renders cyan/blue, over neutral pixels white, which looks broken against
        // our drawn crosshair. Build a solid color cursor once here so the pointer
        // stays a fixed color regardless of what's under it.
        _crossCursor = CreateColorCrossCursor();
        Cursor = _crossCursor;
        DoubleBuffered = true;

        var vs = SystemInformation.VirtualScreen;
        Bounds = vs;

        _backdrop = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_backdrop))
        {
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            // Bake a light dim into the backdrop at capture time — signals "this image
            // is frozen" without adding any runtime cost during tracking.
            using var dim = new SolidBrush(Color.FromArgb(64, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, _backdrop.Width, _backdrop.Height);
        }

        _memDc = CreateCompatibleDC(IntPtr.Zero);
        _backdropHBitmap = _backdrop.GetHbitmap();
        _oldBitmap = SelectObject(_memDc, _backdropHBitmap);
    }

    public new void Show()
    {
        base.Show();
        var pos = Cursor.Position;
        UpdateCrosshair(pos.X - Left, pos.Y - Top, pos.X, pos.Y);
    }

    public void Cancel() => Settle(true);

    private void UpdateCrosshair(int clientX, int clientY, int screenX, int screenY)
    {
        // Always invalidate the old + new crosshair strips, regardless of whether we
        // intend to draw the lines. Those strips are monitor-spanning at the cursor's
        // X and Y, which happens to coincide with the selection rectangle's moving
        // edges during a drag. If they're not invalidated, the moving edges of the
        // selection rect (right + bottom on a right-down drag) visibly fail to draw.
        // Invalidating them here provides that coverage in every mode; _haveCrosshair
        // still controls whether the lines themselves are painted.
        InvalidateCrosshairRegion();

        var monitor = Screen.FromPoint(new Point(screenX, screenY)).Bounds;
        _cursorX = clientX;
        _cursorY = clientY;
        _monLeft = monitor.Left - Left;
        _monRight = monitor.Right - Left;
        _monTop = monitor.Top - Top;
        _monBottom = monitor.Bottom - Top;

        InvalidateCrosshairRegion();
        _haveCrosshair = ShouldShowCrosshair();
    }

    private bool ShouldShowCrosshair() => _crosshairMode switch
    {
        CrosshairMode.None => false,
        CrosshairMode.FirstPoint => !_dragging,
        CrosshairMode.BothPoints => true,
        _ => true
    };

    private void InvalidateCrosshairRegion()
    {
        InvalidateStrip(new Rectangle(_monLeft, _cursorY - 1, Math.Max(0, _monRight - _monLeft), 3));
        InvalidateStrip(new Rectangle(_cursorX - 1, _monTop, 3, Math.Max(0, _monBottom - _monTop)));
    }

    private void InvalidateStrip(Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        Invalidate(r);
    }

    private void SetSelection(Rectangle rect)
    {
        if (_haveSelection) InvalidateRectEdges(_selectionRectClient);
        _selectionRectClient = rect;
        _haveSelection = true;
        InvalidateRectEdges(rect);
    }

    private void InvalidateRectEdges(Rectangle r)
    {
        int t = _borderThickness + 1;
        InvalidateStrip(new Rectangle(r.Left - t, r.Top - t, r.Width + 2 * t, 2 * t));
        InvalidateStrip(new Rectangle(r.Left - t, r.Bottom - t, r.Width + 2 * t, 2 * t));
        InvalidateStrip(new Rectangle(r.Left - t, r.Top - t, 2 * t, r.Height + 2 * t));
        InvalidateStrip(new Rectangle(r.Right - t, r.Top - t, 2 * t, r.Height + 2 * t));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Skip BackColor fill — the backdrop screenshot is painted explicitly in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_memDc != IntPtr.Zero)
        {
            // Read the update-region scanlines BEFORE GetHdc — once we've checked out
            // the HDC, the managed Graphics is locked and any property access (Clip,
            // etc.) throws "Object is currently in use elsewhere".
            using var matrix = new Matrix();
            var scans = e.Graphics.Clip.GetRegionScans(matrix);

            // Iterate the actual update-region scanlines — covers both my explicit
            // Invalidate() calls AND implicit invalidations (first show, expose, etc.)
            // while only BitBlt'ing the thin strips (not the whole bounding box of a
            // far cursor jump).
            var hdc = e.Graphics.GetHdc();
            foreach (var scan in scans)
            {
                int x = (int)scan.X;
                int y = (int)scan.Y;
                int w = (int)scan.Width;
                int h = (int)scan.Height;
                BitBlt(hdc, x, y, w, h, _memDc, x, y, SRCCOPY);
            }
            e.Graphics.ReleaseHdc(hdc);
        }

        e.Graphics.SmoothingMode = SmoothingMode.None;
        if (_haveCrosshair)
        {
            // Restrict the clip region to just the *current* crosshair strips before
            // drawing. Otherwise DrawLine gets clipped to the full update region,
            // which includes the *old* strips we just BitBlt'd clean — and the line
            // would leave 3px residuals at (NX, OY) and (OX, NY) that show up as
            // a tiny cross chasing the cursor across its previous row/column.
            var oldClip = e.Graphics.Clip;
            using var newClip = new Region(new Rectangle(_monLeft, _cursorY - 1, Math.Max(0, _monRight - _monLeft), 3));
            newClip.Union(new Rectangle(_cursorX - 1, _monTop, 3, Math.Max(0, _monBottom - _monTop)));
            e.Graphics.Clip = newClip;
            e.Graphics.DrawLine(_crosshairPen, _monLeft, _cursorY, _monRight, _cursorY);
            e.Graphics.DrawLine(_crosshairPen, _cursorX, _monTop, _cursorX, _monBottom);
            e.Graphics.Clip = oldClip;
            oldClip.Dispose();
        }
        // Only draw the rectangle during an actual drag. Stage 1 also sets a fake
        // _selectionRectClient every frame for timing-smoothness reasons (see OnMouseMove);
        // _dragging is the flag that means "this is a real user selection".
        if (_dragging && _haveSelection && _selectionRectClient.Width > 0 && _selectionRectClient.Height > 0)
            e.Graphics.DrawRectangle(_borderPen, _selectionRectClient);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Under mouse capture, Windows does NOT send WM_SETCURSOR, so the Form.Cursor
        // property never gets re-applied. Without re-setting it here, the pointer keeps
        // whatever shape some background window most recently requested (hourglass,
        // resize arrow, etc.). Setting Cursor.Current on every move keeps ours stuck.
        Cursor.Current = _crossCursor;
        UpdateCrosshair(e.X, e.Y, Left + e.X, Top + e.Y);
        Rectangle rect;
        if (_dragging)
        {
            rect = Rectangle.FromLTRB(
                Math.Min(_dragStartClient.X, e.X), Math.Min(_dragStartClient.Y, e.Y),
                Math.Max(_dragStartClient.X, e.X), Math.Max(_dragStartClient.Y, e.Y));
        }
        else
        {
            // Empirically: tracking an invisible 100x100 rectangle around the cursor
            // produces visibly smoother crosshair motion than just invalidating the
            // two thin monitor-spanning crosshair strips. Best guess is that the
            // extra rectangle-edge strips push the update region past a complexity
            // threshold where WinForms/GDI+/DWM switches to a paint path that aligns
            // frame pickup with vsync — without them, paints complete well under a
            // vsync period and DWM composites variable-latency frames. No pixels
            // change visibly; this invalidation is purely for timing.
            rect = new Rectangle(e.X - 50, e.Y - 50, 100, 100);
        }
        SetSelection(rect);
        Update();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStartClient = new Point(e.X, e.Y);
            SetSelection(new Rectangle(e.X, e.Y, 0, 0));
            Capture = true;
            // Re-evaluate crosshair visibility — in FirstPoint mode the crosshair
            // needs to be erased when the drag starts.
            UpdateCrosshair(e.X, e.Y, Left + e.X, Top + e.Y);
        }
        else if (e.Button == MouseButtons.Right)
        {
            _rightCaptured = true;
            Capture = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && _rightCaptured)
        {
            _rightCaptured = false;
            Capture = false;
            Settle(true);
            return;
        }
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;

        var startScreenX = _dragStartClient.X + Left;
        var startScreenY = _dragStartClient.Y + Top;
        var endScreenX = e.X + Left;
        var endScreenY = e.Y + Top;
        var leftPx = Math.Min(startScreenX, endScreenX);
        var topPx = Math.Min(startScreenY, endScreenY);
        var rightPx = Math.Max(startScreenX, endScreenX);
        var bottomPx = Math.Max(startScreenY, endScreenY);

        var vs = SystemInformation.VirtualScreen;
        leftPx = Math.Clamp(leftPx, vs.Left, vs.Right);
        topPx = Math.Clamp(topPx, vs.Top, vs.Bottom);
        rightPx = Math.Clamp(rightPx, vs.Left, vs.Right);
        bottomPx = Math.Clamp(bottomPx, vs.Top, vs.Bottom);

        var w = rightPx - leftPx;
        var h = bottomPx - topPx;
        if (w < MinDragPx || h < MinDragPx)
            Settle(true);
        else
            Settle(false, leftPx, topPx, w, h);
    }

    private void Settle(bool cancelled, int left = 0, int top = 0, int width = 0, int height = 0)
    {
        if (_settled) return;
        _settled = true;
        Close();
        if (cancelled) Cancelled?.Invoke();
        else Selected?.Invoke(left, top, width, height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _borderPen.Dispose();
            _crosshairPen.Dispose();
            _backdrop?.Dispose();
            _crossCursor.Dispose();
        }
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_backdropHBitmap != IntPtr.Zero)
        {
            DeleteObject(_backdropHBitmap);
            _backdropHBitmap = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Cursor CreateColorCrossCursor()
    {
        const int size = 32;
        const int hotspot = size / 2;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            // White outline (3px) for contrast against dark backgrounds.
            using (var outline = new Pen(Color.White, 3))
            {
                g.DrawLine(outline, hotspot, 0, hotspot, size - 1);
                g.DrawLine(outline, 0, hotspot, size - 1, hotspot);
            }
            // Black inner (1px) so the cross is visible on light backgrounds too.
            using (var inner = new Pen(Color.Black, 1))
            {
                g.DrawLine(inner, hotspot, 0, hotspot, size - 1);
                g.DrawLine(inner, 0, hotspot, size - 1, hotspot);
            }
        }

        // Bitmap → HICON (default hotspot 0,0) → ICONINFO → override hotspot → HCURSOR.
        var hIcon = bmp.GetHicon();
        GetIconInfo(hIcon, out var info);
        info.fIcon = false;
        info.xHotspot = hotspot;
        info.yHotspot = hotspot;
        var hCursor = CreateIconIndirect(ref info);

        DestroyIcon(hIcon);
        if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);

        return new Cursor(hCursor);
    }
}
