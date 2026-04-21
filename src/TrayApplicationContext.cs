using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using CropStage.Features.AreaSelect;
using CropStage.Features.SizingFrame;

namespace CropStage;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config = null!;
    private readonly SizingFrameFeature _frameFeature = null!;
    private readonly AreaSelectFeature _areaSelectFeature = null!;
    private readonly GlobalHotkey _frameHotkey = null!;
    private readonly NotifyIcon _trayIcon = null!;
    private readonly ToolStripMenuItem _startWithWindowsItem = null!;

    private GlobalHotkey? _screenshotHotkey;
    private GlobalHotkey? _areaSelectHotkey;
    private GlobalHotkey? _quickSaveAreaSelectHotkey;
    private Icon? _trayIconImage;
    private bool _disposed;

    public TrayApplicationContext()
    {
        Logger.Init();

        var infoVersion = typeof(TrayApplicationContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        var versionLabel = infoVersion != null && infoVersion != "1.0.0" ? $"v{infoVersion}" : "dev version";
        Logger.Info($"Crop Stage: {versionLabel}");

        try
        {
            _config = new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            Logger.Error($"Config error: '{configPath}': {ex.Message}");
            var heading = ex is FileNotFoundException ? "Config file not found" : "Config file is invalid";
            var detail = ex switch
            {
                FileNotFoundException => "Expected config.json next to the executable.",
                JsonException je => je.Message.Split('.')[0] + ".",
                InvalidOperationException ioe => ioe.Message.Replace("Invalid config: ", ""),
                _ => "Check log file for more details."
            };
            ShowConfigError(heading, detail);
            return;
        }
        Logger.Info($"Config: frameToggleShortcut='{_config.FrameToggleShortcut}', screenshotShortcut='{_config.ScreenshotShortcut}', areaSelectShortcut='{_config.AreaSelectShortcut}', quickSaveAreaSelectShortcut='{_config.QuickSaveAreaSelectShortcut}', frameBorderColor={_config.FrameBorderColor}, frameBorderThickness={_config.FrameBorderThickness}, defaultFrame={_config.DefaultFrameWidth}x{_config.DefaultFrameHeight}, defaultFolder='{_config.DefaultScreenshotFolder}', defaultFilename='{_config.DefaultScreenshotFilename}', quickSaveFolder='{_config.QuickSaveFolder}'");

        _frameFeature = new SizingFrameFeature(_config);
        _areaSelectFeature = new AreaSelectFeature(_config, _frameFeature);

        if (!_frameFeature.StartWithWindowsInitialized)
        {
            try { AppConfig.SetStartWithWindows(true); }
            catch (Exception ex) { Logger.Warn($"Failed to set Start with Windows default: {ex.Message}"); }
            _frameFeature.StartWithWindowsInitialized = true;
        }

        _frameHotkey = new GlobalHotkey(1, _config.FrameToggleShortcut, () => _frameFeature.Toggle());
        if (!_frameHotkey.IsRegistered)
            Logger.Warn($"Could not register hotkey '{_config.FrameToggleShortcut}' — use the tray menu instead");

        if (!string.IsNullOrWhiteSpace(_config.ScreenshotShortcut))
        {
            _screenshotHotkey = new GlobalHotkey(2, _config.ScreenshotShortcut, () => _frameFeature.TakeScreenshotOrShow());
            if (!_screenshotHotkey.IsRegistered)
                Logger.Warn($"Could not register screenshot hotkey '{_config.ScreenshotShortcut}' — may be in use by Snipping Tool or another app");
        }

        if (!string.IsNullOrWhiteSpace(_config.AreaSelectShortcut))
        {
            _areaSelectHotkey = new GlobalHotkey(3, _config.AreaSelectShortcut, () => _areaSelectFeature.Start());
            if (!_areaSelectHotkey.IsRegistered)
                Logger.Warn($"Could not register area-select hotkey '{_config.AreaSelectShortcut}' — may already be in use");
        }

        if (!string.IsNullOrWhiteSpace(_config.QuickSaveAreaSelectShortcut))
        {
            _quickSaveAreaSelectHotkey = new GlobalHotkey(4, _config.QuickSaveAreaSelectShortcut, () => _areaSelectFeature.StartQuickSave());
            if (!_quickSaveAreaSelectHotkey.IsRegistered)
                Logger.Warn($"Could not register quick-save area-select hotkey '{_config.QuickSaveAreaSelectShortcut}' — may already be in use");
        }

        _trayIconImage = AppUtilities.LoadOrCreateIcon();

        var toggleFrameItem = new ToolStripMenuItem("Show Frame");
        if (_frameHotkey.IsRegistered)
            toggleFrameItem.ShortcutKeyDisplayString = _config.FrameToggleShortcut;
        toggleFrameItem.Click += (_, _) => _frameFeature.Toggle();

        var areaSelectItem = new ToolStripMenuItem("Area Select");
        if (_areaSelectHotkey?.IsRegistered == true)
            areaSelectItem.ShortcutKeyDisplayString = _config.AreaSelectShortcut;
        areaSelectItem.Click += (_, _) => _areaSelectFeature.Start();

        var quickSaveAreaSelectItem = new ToolStripMenuItem("Quick-save Area Select");
        if (_quickSaveAreaSelectHotkey?.IsRegistered == true)
            quickSaveAreaSelectItem.ShortcutKeyDisplayString = _config.QuickSaveAreaSelectShortcut;
        quickSaveAreaSelectItem.Click += (_, _) => _areaSelectFeature.StartQuickSave();

        var hideWithEscItem = new ToolStripMenuItem("Hide with Esc")
        {
            CheckOnClick = true,
            Checked = _frameFeature.HideWithEsc
        };
        hideWithEscItem.CheckedChanged += (_, _) => _frameFeature.HideWithEsc = hideWithEscItem.Checked;

        var allowResizeItem = new ToolStripMenuItem("Drag to Resize")
        {
            CheckOnClick = true,
            Checked = _frameFeature.Resizable
        };
        allowResizeItem.CheckedChanged += (_, _) => _frameFeature.Resizable = allowResizeItem.Checked;

        var copyToClipboardItem = BuildClipboardModeMenu();
        var crosshairItem = BuildCrosshairModeMenu();

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = GetStartWithWindows()
        };
        _startWithWindowsItem.CheckedChanged += OnStartWithWindowsChanged;

        var openConfigItem = new ToolStripMenuItem("Open Config Location");
        openConfigItem.Click += (_, _) =>
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(settingsPath))
                Process.Start("explorer.exe", $"/select,\"{settingsPath}\"");
            else
                Process.Start("explorer.exe", AppContext.BaseDirectory);
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "Crop Stage",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Opening += (_, _) => toggleFrameItem.Text = _frameFeature.IsVisible ? "Hide Frame" : "Show Frame";
        _trayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            toggleFrameItem,
            areaSelectItem,
            quickSaveAreaSelectItem,
            hideWithEscItem,
            allowResizeItem,
            copyToClipboardItem,
            crosshairItem,
            new ToolStripSeparator(),
            _startWithWindowsItem,
            openConfigItem,
            new ToolStripSeparator(),
            exitItem
        });

        _frameFeature.ScreenshotSaved += OnScreenshotSaved;

        Logger.Info("Crop Stage started.");
    }

    private ToolStripMenuItem BuildClipboardModeMenu()
    {
        var parent = new ToolStripMenuItem("Copy to Clipboard");
        var imageItem = new ToolStripMenuItem("Image");
        var pathItem = new ToolStripMenuItem("Path");
        var nothingItem = new ToolStripMenuItem("Nothing");

        void Refresh()
        {
            var mode = _frameFeature.ClipboardMode;
            imageItem.Checked = mode == ClipboardMode.Image;
            pathItem.Checked = mode == ClipboardMode.Path;
            nothingItem.Checked = mode == ClipboardMode.None;
        }

        imageItem.Click += (_, _) => { _frameFeature.ClipboardMode = ClipboardMode.Image; Refresh(); };
        pathItem.Click += (_, _) => { _frameFeature.ClipboardMode = ClipboardMode.Path; Refresh(); };
        nothingItem.Click += (_, _) => { _frameFeature.ClipboardMode = ClipboardMode.None; Refresh(); };

        parent.DropDownItems.AddRange(new ToolStripItem[] { imageItem, pathItem, nothingItem });
        Refresh();
        return parent;
    }

    private ToolStripMenuItem BuildCrosshairModeMenu()
    {
        var parent = new ToolStripMenuItem("Area Select Crosshair");
        var noneItem = new ToolStripMenuItem("None");
        var firstItem = new ToolStripMenuItem("1st point");
        var bothItem = new ToolStripMenuItem("Both points");

        void Refresh()
        {
            var mode = _areaSelectFeature.CrosshairMode;
            noneItem.Checked = mode == CrosshairMode.None;
            firstItem.Checked = mode == CrosshairMode.FirstPoint;
            bothItem.Checked = mode == CrosshairMode.BothPoints;
        }

        noneItem.Click += (_, _) => { _areaSelectFeature.CrosshairMode = CrosshairMode.None; Refresh(); };
        firstItem.Click += (_, _) => { _areaSelectFeature.CrosshairMode = CrosshairMode.FirstPoint; Refresh(); };
        bothItem.Click += (_, _) => { _areaSelectFeature.CrosshairMode = CrosshairMode.BothPoints; Refresh(); };

        parent.DropDownItems.AddRange(new ToolStripItem[] { noneItem, firstItem, bothItem });
        Refresh();
        return parent;
    }

    private void OnScreenshotSaved(object? sender, string path)
    {
        _trayIcon.ShowBalloonTip(3000, "Crop Stage", $"Saved: {path}", ToolTipIcon.Info);
    }

    private void OnStartWithWindowsChanged(object? sender, EventArgs e)
    {
        try
        {
            AppConfig.SetStartWithWindows(_startWithWindowsItem.Checked);
            Logger.Info($"Start with Windows: {_startWithWindowsItem.Checked}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to set Start with Windows: {ex.Message}");
            _startWithWindowsItem.CheckedChanged -= OnStartWithWindowsChanged;
            _startWithWindowsItem.Checked = !_startWithWindowsItem.Checked;
            _startWithWindowsItem.CheckedChanged += OnStartWithWindowsChanged;
        }
    }

    private void ExitApplication()
    {
        Logger.Info("Shutting down...");
        Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _frameHotkey?.Dispose();
            _screenshotHotkey?.Dispose();
            _areaSelectHotkey?.Dispose();
            _quickSaveAreaSelectHotkey?.Dispose();
            _areaSelectFeature?.Dispose();
            _frameFeature?.Dispose();
            _trayIconImage?.Dispose();
            Logger.Close();
        }
        base.Dispose(disposing);
    }

    private static void ShowConfigError(string heading, string detail)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "crop-stage.log");
        Logger.Close();
        var logContent = "";
        try { logContent = File.ReadAllText(logPath); } catch { }
        var page = new TaskDialogPage
        {
            Heading = heading,
            Text = detail,
            Icon = TaskDialogIcon.Error,
            Caption = "Crop Stage",
            Buttons = { TaskDialogButton.OK }
        };
        if (!string.IsNullOrEmpty(logContent))
            page.Expander = new TaskDialogExpander { Text = logContent, CollapsedButtonText = "Details", ExpandedButtonText = "Details", Position = TaskDialogExpanderPosition.AfterFootnote };
        TaskDialog.ShowDialog(page);
        Environment.Exit(1);
    }

    private static bool GetStartWithWindows()
    {
        try { return AppConfig.IsStartWithWindows(); } catch { return false; }
    }
}
