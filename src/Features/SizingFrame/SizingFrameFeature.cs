using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace CropStage.Features.SizingFrame;

/// <summary>
/// Coordinates the sizing frame: dialog + frame window, state persistence,
/// geometry sync, and screenshot capture.
/// </summary>
public sealed class SizingFrameFeature : IDisposable
{
    private const int EscHotkeyId = 9998;

    private readonly AppConfig _config;
    private readonly FrameState _state;

    private SizingDialogWindow? _dialog;
    private SizingFrameBorder? _frame;
    private GlobalHotkey? _escHotkey;
    private double _dialogWidthCache;
    private double _dialogHeightCache;
    private bool _visible;
    private bool _disposed;
    private bool _closing;
    private bool _suppressSync;
    private double _preResizeDialogLeft;
    private double _preResizeDialogTop;
    private int _pendingDialogPhysicalLeft;
    private int _pendingDialogPhysicalTop;
    private bool _hasPendingDialogPosition;

    public bool IsVisible => _visible;
    public event EventHandler<string>? ScreenshotSaved;

    public SizingFrameFeature(AppConfig config)
    {
        _config = config;
        _state = new FrameState(config);
    }

    public bool HideWithEsc
    {
        get => _state.HideWithEsc;
        set
        {
            if (_state.HideWithEsc == value) return;
            _state.HideWithEsc = value;
            if (_visible)
            {
                _escHotkey?.Dispose();
                _escHotkey = null;
                if (value) RegisterEscHotkey();
            }
        }
    }

    public ClipboardMode ClipboardMode
    {
        get => _state.ClipboardMode;
        set => _state.ClipboardMode = value;
    }

    public CrosshairMode CrosshairMode
    {
        get => _state.CrosshairMode;
        set => _state.CrosshairMode = value;
    }

    public bool Resizable
    {
        get => _state.Resizable;
        set
        {
            if (_state.Resizable == value) return;
            _state.Resizable = value;
            _frame?.SetResizable(value);
        }
    }

    public bool StartWithWindowsInitialized
    {
        get => _state.StartWithWindowsInitialized;
        set => _state.StartWithWindowsInitialized = value;
    }

    public void Toggle()
    {
        if (_visible)
            Hide();
        else
            Show();
    }

    /// <summary>
    /// Called from a global hotkey (e.g. PrintScreen): opens the dialog if closed,
    /// or triggers a screenshot if already open.
    /// </summary>
    public void TakeScreenshotOrShow()
    {
        if (_visible)
            OnScreenshotRequested(this, EventArgs.Empty);
        else
            Show();
    }

    private void Show()
    {
        EnsureWindows();
        _dialog!.SetFields(_state.Width, _state.Height, _state.Folder, _state.Filename);

        if (_state.Left.HasValue && _state.Top.HasValue)
            PlaceAtSavedPosition(_state.Left.Value, _state.Top.Value);
        else
            PlaceDialogAndFrameCentered();

        ShowWindowsAtCurrentState();
    }

    public void ShowAtRect(int leftPx, int topPx, int widthPx, int heightPx)
    {
        _state.Width = widthPx;
        _state.Height = heightPx;
        _state.Left = leftPx;
        _state.Top = topPx;

        EnsureWindows();
        _dialog!.SetFields(widthPx, heightPx, _state.Folder, _state.Filename);
        PlaceAtSavedPosition(leftPx, topPx);
        ShowWindowsAtCurrentState();
    }

    public void HideIfVisible()
    {
        if (_visible) Hide();
    }

