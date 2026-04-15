using System.IO;
using System.Text.Json;
using CropStage.Features.SizingFrame;
using Xunit;

namespace CropStage.Tests;

public class FrameStateTests
{
    private static string WriteConfig(string dir)
    {
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
        {
          "frameToggleShortcut": "Ctrl+Shift+0",
          "frameBorderColor": "#FF0000",
          "frameBorderThickness": 2,
          "defaultFrameWidth": 1280,
          "defaultFrameHeight": 800,
          "defaultScreenshotFolder": "%USERPROFILE%\\Pictures",
          "defaultScreenshotFilename": "frame.png"
        }
        """);
        return configPath;
    }

    [Fact]
    public void DefaultsUsedWhenStateMissing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = new AppConfig(WriteConfig(tmp));
            var state = new FrameState(config, Path.Combine(tmp, "state.json"));
            Assert.Equal(1280, state.Width);
            Assert.Equal(800, state.Height);
            Assert.Equal("frame.png", state.Filename);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void WritesStateOnMutation()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var statePath = Path.Combine(tmp, "state.json");
        try
        {
            var config = new AppConfig(WriteConfig(tmp));
            var state = new FrameState(config, statePath);
            state.Width = 1920;
            state.Height = 1080;
            state.Filename = "shot.png";

            Assert.True(File.Exists(statePath));
            var json = File.ReadAllText(statePath);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1920, doc.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(1080, doc.RootElement.GetProperty("height").GetInt32());
            Assert.Equal("shot.png", doc.RootElement.GetProperty("filename").GetString());
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void NoWriteWhenValueUnchanged()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var statePath = Path.Combine(tmp, "state.json");
        try
        {
            var config = new AppConfig(WriteConfig(tmp));
            var state = new FrameState(config, statePath);
            state.Width = 1920;
            Assert.True(File.Exists(statePath));
            var firstWrite = File.GetLastWriteTimeUtc(statePath);

            System.Threading.Thread.Sleep(20);
            state.Width = 1920;

            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(statePath));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void RoundTripsExistingState()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var statePath = Path.Combine(tmp, "state.json");
        try
        {
            File.WriteAllText(statePath, """
            { "width": 800, "height": 600, "folder": "C:\\Temp", "filename": "out.png" }
            """);
            var config = new AppConfig(WriteConfig(tmp));
            var state = new FrameState(config, statePath);
            Assert.Equal(800, state.Width);
            Assert.Equal(600, state.Height);
            Assert.Equal("C:\\Temp", state.Folder);
            Assert.Equal("out.png", state.Filename);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void MalformedStateFallsBackToDefaults()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var statePath = Path.Combine(tmp, "state.json");
        try
        {
            File.WriteAllText(statePath, "{ not valid json");
            var config = new AppConfig(WriteConfig(tmp));
            var state = new FrameState(config, statePath);
            Assert.Equal(1280, state.Width);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
