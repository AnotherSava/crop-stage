using System.Windows.Forms;
using Xunit;

namespace CropStage.Tests;

public class GlobalHotkeyTests
{
    [Fact]
    public void ParsesCtrlShiftH()
    {
        var (mods, vk) = GlobalHotkey.ParseHotkeyString("Ctrl+Shift+H");
        Assert.Equal(0x0002u | 0x0004u, mods);
        Assert.Equal((uint)Keys.H, vk);
    }

    [Fact]
    public void ParsesDigitKey()
    {
        var (mods, vk) = GlobalHotkey.ParseHotkeyString("Ctrl+Shift+0");
        Assert.Equal(0x0002u | 0x0004u, mods);
        Assert.Equal((uint)Keys.D0, vk);
    }

    [Fact]
    public void ParsesAltAndWinModifiers()
    {
        var (mods, vk) = GlobalHotkey.ParseHotkeyString("Alt+Win+A");
        Assert.Equal(0x0001u | 0x0008u, mods);
        Assert.Equal((uint)Keys.A, vk);
    }

    [Fact]
    public void UnknownKeyReturnsZeroVk()
    {
        var (_, vk) = GlobalHotkey.ParseHotkeyString("Ctrl+Shift+Unobtanium");
        Assert.Equal(0u, vk);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var (mods, vk) = GlobalHotkey.ParseHotkeyString("CTRL+shift+h");
        Assert.Equal(0x0002u | 0x0004u, mods);
        Assert.Equal((uint)Keys.H, vk);
    }
}
