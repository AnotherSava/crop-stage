using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CropStage.Features.SizingFrame;

namespace CropStage.Features.AreaSelect;

/// <summary>
/// Draws the quick-save clipboard hint as a rounded pill: a blue status dot, a "To clipboard"
/// label, the effective target shown as a filled blue pill, and the Shift alternative shown as
/// dim "⇧ &lt;name&gt;" text. Holding Shift moves the blue-pill highlight to the alternative.
///
/// Text is drawn with GDI <see cref="TextRenderer"/> (not GDI+ <c>Graphics.DrawString</c>): GDI
/// gives crisp, correctly-hinted ClearType at high DPI, and DT_VCENTER|DT_SINGLELINE centres text
/// reliably — GDI+ text renders soft and mis-centres with typographic string formats. All metrics
/// scale by the DPI of the monitor the hint is shown on.
/// </summary>
internal sealed class ClipboardHintPainter : IDisposable
{
    private const string Label = "To clipboard";
    private const string ShiftGlyph = "⇧"; // ⇧ UPWARDS WHITE ARROW

    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
    private const TextFormatFlags DrawLeftFlags =
        MeasureFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoClipping;
    private const TextFormatFlags DrawCenterFlags =
        MeasureFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoClipping;

    private readonly string _baseText;
    private readonly string _altText;

    private readonly SolidBrush _containerBrush = new(Color.FromArgb(255, 42, 43, 48));
    private readonly Pen _borderPen = new(Color.FromArgb(64, 255, 255, 255), 1f);
    private readonly SolidBrush _accentBrush = new(Color.FromArgb(255, 47, 128, 237)); // blue dot + active pill

    private readonly Color _labelColor = Color.FromArgb(255, 232, 233, 238);
    private readonly Color _activeColor = Color.FromArgb(255, 255, 255, 255);
    private readonly Color _dimColor = Color.FromArgb(255, 168, 170, 178);
    private readonly Color _glyphDimColor = Color.FromArgb(255, 200, 202, 208);

    private float _scale = -1f;
    private Font? _font;      // label + option words (semibold)
    private Font? _glyphFont; // the ⇧ symbol

    // Layout, recomputed in Layout(); drawn verbatim in Paint().
    private Size _labelSz, _glyphSz, _altWordSz;
    private Rectangle _dotRect, _slotBase, _slotAlt;
    private int _labelX, _glyphGap;
    private int _pillRadius, _containerRadius;

    public Rectangle PanelRect { get; private set; }

    public ClipboardHintPainter(ClipboardMode baseMode)
    {
        _baseText = ModeWord(baseMode);
        _altText = ModeWord(AreaSelectOverlay.ShiftAlternative(baseMode));
    }

    /// <summary>Measures content and centres the pill horizontally on the given monitor, anchored
    /// near its top (or bottom when <paramref name="atBottom"/>). All coordinates are the overlay's
    /// client space. Updates <see cref="PanelRect"/>.</summary>
    public void Layout(Graphics g, float scale, int monLeft, int monTop, int monRight, int monBottom, bool atBottom)
    {
        if (scale != _scale) { RebuildFonts(scale); _scale = scale; }
        _borderPen.Width = Math.Max(1f, scale);

        int padX = R(16 * scale);
        int dot = R(9 * scale);
        int gapDotLabel = R(9 * scale);
        int gapLabelOpts = R(14 * scale);
        int pillPadX = R(11 * scale);
        int pillPadY = R(5 * scale);
        int slotGap = R(6 * scale);
        int containerPadY = R(7 * scale);
        _glyphGap = R(3 * scale);

        _labelSz = Measure(g, Label, _font!);
        var baseSz = Measure(g, _baseText, _font!);
        _glyphSz = Measure(g, ShiftGlyph, _glyphFont!);
        _altWordSz = Measure(g, _altText, _font!);

        int wordH = Math.Max(baseSz.Height, _altWordSz.Height);
        int pillH = wordH + 2 * pillPadY;
        int containerH = pillH + 2 * containerPadY;
        _pillRadius = pillH / 2;
        _containerRadius = containerH / 2;

        int slotBaseW = baseSz.Width + 2 * pillPadX;
        int altContentW = _glyphSz.Width + _glyphGap + _altWordSz.Width;
        int slotAltW = altContentW + 2 * pillPadX;

        int panelW = padX + dot + gapDotLabel + _labelSz.Width + gapLabelOpts
                     + slotBaseW + slotGap + slotAltW + padX;
        int panelH = containerH;

        int monWidth = monRight - monLeft;
        int px = monLeft + (monWidth - panelW) / 2;
        if (px < monLeft) px = monLeft;
        int margin = R(36 * scale);
        int py = atBottom ? monBottom - panelH - margin : monTop + margin;
        PanelRect = new Rectangle(px, py, panelW, panelH);

        int cy = py + panelH / 2;
        int x = px + padX;
        _dotRect = new Rectangle(x, cy - dot / 2, dot, dot);
        x += dot + gapDotLabel;
        _labelX = x;
        x += _labelSz.Width + gapLabelOpts;
        _slotBase = new Rectangle(x, cy - pillH / 2, slotBaseW, pillH);
        x += slotBaseW + slotGap;
        _slotAlt = new Rectangle(x, cy - pillH / 2, slotAltW, pillH);
    }

