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

    // Clipboard hint + Shift toggle (quick-save only). The panel shows which of two targets
    // (base / alternative) the capture will copy to the clipboard; holding Shift momentarily
    // flips to the alternative. See ShiftAlternative for the pairing.
    private readonly bool _showClipboardHint;
    private readonly ClipboardMode _baseClipboardMode;
    private ClipboardHintPainter? _hintPainter;
    private System.Windows.Forms.Timer? _shiftTimer;
    // The quick-save hotkey usually contains Shift (default Ctrl+Shift+8), so Shift is still
    // held when the overlay opens. Ignore that leftover hold: only treat Shift as a toggle
    // once it has been released at least once ("armed").
    private bool _shiftArmed;
    private bool _shiftEffectiveDown;
    private Rectangle _hintMon;
    // The hint has two anchors (top / bottom of the monitor). It flips to the far one whenever
    // the cursor approaches its current spot, so it never sits under the pointer.
    private bool _hintAtBottom;
    private float _hintScale = 1f;
    // How close (in DIP-ish px, scaled per monitor) the cursor gets to the panel before it dodges.
    private const int HintDodgeMarginX = 48;
    private const int HintDodgeMarginY = 80;

    /// <summary>The clipboard target chosen during selection: the base tray setting, or its
    /// <see cref="ShiftAlternative"/> while Shift was held. Only meaningful in quick-save mode.</summary>
    public ClipboardMode EffectiveClipboardMode { get; private set; }

    public event Action<int, int, int, int>? Selected;
    public event Action? Cancelled;

    public AreaSelectOverlay(AppConfig config, CrosshairMode crosshairMode, bool showClipboardHint, ClipboardMode baseClipboardMode)
    {
        var color = Color.Red;
        if (AppConfig.TryParseHexColor(config.FrameBorderColor, out var rgb))
            color = Color.FromArgb(rgb.R, rgb.G, rgb.B);
        _borderThickness = Math.Max(1, config.FrameBorderThickness);
        _borderPen = new Pen(color, _borderThickness);
        _crosshairPen = new Pen(color, 1);
        _crosshairMode = crosshairMode;
        _showClipboardHint = showClipboardHint;
        _baseClipboardMode = baseClipboardMode;
        EffectiveClipboardMode = baseClipboardMode;
        if (showClipboardHint)
            _hintPainter = new ClipboardHintPainter(baseClipboardMode);

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
        if (_showClipboardHint)
        {
            // Establish initial arming/effective state (Shift may still be held from the
            // hotkey chord), then poll on a timer so press/release is reflected live even
            // when the cursor is stationary. OnMouseMove is deliberately left untouched —
            // its update-region shaping is timing-sensitive.
            PollShiftState();
            _shiftTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _shiftTimer.Tick += (_, _) => PollShiftState();
            _shiftTimer.Start();
        }
    }

    public void Cancel() => Settle(true);

    /// <summary>The clipboard target Shift flips to, given the base tray setting. Image and
    /// Path are natural opposites; from None, Shift enables copying the Image (so the unpressed
    /// default still honours "don't touch the clipboard").</summary>
    internal static ClipboardMode ShiftAlternative(ClipboardMode baseMode) => baseMode switch
    {
        ClipboardMode.Image => ClipboardMode.Path,
        ClipboardMode.Path => ClipboardMode.Image,
        _ => ClipboardMode.Image,
    };

    private void PollShiftState()
    {
        if (!_showClipboardHint || _settled) return;
        bool down = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        // Arm only after Shift has been observed up at least once, so the Shift left over
        // from a Shift-containing quick-save hotkey doesn't count as a toggle.
        if (!down) _shiftArmed = true;
        bool effectiveDown = down && _shiftArmed;
        if (effectiveDown == _shiftEffectiveDown) return;
        _shiftEffectiveDown = effectiveDown;
        EffectiveClipboardMode = effectiveDown ? ShiftAlternative(_baseClipboardMode) : _baseClipboardMode;
        var pr = _hintPainter?.PanelRect ?? Rectangle.Empty;
        if (pr.Width > 0) { Invalidate(pr); Update(); }
    }

    private void LayoutHint(int screenX, int screenY)
    {
        if (_hintPainter == null || !IsHandleCreated) return;
        _hintScale = GetMonitorScale(screenX, screenY);
        var old = _hintPainter.PanelRect;
        using (var g = CreateGraphics())
            _hintPainter.Layout(g, _hintScale, _monLeft, _monTop, _monRight, _monBottom, _hintAtBottom);
        var now = _hintPainter.PanelRect;
        // Panel moved (monitor change or anchor flip): restore the backdrop where it used to be,
        // paint the new spot.
        if (old.Width > 0 && old != now) InvalidateStrip(old);
        if (now.Width > 0) InvalidateStrip(now);
    }

    private static float GetMonitorScale(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96f;
        return 1f;
    }

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

        if (_showClipboardHint)
            UpdateHintPlacement(screenX, screenY);
    }

    private void UpdateHintPlacement(int screenX, int screenY)
    {
        // Re-lay the hint when the cursor crosses to a different monitor (its size and centering
        // depend on that monitor's bounds and DPI).
        var monRect = new Rectangle(_monLeft, _monTop, _monRight - _monLeft, _monBottom - _monTop);
        if (monRect != _hintMon) { _hintMon = monRect; LayoutHint(screenX, screenY); }

        if (_hintPainter == null || _hintPainter.PanelRect.Width <= 0) return;
        // Flip to the other anchor when the cursor enters the panel's inflated trigger zone. Top
        // and bottom are far enough apart that the flip moves the panel clear of the cursor, so a
        // single flip settles it — no oscillation on any realistically-tall monitor.
        var trigger = _hintPainter.PanelRect;
        trigger.Inflate((int)Math.Round(HintDodgeMarginX * _hintScale), (int)Math.Round(HintDodgeMarginY * _hintScale));
        if (trigger.Contains(_cursorX, _cursorY))
        {
            _hintAtBottom = !_hintAtBottom;
            LayoutHint(screenX, screenY);
        }
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
        // Redraw the clipboard hint panel whenever the update region touches it. We restore
        // the full backdrop under the whole panel (below) and repaint the whole panel (last),
        // so the result is idempotent regardless of how much of it the update region covers.
        var panelRect = _hintPainter?.PanelRect ?? Rectangle.Empty;
        bool paintPanel = _showClipboardHint && _hintPainter != null
            && panelRect.Width > 0 && panelRect.Height > 0
            && e.ClipRectangle.IntersectsWith(panelRect);

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
            // Lay a clean backdrop under the entire panel before it (and any crosshair/
            // selection line crossing it) is painted, so the opaque panel always sits on
            // top of a pristine background — no accumulation of anti-aliased edges.
            if (paintPanel)
                BitBlt(hdc, panelRect.X, panelRect.Y, panelRect.Width, panelRect.Height, _memDc, panelRect.X, panelRect.Y, SRCCOPY);
            e.Graphics.ReleaseHdc(hdc);
        }

        e.Graphics.SmoothingMode = SmoothingMode.None;
        // Keep the crosshair and selection lines out of the panel entirely. The panel body is
        // an opaque rounded rect, so its bounding-box corners stay transparent — without this,
        // a crosshair/selection line drawn (before the panel) over the clean backdrop laid under
        // the panel would poke through those corner notches, since the panel repaints only its
        // rounded shape.
        if (paintPanel) e.Graphics.ExcludeClip(panelRect);
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
            // Setting Clip replaces (doesn't intersect) the ambient exclude above, so re-apply it.
            if (paintPanel) newClip.Exclude(panelRect);
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

        // Painted last so it sits above the crosshair/selection lines. SetClip replaces the
        // ambient exclude with the panel's own rect, so the panel paints in full.
        if (paintPanel)
        {
            var prevClip = e.Graphics.Clip;
            e.Graphics.SetClip(panelRect);
            _hintPainter!.Paint(e.Graphics, _shiftEffectiveDown);
            e.Graphics.Clip = prevClip;
            prevClip.Dispose();
        }
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
        // Capture Shift exactly at release, so the effective clipboard target isn't up to one
        // timer tick stale when it's read back after Settle.
        if (_showClipboardHint) PollShiftState();

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
        _shiftTimer?.Stop();
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
            _shiftTimer?.Dispose();
            _hintPainter?.Dispose();
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

    private const int VK_SHIFT = 0x10;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

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