    private void ShowWindowsAtCurrentState()
    {
        _dialog!.Show();
        // Forward keyboard messages from the WinForms message pump into WPF's input system.
        // Without this, WPF receives WM_KEYDOWN but never the WM_CHAR that TextInput needs,
        // so letters/digits don't appear in textboxes (only backspace/delete work).
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_dialog);
        _dialog.Activate();
        // Pin the dialog to the exact physical pixels we computed. WPF's own Left/Top
        // path converts DIPs using the primary-monitor DPI at Show() time, so on a
        // non-primary-DPI monitor the dialog ends up offset from the frame. SetWindowPos
        // bypasses that conversion.
        if (_hasPendingDialogPosition)
        {
            var hwnd = new WindowInteropHelper(_dialog).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, IntPtr.Zero, _pendingDialogPhysicalLeft, _pendingDialogPhysicalTop,
                    0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            _hasPendingDialogPosition = false;
        }
        _frame!.Show();
        if (_state.HideWithEsc) RegisterEscHotkey();
        _visible = true;
        Logger.Info($"Frame shown: {_state.Width}x{_state.Height}");
    }

    private void Hide()
    {
        // Commit any pending textbox edits before hiding so typed values aren't lost.
        OnCommitRequested(this, EventArgs.Empty);
        SavePosition();
        _escHotkey?.Dispose();
        _escHotkey = null;
        _dialog?.Hide();
        _frame?.Hide();
        _visible = false;
    }

    private void SavePosition()
    {
        if (_dialog == null || _frame == null) return;
        var (leftPx, topPx) = GetInteriorOriginPx(_state.Height);
        _state.Left = leftPx;
        _state.Top = topPx;
    }

    private void RegisterEscHotkey()
    {
        _escHotkey?.Dispose();
        try
        {
            _escHotkey = new GlobalHotkey(EscHotkeyId, "Escape", () => Hide());
            if (!_escHotkey.IsRegistered)
            {
                Logger.Warn("Could not register Esc hotkey for dismiss");
                _escHotkey.Dispose();
                _escHotkey = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Esc hotkey registration failed: {ex.Message}");
            _escHotkey = null;
        }
    }

    private void EnsureWindows()
    {
        if (_dialog != null && _frame != null) return;

        _dialog = new SizingDialogWindow();
        _dialog.SetScreenshotShortcut(_config.ScreenshotShortcut);
        _frame = new SizingFrameBorder(_config.FrameBorderColor, _config.FrameBorderThickness);

        _dialog.LocationChanged += OnDialogLocationChanged;
        _dialog.CommitRequested += OnCommitRequested;
        _dialog.DimensionsChanged += OnDimensionsChanged;
        _dialog.ScreenshotRequested += OnScreenshotRequested;
        _dialog.BrowseRequested += OnBrowseRequested;
        _dialog.CompactModeChanged += OnCompactModeChanged;
        _dialog.Closing += (s, e) => { if (_closing) return; e.Cancel = true; Hide(); };

        _frame.ResizeStarted += OnFrameResizeStarted;
        _frame.FrameResizing += OnFrameResizing;
        _frame.ResizeCompleted += OnFrameResizeCompleted;
        _frame.SetResizable(_state.Resizable);
    }

    private void PlaceDialogAndFrameCentered()
    {
        // Layout: frame on top, dialog directly below, dialog's top-left flush with
        // frame's outer-bottom-left. Frame dimension changes pivot at this corner.
        // Center on primary monitor; compute frame in physical pixels, dialog in
        // primary-monitor DIPs (primary is the initial host, so WPF's DIP scale matches).
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var dpi = AppUtilities.GetPrimaryDpiScale();

        var interiorWidthPx = _state.Width;
        var interiorHeightPx = _state.Height;
        var borderTpx = _config.FrameBorderThickness;
        var dialogWidthPx = (int)Math.Round(GetDialogWidth() * dpi);
        var dialogHeightPx = (int)Math.Round(GetDialogHeight() * dpi);

        var frameOuterWidthPx = interiorWidthPx + 2 * borderTpx;
        var frameOuterHeightPx = interiorHeightPx + 2 * borderTpx;
        var compWidthPx = Math.Max(frameOuterWidthPx, dialogWidthPx);
        var compHeightPx = frameOuterHeightPx + dialogHeightPx;

        var compLeftPx = primary.Bounds.Left + Math.Max(0, (primary.Bounds.Width - compWidthPx) / 2);
        var compTopPx = primary.Bounds.Top + Math.Max(0, (primary.Bounds.Height - compHeightPx) / 2);

        var frameOuterLeftPx = compLeftPx + (compWidthPx - frameOuterWidthPx) / 2;
        var dialogLeftPx = frameOuterLeftPx;
        var dialogTopPx = compTopPx + frameOuterHeightPx;
        _dialog!.Left = dialogLeftPx / dpi;
        _dialog.Top = dialogTopPx / dpi;
        _pendingDialogPhysicalLeft = dialogLeftPx;
        _pendingDialogPhysicalTop = dialogTopPx;
        _hasPendingDialogPosition = true;

        var interiorLeftPx = frameOuterLeftPx + borderTpx;
        var interiorTopPx = compTopPx + borderTpx;

        UpdateFrameGeometry(interiorLeftPx, interiorTopPx, interiorWidthPx, interiorHeightPx);
    }

    private void PlaceAtSavedPosition(int savedLeftPx, int savedTopPx)
    {
        var interiorWidthPx = _state.Width;
        var interiorHeightPx = _state.Height;
        var borderTpx = _config.FrameBorderThickness;
        var dpi = AppUtilities.GetPrimaryDpiScale();
        var dialogLeftPx = savedLeftPx - borderTpx;
        var dialogTopPx = savedTopPx + interiorHeightPx + borderTpx;

        var frameOnScreen = IsRectFullyOnScreen(savedLeftPx, savedTopPx, interiorWidthPx, interiorHeightPx);

        // Try full dialog at saved position — ensure expanded mode before measuring.
        _dialog!.SetCompactMode(false);
        var (fullW, fullH) = MeasureDialogFresh();
        var fullDialogWidthPx = (int)Math.Round(fullW * dpi);
        var fullDialogHeightPx = (int)Math.Round(fullH * dpi);
        var fullDialogOnScreen = IsRectFullyOnScreen(dialogLeftPx, dialogTopPx, fullDialogWidthPx, fullDialogHeightPx);
        if (frameOnScreen && fullDialogOnScreen)
        {
            PlaceAtPosition(savedLeftPx, savedTopPx, dpi);
            return;
        }

        // Try compact dialog at saved position
        _dialog.SetCompactMode(true);
        var (compW, compH) = MeasureDialogFresh();
        var compactDialogWidthPx = (int)Math.Round(compW * dpi);
        var compactDialogHeightPx = (int)Math.Round(compH * dpi);
        var compactDialogOnScreen = IsRectFullyOnScreen(dialogLeftPx, dialogTopPx, compactDialogWidthPx, compactDialogHeightPx);
        if (frameOnScreen && compactDialogOnScreen)
        {
            PlaceAtPosition(savedLeftPx, savedTopPx, dpi);
            return;
        }

        // Nothing fits — center on primary with full dialog
        _dialog.SetCompactMode(false);
        PlaceDialogAndFrameCentered();
    }

    private void PlaceAtPosition(int interiorLeftPx, int interiorTopPx, double dpi)
    {
        var borderTpx = _config.FrameBorderThickness;
        var dialogLeftPx = interiorLeftPx - borderTpx;
        var dialogTopPx = interiorTopPx + _state.Height + borderTpx;
        // Set Window.Left/Top in DIPs as an initial placement hint for WPF, but
        // queue a post-Show SetWindowPos so the dialog lands at the exact physical
        // position regardless of per-monitor DPI mismatches.
        _dialog!.Left = dialogLeftPx / dpi;
        _dialog.Top = dialogTopPx / dpi;
        _pendingDialogPhysicalLeft = dialogLeftPx;
        _pendingDialogPhysicalTop = dialogTopPx;
        _hasPendingDialogPosition = true;
        UpdateFrameGeometry(interiorLeftPx, interiorTopPx, _state.Width, _state.Height);
    }

    private (double width, double height) MeasureDialogFresh()
    {
        // Window.Measure() returns stale DesiredSize after the window has been shown
        // and hidden. Measure the Content element (the Border) directly — it correctly
        // reflects collapsed children and has no chrome (WindowStyle=None).
        var content = (UIElement)_dialog!.Content;
        content.InvalidateMeasure();
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return (content.DesiredSize.Width, content.DesiredSize.Height);
    }

    private double GetDialogWidth()
    {
        if (_dialog == null) return 300;
        if (_dialog.ActualWidth > 0) { _dialogWidthCache = _dialog.ActualWidth; return _dialogWidthCache; }
        if (_dialogWidthCache > 0) return _dialogWidthCache;
        _dialog.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = _dialog.DesiredSize.Width;
        if (w > 0) _dialogWidthCache = w;
        return w > 0 ? w : 300;
    }

    private double GetDialogHeight()
    {
        if (_dialog == null) return 200;
        if (_dialog.ActualHeight > 0) { _dialogHeightCache = _dialog.ActualHeight; return _dialogHeightCache; }
        if (_dialogHeightCache > 0) return _dialogHeightCache;
        _dialog.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var h = _dialog.DesiredSize.Height;
        if (h > 0) _dialogHeightCache = h;
        return h > 0 ? h : 200;
    }

    private void OnDialogLocationChanged(object? sender, EventArgs e)
    {
        SyncFrameToDialog();
    }

    private void OnCompactModeChanged(object? sender, EventArgs e)
    {
        // Dialog has resized — re-measure and reposition the frame so its outer-bottom-left
        // still sits flush with the dialog's top-left.
        _dialog?.UpdateLayout();
        _dialogWidthCache = 0;
        _dialogHeightCache = 0;
        SyncFrameToDialog();
    }

    private void SyncFrameToDialog() => SyncFrameToDialog(_state.Width, _state.Height);

    private void SyncFrameToDialog(int widthPx, int heightPx)
    {
        if (_dialog == null || _frame == null || _suppressSync) return;
        var (leftPx, topPx) = GetInteriorOriginPx(heightPx);
        UpdateFrameGeometry(leftPx, topPx, widthPx, heightPx);
    }

    private void UpdateFrameGeometry(int leftPx, int topPx, int widthPx, int heightPx)
    {
        _frame!.SetInteriorGeometry(leftPx, topPx, widthPx, heightPx);
        var onScreen = IsRectFullyOnScreen(leftPx, topPx, widthPx, heightPx);
        _dialog?.SetScreenshotEnabled(onScreen);
        _frame.SetCrossVisible(!onScreen);
    }

    /// <summary>
    /// Returns true if every pixel of the given rect (in physical virtual-screen pixels)
    /// is covered by some monitor. Assumes monitors don't overlap, which is the Windows default.
    /// </summary>
    private static bool IsRectFullyOnScreen(int leftPx, int topPx, int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0) return false;
        var rect = new System.Drawing.Rectangle(leftPx, topPx, widthPx, heightPx);
        long rectArea = (long)widthPx * heightPx;
        long covered = 0;
        foreach (var screen in Screen.AllScreens)
        {
            var intersect = System.Drawing.Rectangle.Intersect(rect, screen.Bounds);
            if (!intersect.IsEmpty)
                covered += (long)intersect.Width * intersect.Height;
        }
        return covered >= rectArea;
    }

    /// <summary>
    /// Computes the interior (top-left) corner of the frame in physical virtual-screen pixels,
    /// using the dialog's current monitor DPI (PerMonitorV2) so the frame lines up exactly
    /// with the dialog even across monitors.
    /// </summary>
    private (int leftPx, int topPx) GetInteriorOriginPx(int interiorHeightPx)
    {
        var dpi = AppUtilities.GetDpiScaleForWindow(_dialog!);
        var borderTpx = _config.FrameBorderThickness;
        var dialogLeftPx = (int)Math.Round(_dialog!.Left * dpi);
        var dialogTopPx = (int)Math.Round(_dialog.Top * dpi);
        return (dialogLeftPx + borderTpx, dialogTopPx - interiorHeightPx - borderTpx);
    }

    private void OnDimensionsChanged(object? sender, EventArgs e)
    {
        if (_dialog == null) return;
        var w = _dialog.WidthValue;
        var h = _dialog.HeightValue;
        // Live frame resize while typing. State (and disk save) updates on LostFocus
        // via OnCommitRequested. Skip transient zero/empty values.
        if (w > 0 && h > 0) SyncFrameToDialog(w, h);
    }

    private void OnCommitRequested(object? sender, EventArgs e)
    {
        if (_dialog == null) return;

        var w = _dialog.WidthValue;
        var h = _dialog.HeightValue;
        var dimsChanged = false;
        if (w > 0 && w != _state.Width) { _state.Width = w; dimsChanged = true; }
        if (h > 0 && h != _state.Height) { _state.Height = h; dimsChanged = true; }

        var folder = _dialog.FolderValue;
        _state.Folder = string.IsNullOrWhiteSpace(folder) ? _config.DefaultScreenshotFolder : folder;

        var filename = _dialog.FilenameValue;
        filename = string.IsNullOrWhiteSpace(filename) ? _config.DefaultScreenshotFilename : filename;
        // Strip directory components and force .png extension — capture always writes PNG.
        filename = Path.GetFileName(filename);
        var ext = Path.GetExtension(filename);
        if (!string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
            filename = Path.ChangeExtension(filename, ".png");
        _state.Filename = filename;

        // Push normalized values back so the UI never shows a blank when a default is in effect.
        _dialog.SetFields(_state.Width, _state.Height, _state.Folder, _state.Filename);

        if (dimsChanged) SyncFrameToDialog();
    }

    private void OnFrameResizeStarted()
    {
        _escHotkey?.Dispose();
        _escHotkey = null;
        if (_dialog != null)
        {
            _preResizeDialogLeft = _dialog.Left;
            _preResizeDialogTop = _dialog.Top;
        }
    }

    private void OnFrameResizing(int outerLeftPx, int outerBottomPx, int interiorWidthPx, int interiorHeightPx)
    {
        if (_dialog == null) return;
        _suppressSync = true;
        try
        {
            var dpi = AppUtilities.GetDpiScaleForWindow(_dialog);
            _dialog.Left = outerLeftPx / dpi;
            _dialog.Top = outerBottomPx / dpi;
            _dialog.SetFields(interiorWidthPx, interiorHeightPx, _dialog.FolderValue, _dialog.FilenameValue);
        }
        finally { _suppressSync = false; }
    }

    private void OnFrameResizeCompleted()
    {
        if (_visible && _state.HideWithEsc) RegisterEscHotkey();

        if (_dialog == null || _frame == null) return;
        var (w, h) = _frame.GetInteriorDimensions();
        bool cancelled = w == _state.Width && h == _state.Height;
        if (cancelled)
        {
            _suppressSync = true;
            try
            {
                _dialog.Left = _preResizeDialogLeft;
                _dialog.Top = _preResizeDialogTop;
                _dialog.SetFields(w, h, _dialog.FolderValue, _dialog.FilenameValue);
            }
            finally { _suppressSync = false; }
        }
        else if (w > 0 && h > 0)
        {
            _suppressSync = true;
            try { _dialog.SetFields(w, h, _dialog.FolderValue, _dialog.FilenameValue); }
            finally { _suppressSync = false; }
            _state.Width = w;
            _state.Height = h;
        }
        SyncFrameToDialog();
    }

    private void OnBrowseRequested(object? sender, EventArgs e)
    {
        var initial = _state.ResolvedFolder;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder for screenshots"
        };
        if (Directory.Exists(initial)) dlg.InitialDirectory = initial;

        // Release the global Esc hotkey so the folder picker can dismiss with Esc.
        _escHotkey?.Dispose();
        _escHotkey = null;
        bool? result;
        try { result = dlg.ShowDialog(); }
        finally { if (_visible && _state.HideWithEsc) RegisterEscHotkey(); }

        if (result == true && _dialog != null)
        {
            var folder = dlg.FolderName;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                _dialog.SetFields(_dialog.WidthValue, _dialog.HeightValue, folder, _dialog.FilenameValue);
                _state.Folder = folder;
            }
        }
    }

    private void OnScreenshotRequested(object? sender, EventArgs e)
    {
        if (_dialog == null || _frame == null) return;

        // Commit any pending textbox edits so the capture uses current dialog values.
        OnCommitRequested(this, EventArgs.Empty);

        var interiorWidthPx = _state.Width;
        var interiorHeightPx = _state.Height;
        var (interiorLeftPx, interiorTopPx) = GetInteriorOriginPx(interiorHeightPx);

        if (!IsRectFullyOnScreen(interiorLeftPx, interiorTopPx, interiorWidthPx, interiorHeightPx))
        {
            Logger.Info("Screenshot skipped: frame is not fully on-screen");
            return;
        }

        var folder = _state.ResolvedFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            Logger.Error("Screenshot folder is empty — cannot save");
            return;
        }
        var filename = _state.Filename;

        // Dialog is to the left of the frame, borders are outside the interior —
        // nothing we own is inside the captured rect, so capture directly and flash
        // the frame as visual confirmation.
        try
        {
            var saved = ScreenshotCapture.Capture(interiorLeftPx, interiorTopPx, interiorWidthPx, interiorHeightPx, folder, filename);
            Logger.Info($"Screenshot saved: '{saved}'");
            CopyToClipboard(saved);
            ScreenshotSaved?.Invoke(this, saved);
            _frame.Flash();
        }
        catch (Exception ex)
        {
            Logger.Error($"Screenshot failed: {ex.Message}");
        }
    }

    private void CopyToClipboard(string savedPath)
    {
        switch (_state.ClipboardMode)
        {
            case ClipboardMode.Image:
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(savedPath);
                    bmp.EndInit();
                    bmp.Freeze();
                    System.Windows.Clipboard.SetImage(bmp);
                }
                catch (Exception ex) { Logger.Warn($"Could not copy image to clipboard: {ex.Message}"); }
                break;
            case ClipboardMode.Path:
                try { System.Windows.Clipboard.SetText(savedPath); }
                catch (Exception ex) { Logger.Warn($"Could not copy path to clipboard: {ex.Message}"); }
                break;
            case ClipboardMode.None:
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _escHotkey?.Dispose();
        _closing = true;
        _dialog?.Close();
        _frame?.Dispose();
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
