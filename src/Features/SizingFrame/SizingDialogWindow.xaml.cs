using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CropStage.Features.SizingFrame;

public sealed class DragStartingEventArgs : EventArgs
{
    public int? WarpToScreenX { get; set; }
    public int? WarpToScreenY { get; set; }
}

public partial class SizingDialogWindow : Window
{
    private const double FilenameBoxExpandedWidth = 192;
    private const double FilenameBoxCompactWidth = 154;

    private bool _isCompact;
    private string _screenshotTip = "Screenshot";
    private const string OffScreenTip = "Frame is partly off-screen";

    private bool _dragging;
    private int _dragAnchorOffsetX;
    private int _dragAnchorOffsetY;
    private int _dragReturnOffsetX;
    private int _dragReturnOffsetY;

    public event EventHandler? CommitRequested;
    public event EventHandler? DimensionsChanged;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? BrowseRequested;
    public event EventHandler? CompactModeChanged;
    public event EventHandler<DragStartingEventArgs>? DragStarting;

    public SizingDialogWindow()
    {
        InitializeComponent();

        WidthBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        HeightBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        FolderBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        FilenameBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);

        // Live frame resize while typing — separate from CommitRequested so we
        // don't write state.json on every keystroke.
        WidthBox.TextChanged += (_, _) => DimensionsChanged?.Invoke(this, EventArgs.Empty);
        HeightBox.TextChanged += (_, _) => DimensionsChanged?.Invoke(this, EventArgs.Empty);

        ScreenshotButton.Click += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);
        BrowseButton.Click += (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty);
        ExpandedToggleButton.Click += (_, _) => ToggleCompactMode();
        CompactToggleButton.Click += (_, _) => ToggleCompactMode();

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Frameless window: drag from any non-input element (labels, background, border).
        // Double-click on same areas toggles compact mode.
        if (e.OriginalSource is TextBlock or Border or Grid or Window)
        {
            if (e.ClickCount == 2)
            {
                ToggleCompactMode();
            }
            else
            {
                var args = new DragStartingEventArgs();
                DragStarting?.Invoke(this, args);
                StartCustomDrag(args.WarpToScreenX!.Value, args.WarpToScreenY!.Value);
            }
        }
    }

    /// <summary>
    /// Custom drag loop driven by WPF mouse events + CaptureMouse. Does NOT use
    /// DragMove/SC_MOVE because that loop's anchor is sampled from the mouse message
    /// queue (pre-warp cursor position), which produces a composite shift equal to
    /// the warp delta on the first tick. Here we own the anchor directly.
    /// </summary>
    private void StartCustomDrag(int warpX, int warpY)
    {
        var dpi = AppUtilities.GetDpiScaleForWindow(this);
        var windowScreenX = (int)Math.Round(Left * dpi);
        var windowScreenY = (int)Math.Round(Top * dpi);
        _dragAnchorOffsetX = warpX - windowScreenX;
        _dragAnchorOffsetY = warpY - windowScreenY;
        // Remember the click's offset from the warp point (equivalently, from the frame's
        // bottom-left interior pixel). On drag end we warp the cursor back by this same
        // offset so it reappears at the same relative position on the moved composite.
        if (GetCursorPos(out var origin))
        {
            _dragReturnOffsetX = origin.X - warpX;
            _dragReturnOffsetY = origin.Y - warpY;
        }
        else
        {
            _dragReturnOffsetX = 0;
            _dragReturnOffsetY = 0;
        }
        _dragging = true;
        // Hide the cursor BEFORE warping so the jump isn't visually perceptible.
        // OverrideCursor alone lags: WPF only reapplies it on the next WM_SETCURSOR,
        // which SetCursorPos itself triggers — so the cursor flashes along the warp
        // path. ShowCursor(false) forces immediate Win32-level hiding.
        ShowCursor(false);
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
        Opacity = 0;
        SetCursorPos(warpX, warpY);
        CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!GetCursorPos(out var pt)) return;
        var dpi = AppUtilities.GetDpiScaleForWindow(this);
        var newLeft = (pt.X - _dragAnchorOffsetX) / dpi;
        var newTop = (pt.Y - _dragAnchorOffsetY) / dpi;
        if (Math.Abs(Left - newLeft) > 0.01) Left = newLeft;
        if (Math.Abs(Top - newTop) > 0.01) Top = newTop;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) EndCustomDrag();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) EndCustomDrag();
    }

    private void EndCustomDrag()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // Warp the cursor back to its pre-drag offset against the frame's bottom-left
        // interior pixel BEFORE clearing OverrideCursor — otherwise the pointer briefly
        // flashes at the warp point before it moves.
        if (GetCursorPos(out var pt))
            SetCursorPos(pt.X + _dragReturnOffsetX, pt.Y + _dragReturnOffsetY);
        System.Windows.Input.Mouse.OverrideCursor = null;
        ShowCursor(true);
        Opacity = 1;
    }

    private void ToggleCompactMode()
    {
        ApplyCompactVisuals(!_isCompact);
        CompactModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCompactMode(bool compact)
    {
        if (_isCompact == compact) return;
        ApplyCompactVisuals(compact);
    }

    private void ApplyCompactVisuals(bool compact)
    {
        _isCompact = compact;
        var expandedVis = compact ? Visibility.Collapsed : Visibility.Visible;
        var compactVis = compact ? Visibility.Visible : Visibility.Collapsed;
        SizeLabel.Visibility = expandedVis;
        SizeFields.Visibility = expandedVis;
        ExpandedToggleButton.Visibility = expandedVis;
        FolderLabel.Visibility = expandedVis;
        FolderBox.Visibility = expandedVis;
        BrowseButton.Visibility = expandedVis;
        CompactToggleButton.Visibility = compactVis;
        FilenameBox.Width = compact ? FilenameBoxCompactWidth : FilenameBoxExpandedWidth;
    }

    public void SetScreenshotShortcut(string shortcut)
    {
        _screenshotTip = string.IsNullOrWhiteSpace(shortcut) ? "Screenshot" : $"Screenshot ({shortcut})";
        if (ScreenshotButton.IsEnabled) ScreenshotButton.ToolTip = _screenshotTip;
    }

    public void SetScreenshotEnabled(bool enabled)
    {
        ScreenshotButton.IsEnabled = enabled;
        ScreenshotButton.ToolTip = enabled ? _screenshotTip : OffScreenTip;
    }

    public int WidthValue => int.TryParse(WidthBox.Text, out var v) ? v : 0;
    public int HeightValue => int.TryParse(HeightBox.Text, out var v) ? v : 0;
    public string FolderValue => FolderBox.Text;
    public string FilenameValue => FilenameBox.Text;

    public void SetFields(int width, int height, string folder, string filename)
    {
        WidthBox.Text = width.ToString();
        HeightBox.Text = height.ToString();
        FolderBox.Text = folder;
        FilenameBox.Text = filename;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);
}
