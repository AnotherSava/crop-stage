using System.IO;
using Xunit;

namespace CropStage.Tests;

public class AppConfigTests
{
    [Fact]
    public void HexColorParses()
    {
        Assert.True(AppConfig.TryParseHexColor("#FF0000", out var c));
        Assert.Equal(((byte)0xFF, (byte)0x00, (byte)0x00), c);
    }

    [Fact]
    public void HexColorWithoutHashParses()
    {
        Assert.True(AppConfig.TryParseHexColor("00AAFF", out var c));
        Assert.Equal(((byte)0x00, (byte)0xAA, (byte)0xFF), c);
    }

    [Fact]
    public void MalformedHexColorRejected()
    {
        Assert.False(AppConfig.TryParseHexColor("red", out _));
        Assert.False(AppConfig.TryParseHexColor("#FFF", out _));
        Assert.False(AppConfig.TryParseHexColor("#GGGGGG", out _));
    }

    [Fact]
    public void MissingShortcutReportsError()
    {
        var data = new SettingsData
        {
            FrameToggleShortcut = "",
            FrameBorderColor = "#FF0000",
            FrameBorderThickness = 2,
            DefaultFrameWidth = 1280,
            DefaultFrameHeight = 800,
            DefaultScreenshotFolder = "%USERPROFILE%\\Pictures",
            DefaultScreenshotFilename = "frame.png"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => AppConfig.Validate(data));
        Assert.Contains("frameToggleShortcut", ex.Message);
    }

    [Fact]
    public void NegativeDimensionReportsError()
    {
        var data = new SettingsData
        {
            FrameToggleShortcut = "Ctrl+Shift+0",
            FrameBorderColor = "#FF0000",
            FrameBorderThickness = 2,
            DefaultFrameWidth = -1,
            DefaultFrameHeight = 800,
            DefaultScreenshotFolder = "%USERPROFILE%\\Pictures",
            DefaultScreenshotFilename = "frame.png"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => AppConfig.Validate(data));
        Assert.Contains("defaultFrameWidth", ex.Message);
    }

    [Fact]
    public void LoadsFromFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"crop-stage-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var configPath = Path.Combine(tmp, "config.json");
            File.WriteAllText(configPath, """
            {
              "frameToggleShortcut": "Ctrl+Shift+1",
              "screenshotShortcut": "PrintScreen",
              "frameBorderColor": "#00FF00",
              "frameBorderThickness": 3,
              "defaultFrameWidth": 640,
              "defaultFrameHeight": 480,
              "defaultScreenshotFolder": "%TEMP%",
              "defaultScreenshotFilename": "snap.png"
            }
            """);
            var cfg = new AppConfig(configPath);
            Assert.Equal("Ctrl+Shift+1", cfg.FrameToggleShortcut);
            Assert.Equal("PrintScreen", cfg.ScreenshotShortcut);
            Assert.Equal("#00FF00", cfg.FrameBorderColor);
            Assert.Equal(3, cfg.FrameBorderThickness);
            Assert.Equal(640, cfg.DefaultFrameWidth);
            Assert.Equal(480, cfg.DefaultFrameHeight);
            Assert.Equal("%TEMP%", cfg.DefaultScreenshotFolder);
            Assert.Equal("snap.png", cfg.DefaultScreenshotFilename);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
