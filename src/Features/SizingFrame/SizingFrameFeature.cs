using System.IO;
using System.Windows;
using System.Windows.Forms;

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
        PlaceDialogAndFrameCentered();
        _dialog.Show();
        // Forward keyboard messages from the WinForms message pump into WPF's input system.
        // Without this, WPF receives WM_KEYDOWN but never the WM_CHAR that TextInput needs,
        // so letters/digits don't appear in textboxes (only backspace/delete work).
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_dialog);
        _dialog.Activate();
        _frame!.Show();
        if (_state.HideWithEsc) RegisterEscHotkey();
        _visible = true;
        Logger.Info($"Frame shown: {_state.Width}x{_state.Height}");
    }

    private void Hide()
    {
        // Commit any pending textbox edits before hiding so typed values aren't lost.
        OnCommitRequested(this, EventArgs.Empty);
        _escHotkey?.Dispose();
        _escHotkey = null;
        _dialog?.Hide();
        _frame?.Hide();
        _visible = false;
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
    }

    private void PlaceDialogAndFrameCentered()
    {
        // Layout: frame on top, dialog directly below, dialog's top-left flush with
        // frame's outer-bottom-left. Frame dimension changes pivot at this corner.
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var dpi = AppUtilities.GetPrimaryDpiScale();
        var screenWidthDip = primary.Bounds.Width / dpi;
        var screenHeightDip = primary.Bounds.Height / dpi;

        var interiorWidth = _state.Width / dpi;
        var interiorHeight = _state.Height / dpi;
        var borderT = _config.FrameBorderThickness / dpi;
        var dialogWidth = GetDialogWidth();
        var dialogHeight = GetDialogHeight();

        var frameOuterWidth = interiorWidth + 2 * borderT;
        var frameOuterHeight = interiorHeight + 2 * borderT;
        var compWidth = Math.Max(frameOuterWidth, dialogWidth);
        var compHeight = frameOuterHeight + dialogHeight;

        var compLeft = Math.Max(0, (screenWidthDip - compWidth) / 2);
        var compTop = Math.Max(0, (screenHeightDip - compHeight) / 2);

        var frameOuterLeft = compLeft + (compWidth - frameOuterWidth) / 2;
        _dialog!.Left = frameOuterLeft;
        _dialog.Top = compTop + frameOuterHeight;

        var interiorLeft = frameOuterLeft + borderT;
        var interiorTop = compTop + borderT;

        _frame!.SetInteriorGeometry(interiorLeft, interiorTop, interiorWidth, interiorHeight);
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
        if (_dialog == null || _frame == null) return;
        var dpi = AppUtilities.GetPrimaryDpiScale();
        var borderT = _config.FrameBorderThickness / dpi;
        var interiorWidth = widthPx / dpi;
        var interiorHeight = heightPx / dpi;
        var interiorLeft = _dialog.Left + borderT;
        var interiorTop = _dialog.Top - interiorHeight - borderT;
        _frame.SetInteriorGeometry(interiorLeft, interiorTop, interiorWidth, interiorHeight);
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

        var dpi = AppUtilities.GetPrimaryDpiScale();
        var borderT = _config.FrameBorderThickness / dpi;
        var interiorWidthDip = _state.Width / dpi;
        var interiorHeightDip = _state.Height / dpi;
        var interiorLeftDip = _dialog.Left + borderT;
        var interiorTopDip = _dialog.Top - interiorHeightDip - borderT;
        var interiorLeftPx = (int)Math.Round(interiorLeftDip * dpi);
        var interiorTopPx = (int)Math.Round(interiorTopDip * dpi);
        var interiorWidthPx = _state.Width;
        var interiorHeightPx = _state.Height;

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
            ScreenshotSaved?.Invoke(this, saved);
            _frame.Flash();
        }
        catch (Exception ex)
        {
            Logger.Error($"Screenshot failed: {ex.Message}");
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
}
