using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CropStage;

public static class AppUtilities
{
    public static Icon LoadOrCreateIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CropStage.icon.ico");
        if (stream != null)
            return new Icon(stream);

        return CreateDefaultIcon();
    }

    private static Icon CreateDefaultIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var fillBrush = new SolidBrush(Color.FromArgb(0x20, 0x60, 0xC0));
        using var borderPen = new Pen(Color.FromArgb(0xA0, 0xC0, 0xFF), 1);
        g.FillRectangle(fillBrush, 2, 2, 11, 11);
        g.DrawRectangle(borderPen, 2, 2, 11, 11);

        using var crossPen = new Pen(Color.White, 1);
        g.DrawLine(crossPen, 7, 4, 7, 10);
        g.DrawLine(crossPen, 4, 7, 10, 7);

        return CloneIconFromHandle(bmp.GetHicon());
    }

    private static Icon CloneIconFromHandle(IntPtr hIcon)
    {
        using var tempIcon = Icon.FromHandle(hIcon);
        var clone = (Icon)tempIcon.Clone();
        DestroyIcon(hIcon);
        return clone;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static double GetPrimaryDpiScale()
    {
        var primary = Screen.PrimaryScreen;
        if (primary == null) return 1.0;
        return System.Windows.SystemParameters.PrimaryScreenHeight > 0
            ? primary.Bounds.Height / System.Windows.SystemParameters.PrimaryScreenHeight
            : 1.0;
    }
}
