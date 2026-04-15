using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using CropStage.Features.SizingFrame;

namespace CropStage;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config = null!;
    private readonly SizingFrameFeature _frameFeature = null!;
    private readonly GlobalHotkey _frameHotkey = null!;
    private readonly NotifyIcon _trayIcon = null!;
    private readonly ToolStripMenuItem _startWithWindowsItem = null!;

    private GlobalHotkey? _screenshotHotkey;
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
        Logger.Info($"Config: frameToggleShortcut='{_config.FrameToggleShortcut}', frameBorderColor={_config.FrameBorderColor}, frameBorderThickness={_config.FrameBorderThickness}, defaultFrame={_config.DefaultFrameWidth}x{_config.DefaultFrameHeight}, defaultFolder='{_config.DefaultScreenshotFolder}', defaultFilename='{_config.DefaultScreenshotFilename}'");

        _frameFeature = new SizingFrameFeature(_config);

        _frameHotkey = new GlobalHotkey(1, _config.FrameToggleShortcut, () => _frameFeature.Toggle());
        if (!_frameHotkey.IsRegistered)
            Logger.Warn($"Could not register hotkey '{_config.FrameToggleShortcut}' — use the tray menu instead");

        if (!string.IsNullOrWhiteSpace(_config.ScreenshotShortcut))
        {
            _screenshotHotkey = new GlobalHotkey(2, _config.ScreenshotShortcut, () => _frameFeature.TakeScreenshotOrShow());
            if (!_screenshotHotkey.IsRegistered)
                Logger.Warn($"Could not register screenshot hotkey '{_config.ScreenshotShortcut}' — may be in use by Snipping Tool or another app");
        }

        _trayIconImage = AppUtilities.LoadOrCreateIcon();

        var toggleFrameItem = new ToolStripMenuItem("Sizing Frame");
        if (_frameHotkey.IsRegistered)
            toggleFrameItem.ShortcutKeyDisplayString = _config.FrameToggleShortcut;
        toggleFrameItem.Click += (_, _) => _frameFeature.Toggle();

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
        _trayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            toggleFrameItem,
            new ToolStripSeparator(),
            _startWithWindowsItem,
            openConfigItem,
            new ToolStripSeparator(),
            exitItem
        });

        _frameFeature.ScreenshotSaved += OnScreenshotSaved;

        Logger.Info("Crop Stage started.");
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
