using CropStage.Features.SizingFrame;
using Xunit;

namespace CropStage.Tests;

public class QuickSaveFilenameTests
{
    [Fact]
    public void SanitizeReturnsEmptyForNullOrBlank()
    {
        Assert.Equal("", QuickSaveFilename.Sanitize(null));
        Assert.Equal("", QuickSaveFilename.Sanitize(""));
        Assert.Equal("", QuickSaveFilename.Sanitize("   "));
        Assert.Equal("", QuickSaveFilename.Sanitize("\t \r\n"));
    }

    [Fact]
    public void SanitizePreservesOrdinaryTitle()
    {
        Assert.Equal("Google Chrome", QuickSaveFilename.Sanitize("Google Chrome"));
        Assert.Equal("Visual Studio Code", QuickSaveFilename.Sanitize("Visual Studio Code"));
    }

    [Fact]
    public void SanitizeReplacesInvalidFilenameCharsWithSpace()
    {
        Assert.Equal("foo bar baz", QuickSaveFilename.Sanitize("foo<bar>:baz?"));
        Assert.Equal("path with pipe", QuickSaveFilename.Sanitize("path | with pipe"));
        Assert.Equal("Section Topic Site", QuickSaveFilename.Sanitize("Section: Topic | Site"));
    }

    [Fact]
    public void SanitizeCollapsesWhitespaceRuns()
    {
        Assert.Equal("a b c", QuickSaveFilename.Sanitize("a   b\tc"));
        Assert.Equal("leading trailing", QuickSaveFilename.Sanitize("   leading    trailing   "));
    }

    [Fact]
    public void SanitizeCollapsesAdjacentStrippedCharsIntoOneSpace()
    {
        Assert.Equal("a b", QuickSaveFilename.Sanitize("a<<>>b"));
        Assert.Equal("a b", QuickSaveFilename.Sanitize("a \t | \r\n b"));
    }

    [Fact]
    public void SanitizeReplacesControlCharsWithSpace()
    {
        Assert.Equal("a b c", QuickSaveFilename.Sanitize("a\u0001b\u0002c"));
    }

    [Fact]
    public void SanitizeTruncatesLongTitle()
    {
        var input = new string('x', 200);
        var result = QuickSaveFilename.Sanitize(input);
        Assert.Equal(QuickSaveFilename.MaxTitleChars, result.Length);
    }

    [Fact]
    public void SanitizeTrimsTrailingSpaceAfterTruncation()
    {
        var input = new string('a', 59) + "  b";
        var result = QuickSaveFilename.Sanitize(input);
        Assert.Equal(59, result.Length);
        Assert.Equal('a', result[^1]);
    }

    [Fact]
    public void BuildReturnsBareTimestampWhenLabelMissing()
    {
        Assert.Equal("2026-04-20 14-35.png", QuickSaveFilename.Build("2026-04-20 14-35", null));
        Assert.Equal("2026-04-20 14-35.png", QuickSaveFilename.Build("2026-04-20 14-35", ""));
        Assert.Equal("2026-04-20 14-35.png", QuickSaveFilename.Build("2026-04-20 14-35", "   "));
    }

    [Fact]
    public void BuildAppendsLabelWithSingleSpaceSeparator()
    {
        Assert.Equal(
            "2026-04-20 14-35 chrome Google Chrome.png",
            QuickSaveFilename.Build("2026-04-20 14-35", "chrome Google Chrome"));
    }

    [Fact]
    public void BuildSanitizesLabelInResult()
    {
        Assert.Equal(
            "2026-04-20 14-35 bad chars.png",
            QuickSaveFilename.Build("2026-04-20 14-35", "bad<>chars"));
    }

    [Fact]
    public void BuildReturnsBareTimestampWhenLabelIsOnlyInvalidChars()
    {
        Assert.Equal("2026-04-20 14-35.png", QuickSaveFilename.Build("2026-04-20 14-35", "<<>>||??"));
    }
}