    /// <summary>Draws the pill. <paramref name="shiftDown"/> highlights the Shift alternative;
    /// otherwise the base target is highlighted.</summary>
    public void Paint(Graphics g, bool shiftDown)
    {
        if (PanelRect.Width <= 0 || _font == null) return;

        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedRect(PanelRect, _containerRadius))
        {
            g.FillPath(_containerBrush, path);
            g.DrawPath(_borderPen, path);
        }
        g.FillEllipse(_accentBrush, _dotRect);
        if (!shiftDown)
            using (var p = RoundedRect(_slotBase, _pillRadius)) g.FillPath(_accentBrush, p);
        else
            using (var p = RoundedRect(_slotAlt, _pillRadius)) g.FillPath(_accentBrush, p);
        g.SmoothingMode = prevSmoothing;

        // Text last, via GDI TextRenderer (crisp + reliable vertical centering).
        var labelRect = new Rectangle(_labelX, PanelRect.Y, _labelSz.Width, PanelRect.Height);
        TextRenderer.DrawText(g, Label, _font!, labelRect, _labelColor, DrawLeftFlags);

        TextRenderer.DrawText(g, _baseText, _font!, _slotBase, shiftDown ? _dimColor : _activeColor, DrawCenterFlags);

        int contentW = _glyphSz.Width + _glyphGap + _altWordSz.Width;
        int gx = _slotAlt.X + (_slotAlt.Width - contentW) / 2;
        var glyphRect = new Rectangle(gx, _slotAlt.Y, _glyphSz.Width, _slotAlt.Height);
        TextRenderer.DrawText(g, ShiftGlyph, _glyphFont!, glyphRect, shiftDown ? _activeColor : _glyphDimColor, DrawLeftFlags);
        var wordRect = new Rectangle(gx + _glyphSz.Width + _glyphGap, _slotAlt.Y, _altWordSz.Width, _slotAlt.Height);
        TextRenderer.DrawText(g, _altText, _font!, wordRect, shiftDown ? _activeColor : _dimColor, DrawLeftFlags);
    }

    private void RebuildFonts(float scale)
    {
        _font?.Dispose();
        _glyphFont?.Dispose();
        _font = new Font("Segoe UI Semibold", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _glyphFont = new Font("Segoe UI Symbol", 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static Size Measure(Graphics g, string text, Font font) =>
        TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), MeasureFlags);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static int R(float v) => (int)Math.Round(v);

    private static string ModeWord(ClipboardMode mode) => mode switch
    {
        ClipboardMode.Image => "Image",
        ClipboardMode.Path => "Path",
        _ => "Nothing",
    };

    public void Dispose()
    {
        _containerBrush.Dispose();
        _borderPen.Dispose();
        _accentBrush.Dispose();
        _font?.Dispose();
        _glyphFont?.Dispose();
    }
}
