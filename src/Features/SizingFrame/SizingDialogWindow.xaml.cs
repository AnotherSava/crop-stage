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

public sealed class DragEndedEventArgs : EventArgs
{
    /// <summary>
    /// Original click offset within the dialog, in physical pixels. The handler
    /// uses this to drop the cursor back onto the dialog after re-evaluating
    /// placement, even if the dialog moved between outside-below and inside-frame.
    /// </summary>
    public int ClickOffsetInDialogX { get; init; }
    public int ClickOffsetInDialogY { get; init; }
}

public partial class SizingDialogWindow : Window
{
    private const double FilenameBoxRegularWidth = 192;
    private const double FilenameBoxFilenameOnlyWidth = 154;

    private DialogMode _mode = DialogMode.Regular;
    private string _screenshotTip = "Screenshot";
    private const string OffScreenTip = "Frame is partly off-screen";

    private bool _dragging;
    private int _dragAnchorOffsetX;
    private int _dragAnchorOffsetY;
    private int _clickOffsetInDialogX;
    private int _clickOffsetInDialogY;

    public event EventHandler? CommitRequested;
    public event EventHandler? DimensionsChanged;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? BrowseRequested;
    public event EventHandler? ModeChanged;
    public event EventHandler<DragStartingEventArgs>? DragStarting;
    public event EventHandler<DragEndedEventArgs>? DragEnded;

    public DialogMode Mode => _mode;

    public SizingDialogWindow()
    {
        InitializeComponent();
        ApplyLayout(_mode);

        WidthBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        HeightBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        FolderBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        FilenameBox.LostFocus += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);

        // Live frame resize while typing — separate from CommitRequested so we
        // don't write state.json on every keystroke.
        WidthBox.TextChanged += (_, _) => DimensionsChanged?.Invoke(this, EventArgs.Empty);
        HeightBox.TextChanged += (_, _) => DimensionsChanged?.Invoke(this, EventArgs.Empty);

