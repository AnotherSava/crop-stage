using CropStage.Features.SizingFrame;

namespace CropStage.Features.AreaSelect;

/// <summary>
/// Coordinates the area-select flow: hotkey press opens a fullscreen overlay for
/// click-drag rectangle selection; on mouse-up the sizing frame is shown at that rect.
/// </summary>
public sealed class AreaSelectFeature : IDisposable
{
    private const int EscHotkeyId = 9997;

    private readonly AppConfig _config;
    private readonly SizingFrameFeature _frameFeature;

    private AreaSelectOverlay? _overlay;
    private GlobalHotkey? _escHotkey;
    private bool _active;
    private bool _disposed;

    public AreaSelectFeature(AppConfig config, SizingFrameFeature frameFeature)
    {
        _config = config;
        _frameFeature = frameFeature;
    }

    public CrosshairMode CrosshairMode
    {
        get => _frameFeature.CrosshairMode;
        set => _frameFeature.CrosshairMode = value;
    }

    public void Start()
    {
        if (_active) return;
        _active = true;

        _frameFeature.HideIfVisible();

        // Global Esc — the overlay has mouse capture, which suppresses normal keyboard
        // focus handling. Route Esc through the same hotkey mechanism used elsewhere
        // instead of relying on KeyDown on the form.
        try
        {
            _escHotkey = new GlobalHotkey(EscHotkeyId, "Escape", () => _overlay?.Cancel());
            if (!_escHotkey.IsRegistered)
            {
                Logger.Warn("Could not register Esc hotkey for area select");
                _escHotkey.Dispose();
                _escHotkey = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Area-select Esc hotkey registration failed: {ex.Message}");
            _escHotkey = null;
        }

        _overlay = new AreaSelectOverlay(_config, _frameFeature.CrosshairMode);
        _overlay.Selected += OnSelected;
        _overlay.Cancelled += OnCancelled;
        _overlay.Show();
    }

    private void OnSelected(int leftPx, int topPx, int widthPx, int heightPx)
    {
        Teardown();
        Logger.Info($"Area selected: {widthPx}x{heightPx} at ({leftPx},{topPx})");
        _frameFeature.ShowAtRect(leftPx, topPx, widthPx, heightPx);
    }

    private void OnCancelled()
    {
        Teardown();
        Logger.Info("Area select cancelled");
    }

    private void Teardown()
    {
        _escHotkey?.Dispose();
        _escHotkey = null;
        if (_overlay != null)
        {
            _overlay.Selected -= OnSelected;
            _overlay.Cancelled -= OnCancelled;
            _overlay = null;
        }
        _active = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Teardown();
    }
}
