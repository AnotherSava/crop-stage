using System.IO;
using CropStage.Features.SizingFrame;
using Xunit;

namespace CropStage.Tests;

public class ScreenshotCaptureTests
{
    [Fact]
    public void UntakenPathReturnedUnchanged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var path = Path.Combine(tmp, "frame.png");
            Assert.Equal(path, ScreenshotCapture.DisambiguatePath(path));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void TakenPathGetsSuffix()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var taken = Path.Combine(tmp, "frame.png");
            File.WriteAllBytes(taken, Array.Empty<byte>());
            Assert.Equal(Path.Combine(tmp, "frame_1.png"), ScreenshotCapture.DisambiguatePath(taken));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void MultipleCollisionsIncrementSuffix()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "frame.png"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(tmp, "frame_1.png"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(tmp, "frame_2.png"), Array.Empty<byte>());
            Assert.Equal(Path.Combine(tmp, "frame_3.png"), ScreenshotCapture.DisambiguatePath(Path.Combine(tmp, "frame.png")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
