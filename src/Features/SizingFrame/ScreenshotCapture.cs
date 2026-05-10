using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace CropStage.Features.SizingFrame;

public static class ScreenshotCapture
{
    /// <summary>
    /// Captures a rectangular region from the desktop and saves it in the requested format.
    /// The supplied filename's extension is replaced with the format's canonical extension.
    /// Returns the path actually written (disambiguated if the target existed).
    /// Coordinates are in physical pixels.
    /// </summary>
    public static string Capture(int x, int y, int width, int height, string folder, string filename, CaptureFormat format)
    {
        Directory.CreateDirectory(folder);
        filename = Path.GetFileName(filename);
        filename = Path.GetFileNameWithoutExtension(filename) + GetExtension(format);
        var targetPath = DisambiguatePath(Path.Combine(folder, filename));

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        if (format == CaptureFormat.Webp)
            SaveAsWebp(bmp, targetPath);
        else
            bmp.Save(targetPath, ImageFormat.Png);

        return targetPath;
    }

    public static string GetExtension(CaptureFormat format) => format switch
    {
        CaptureFormat.Webp => ".webp",
        _ => ".png",
    };

    /// <summary>
    /// Encodes the captured bitmap as a small-file-size WebP (lossy, quality 60, slowest
    /// compression method). Mirrors the toolbox image-opt defaults.
    /// </summary>
    private static void SaveAsWebp(Bitmap bmp, string targetPath)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bmp.Width * 4;
            var bytes = new byte[rowBytes * bmp.Height];
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                for (var row = 0; row < bmp.Height; row++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bytes, row * rowBytes, rowBytes);
            }
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(bytes, bmp.Width, bmp.Height);
            var encoder = new WebpEncoder
            {
                Quality = 60,
                Method = WebpEncodingMethod.BestQuality,
                FileFormat = WebpFileFormatType.Lossy,
            };
            using var fs = File.Create(targetPath);
            image.Save(fs, encoder);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// If <paramref name="path"/> is free, returns it unchanged.
    /// Otherwise, inserts '_1', '_2', ... before the extension until a free name is found.
    /// </summary>
    public static string DisambiguatePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not find a free filename for '{path}'");
    }
}