        ScreenshotButton.Click += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);
        ScreenshotButtonRow0.Click += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);
        BrowseButton.Click += (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty);
        FolderToggleRow0.Click += (_, _) => OnFolderToggleClicked();
        FolderToggleRow2.Click += (_, _) => OnFolderToggleClicked();
        ToFilenameOnlyButton.Click += (_, _) => SetModeAndNotify(DialogMode.FilenameOnly);
        ToRegularButton.Click += (_, _) => SetModeAndNotify(DialogMode.Regular);

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Frameless window: drag from any non-input element (labels, background, border).
        // Double-click on the same areas cycles Regular ↔ FilenameOnly (no-op in DimensionsOnly).
        if (e.OriginalSource is TextBlock or Border or Grid or Window)
        {
            if (e.ClickCount == 2)
            {
                if (_mode == DialogMode.Regular) SetModeAndNotify(DialogMode.FilenameOnly);
                else if (_mode == DialogMode.FilenameOnly) SetModeAndNotify(DialogMode.Regular);
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
        // Remember the click offset within the dialog so the drag-end handler can
        // warp the cursor back onto the dialog at the same spot — even if the dialog
        // moved between outside-below and inside-frame as a result of re-placement.
        if (GetCursorPos(out var origin))
        {
            _clickOffsetInDialogX = origin.X - windowScreenX;
            _clickOffsetInDialogY = origin.Y - windowScreenY;
        }
        else
        {
            _clickOffsetInDialogX = 0;
            _clickOffsetInDialogY = 0;
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
        // Hand control to the feature: it re-evaluates inside/outside placement,
        // moves the dialog if needed, and then warps the cursor onto the new dialog
        // position via WarpCursorIntoDialog (using the offset we captured at click).
        DragEnded?.Invoke(this, new DragEndedEventArgs
        {
            ClickOffsetInDialogX = _clickOffsetInDialogX,
            ClickOffsetInDialogY = _clickOffsetInDialogY,
        });
        // Restore cursor visuals AFTER the warp so the pointer doesn't flash at the
        // pre-warp location.
        System.Windows.Input.Mouse.OverrideCursor = null;
        ShowCursor(true);
        Opacity = 1;
    }

    /// <summary>
    /// Warps the system cursor to the dialog's current position, offset by the
    /// supplied (physical-pixel) values. Used after drag re-placement so the cursor
    /// lands on the dialog regardless of whether it ended up inside or outside the frame.
    /// </summary>
    public void WarpCursorIntoDialog(int offsetXFromDialogLeft, int offsetYFromDialogTop)
    {
        var dpi = AppUtilities.GetDpiScaleForWindow(this);
        var dialogX = (int)Math.Round(Left * dpi);
        var dialogY = (int)Math.Round(Top * dpi);
        SetCursorPos(dialogX + offsetXFromDialogLeft, dialogY + offsetYFromDialogTop);
    }

    public void SetMode(DialogMode mode) => ApplyLayout(mode);

    private void SetModeAndNotify(DialogMode mode)
    {
        ApplyLayout(mode);
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFolderToggleClicked()
    {
        var target = _mode == DialogMode.DimensionsOnly ? DialogMode.Regular : DialogMode.DimensionsOnly;
        SetModeAndNotify(target);
    }

    private void ApplyLayout(DialogMode mode)
    {
        _mode = mode;
        var sizeRowVis    = mode is DialogMode.Regular or DialogMode.DimensionsOnly ? Visibility.Visible : Visibility.Collapsed;
        var folderRowVis  = mode == DialogMode.Regular                              ? Visibility.Visible : Visibility.Collapsed;
        var filenameRowVis= mode is DialogMode.Regular or DialogMode.FilenameOnly   ? Visibility.Visible : Visibility.Collapsed;

        SizeLabel.Visibility    = sizeRowVis;
        SizeFields.Visibility   = sizeRowVis;
        FolderToggleRow0.Visibility    = sizeRowVis;
        ToFilenameOnlyButton.Visibility = mode == DialogMode.Regular        ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotButtonRow0.Visibility = mode == DialogMode.DimensionsOnly ? Visibility.Visible : Visibility.Collapsed;

        FolderLabel.Visibility = folderRowVis;
        FolderBox.Visibility   = folderRowVis;
        BrowseButton.Visibility= folderRowVis;

        FilenameLabel.Visibility = filenameRowVis;
        FilenameBox.Visibility   = filenameRowVis;
        ScreenshotButton.Visibility = filenameRowVis;
        FolderToggleRow2.Visibility = mode == DialogMode.FilenameOnly ? Visibility.Visible : Visibility.Collapsed;
        ToRegularButton.Visibility  = mode == DialogMode.FilenameOnly ? Visibility.Visible : Visibility.Collapsed;

        var folderPressed = mode != DialogMode.DimensionsOnly;
        FolderToggleRow0.IsChecked = folderPressed;
        FolderToggleRow2.IsChecked = folderPressed;

        FilenameBox.Width = mode == DialogMode.FilenameOnly ? FilenameBoxFilenameOnlyWidth : FilenameBoxRegularWidth;
    }

    public void SetScreenshotShortcut(string shortcut)
    {
        _screenshotTip = string.IsNullOrWhiteSpace(shortcut) ? "Screenshot" : $"Screenshot ({shortcut})";
        if (ScreenshotButton.IsEnabled)
        {
            ScreenshotButton.ToolTip = _screenshotTip;
            ScreenshotButtonRow0.ToolTip = _screenshotTip;
        }
    }

    public void SetScreenshotEnabled(bool enabled)
    {
        ScreenshotButton.IsEnabled = enabled;
        ScreenshotButtonRow0.IsEnabled = enabled;
        var tip = enabled ? _screenshotTip : OffScreenTip;
        ScreenshotButton.ToolTip = tip;
        ScreenshotButtonRow0.ToolTip = tip;
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
