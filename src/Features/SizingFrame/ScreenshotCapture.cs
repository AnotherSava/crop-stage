using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace CropStage.Features.SizingFrame;

public static class ScreenshotCapture
{
    /// <summary>
    /// Captures a rectangular region from the desktop and saves it as a PNG.
    /// Returns the path actually written (disambiguated if the target existed).
    /// Coordinates are in physical pixels.
    /// </summary>
    public static string Capture(int x, int y, int width, int height, string folder, string filename)
    {
        Directory.CreateDirectory(folder);
        filename = Path.GetFileName(filename);
        var targetPath = DisambiguatePath(Path.Combine(folder, filename));

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        bmp.Save(targetPath, ImageFormat.Png);
        return targetPath;
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
