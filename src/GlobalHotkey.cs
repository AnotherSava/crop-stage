using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CropStage;

public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HotkeyWindow _window;
    private readonly int _id;
    private bool _registered;
    private bool _disposed;

    public bool IsRegistered => _registered;

    public GlobalHotkey(int id, string hotkeyString, Action onPressed)
    {
        _id = id;
        _window = new HotkeyWindow(onPressed);
        _window.CreateHandle(new CreateParams());

        var (modifiers, vk) = ParseHotkeyString(hotkeyString);
        if (vk == 0)
            _registered = false;
        else
            _registered = RegisterHotKey(_window.Handle, _id, modifiers | MOD_NOREPEAT, vk);
    }

    internal static (uint modifiers, uint vk) ParseHotkeyString(string hotkey)
    {
        uint modifiers = 0;
        uint vk = 0;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0]))
                        vk = (uint)Keys.D0 + (uint)(part[0] - '0');
                    else if (Enum.TryParse<Keys>(part, ignoreCase: true, out var key))
                        vk = (uint)key;
                    break;
            }
        }

        return (modifiers, vk);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            UnregisterHotKey(_window.Handle, _id);
            _registered = false;
        }
        _window.DestroyHandle();
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action _onHotkey;

        public HotkeyWindow(Action onHotkey) => _onHotkey = onHotkey;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                _onHotkey();
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
