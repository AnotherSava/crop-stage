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
    private readonly NotifyIcon _trayIcon = null!;
    private readonly ToolStripMenuItem _startWithWindowsItem = null!;

    private ToolStripMenuItem _toggleFrameItem = null!;
    private ToolStripMenuItem _areaSelectItem = null!;
    private ToolStripMenuItem _quickSaveAreaSelectItem = null!;
    private GlobalHotkey? _frameHotkey;
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

        ReregisterHotkeys();

        _trayIconImage = AppUtilities.LoadOrCreateIcon();

        _toggleFrameItem = new ToolStripMenuItem("Show frame");
        _toggleFrameItem.Click += (_, _) => _frameFeature.Toggle();

        _areaSelectItem = new ToolStripMenuItem("Area select");
        _areaSelectItem.Click += (_, _) => _areaSelectFeature.Start();

        _quickSaveAreaSelectItem = new ToolStripMenuItem("Quick-save area select");
        _quickSaveAreaSelectItem.Click += (_, _) => _areaSelectFeature.StartQuickSave();

        RefreshMenuShortcutLabels();

        var hideWithEscItem = new ToolStripMenuItem("Hide with Esc")
        {
            CheckOnClick = true,
            Checked = _frameFeature.HideWithEsc
        };
        hideWithEscItem.CheckedChanged += (_, _) => _frameFeature.HideWithEsc = hideWithEscItem.Checked;

        var allowResizeItem = new ToolStripMenuItem("Drag to resize")
        {
            CheckOnClick = true,
            Checked = _frameFeature.Resizable
        };
        allowResizeItem.CheckedChanged += (_, _) => _frameFeature.Resizable = allowResizeItem.Checked;

        var copyToClipboardItem = BuildEnumRadioMenu(
            "Copy to clipboard",
            () => _frameFeature.ClipboardMode,
            v => _frameFeature.ClipboardMode = v,
            ("Image", ClipboardMode.Image),
            ("Path", ClipboardMode.Path),
            ("Nothing", ClipboardMode.None));

        var crosshairItem = BuildEnumRadioMenu(
            "Area select crosshair",
            () => _areaSelectFeature.CrosshairMode,
            v => _areaSelectFeature.CrosshairMode = v,
            ("None", CrosshairMode.None),
            ("1st point", CrosshairMode.FirstPoint),
            ("Both points", CrosshairMode.BothPoints));

        var folderModeItem = BuildEnumRadioMenu(
            "Folder mode",
            () => _frameFeature.FolderActivationMode,
            v => _frameFeature.FolderActivationMode = v,
            ("Default folder", FolderActivationMode.DefaultFolder),
            ("Specify destination", FolderActivationMode.SpecifyDestination),
            ("Last used", FolderActivationMode.LastUsed));

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = GetStartWithWindows()
        };
        _startWithWindowsItem.CheckedChanged += OnStartWithWindowsChanged;

        var configItem = BuildConfigMenu();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "Crop Stage",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Opening += (_, _) => _toggleFrameItem.Text = _frameFeature.IsVisible ? "Hide frame" : "Show frame";
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _frameFeature.Toggle(); };
        _trayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            _toggleFrameItem,
            _areaSelectItem,
            _quickSaveAreaSelectItem,
            hideWithEscItem,
            allowResizeItem,
            copyToClipboardItem,
            crosshairItem,
            folderModeItem,
            new ToolStripSeparator(),
            _startWithWindowsItem,
            configItem,
            new ToolStripSeparator(),
            exitItem
        });

        _frameFeature.ScreenshotSaved += OnScreenshotSaved;

        Logger.Info("Crop Stage started.");
    }

    private static ToolStripMenuItem BuildEnumRadioMenu<TEnum>(
        string title,
        Func<TEnum> getCurrent,
        Action<TEnum> setCurrent,
        params (string label, TEnum value)[] items) where TEnum : struct, Enum
    {
        var parent = new ToolStripMenuItem(title);
        var entries = items.Select(i => (item: new ToolStripMenuItem(i.label), i.value)).ToArray();

        void Refresh()
        {
            var current = getCurrent();
            foreach (var (item, value) in entries)
                item.Checked = EqualityComparer<TEnum>.Default.Equals(value, current);
        }

        foreach (var (item, value) in entries)
        {
            var captured = value;
            item.Click += (_, _) => { setCurrent(captured); Refresh(); };
        }

        parent.DropDownItems.AddRange(entries.Select(e => (ToolStripItem)e.item).ToArray());
        Refresh();
        return parent;
    }

    private ToolStripMenuItem BuildConfigMenu()
    {
        var parent = new ToolStripMenuItem("Config");

        var openFileItem = new ToolStripMenuItem("Open config file");
        openFileItem.Click += (_, _) => OpenConfigFile();

        var openLocationItem = new ToolStripMenuItem("Open config location");
        openLocationItem.Click += (_, _) => OpenConfigLocation();

        var reloadItem = new ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, _) => ReloadConfig();

        parent.DropDownItems.AddRange(new ToolStripItem[] { openFileItem, openLocationItem, reloadItem });
        return parent;
    }

    private static void OpenConfigFile()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(settingsPath)) return;
        try { Process.Start(new ProcessStartInfo(settingsPath) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"Could not open config file: {ex.Message}"); }
    }

    private static void OpenConfigLocation()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(settingsPath))
            Process.Start("explorer.exe", $"/select,\"{settingsPath}\"");
        else
            Process.Start("explorer.exe", AppContext.BaseDirectory);
    }

    private void ReloadConfig()
    {
        try
        {
            _config.Reload();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            Logger.Error($"Reload failed: {ex.Message}");
            _trayIcon.ShowBalloonTip(5000, "Crop Stage — reload failed", ex.Message, ToolTipIcon.Error);
            return;
        }

        Logger.Info("Config reloaded");
        ReregisterHotkeys();
        RefreshMenuShortcutLabels();
        _frameFeature.HideForReload();
        _trayIcon.ShowBalloonTip(2000, "Crop Stage", "Config reloaded", ToolTipIcon.Info);
    }

    private void ReregisterHotkeys()
    {
        _frameHotkey?.Dispose();
        _screenshotHotkey?.Dispose();
        _areaSelectHotkey?.Dispose();
        _quickSaveAreaSelectHotkey?.Dispose();
        _frameHotkey = _screenshotHotkey = _areaSelectHotkey = _quickSaveAreaSelectHotkey = null;

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
    }

    private void RefreshMenuShortcutLabels()
    {
        _toggleFrameItem.ShortcutKeyDisplayString = _frameHotkey?.IsRegistered == true ? _config.FrameToggleShortcut : "";
        _areaSelectItem.ShortcutKeyDisplayString = _areaSelectHotkey?.IsRegistered == true ? _config.AreaSelectShortcut : "";
        _quickSaveAreaSelectItem.ShortcutKeyDisplayString = _quickSaveAreaSelectHotkey?.IsRegistered == true ? _config.QuickSaveAreaSelectShortcut : "";
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
