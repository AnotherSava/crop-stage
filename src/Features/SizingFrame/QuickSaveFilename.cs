using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CropStage.Features.SizingFrame;

internal static class QuickSaveFilename
{
    internal const int MaxTitleChars = 60;

    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled);

    internal const string TimestampFormat = "yyyy-MM-dd HH-mm";

    internal static string Build(string? label, CaptureFormat format) =>
        Build(DateTime.Now.ToString(TimestampFormat), label, format);

    internal static string Build(string timestamp, string? label, CaptureFormat format)
    {
        var ext = ScreenshotCapture.GetExtension(format);
        var cleaned = Sanitize(label);
        return cleaned.Length == 0
            ? $"{timestamp}{ext}"
            : $"{timestamp} {cleaned}{ext}";
    }

    internal static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || Array.IndexOf(invalid, c) >= 0)
                sb.Append(' ');
            else
                sb.Append(c);
        }

        var collapsed = WhitespaceRuns.Replace(sb.ToString(), " ").Trim();
        if (collapsed.Length > MaxTitleChars)
            collapsed = collapsed.Substring(0, MaxTitleChars).TrimEnd();
        return collapsed;
    }
}
