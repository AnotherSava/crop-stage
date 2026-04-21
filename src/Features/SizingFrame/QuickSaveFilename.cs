using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CropStage.Features.SizingFrame;

internal static class QuickSaveFilename
{
    internal const int MaxTitleChars = 60;

    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled);

    internal static string Build(string timestamp, string? label)
    {
        var cleaned = Sanitize(label);
        return cleaned.Length == 0
            ? $"{timestamp}.png"
            : $"{timestamp} {cleaned}.png";
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
