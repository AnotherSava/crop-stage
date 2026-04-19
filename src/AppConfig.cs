using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CropStage;

public sealed class AppConfig
{
    private static readonly string ExeDir = AppContext.BaseDirectory;
    private static readonly string SettingsPath = Path.Combine(ExeDir, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private SettingsData _settings = null!;
    private readonly string _settingsFilePath;

    public AppConfig()
    {
        _settingsFilePath = SettingsPath;
        _settings = Load(_settingsFilePath);
    }

    internal AppConfig(string settingsPath)
    {
        _settingsFilePath = settingsPath;
        _settings = Load(settingsPath);
    }

    public string FrameToggleShortcut => _settings.FrameToggleShortcut;
    public string ScreenshotShortcut => _settings.ScreenshotShortcut;
    public string AreaSelectShortcut => _settings.AreaSelectShortcut;
    public string FrameBorderColor => _settings.FrameBorderColor;
    public int FrameBorderThickness => _settings.FrameBorderThickness;
    public int DefaultFrameWidth => _settings.DefaultFrameWidth;
    public int DefaultFrameHeight => _settings.DefaultFrameHeight;
    public string DefaultScreenshotFolder => _settings.DefaultScreenshotFolder;
    public string DefaultScreenshotFilename => _settings.DefaultScreenshotFilename;

    private static SettingsData Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Config file not found: '{filePath}'. The file should be in the same directory as the executable.");

        var json = File.ReadAllText(filePath);
        return Deserialize(json);
    }

    private static SettingsData Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
        Validate(result);
        return result;
    }

    internal static void Validate(SettingsData settings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.FrameToggleShortcut)) errors.Add("'frameToggleShortcut' is missing or empty");
        if (string.IsNullOrWhiteSpace(settings.FrameBorderColor)) errors.Add("'frameBorderColor' is missing or empty");
        else if (!TryParseHexColor(settings.FrameBorderColor, out _)) errors.Add("'frameBorderColor' is not a valid hex color (e.g. #FF0000)");
        if (settings.FrameBorderThickness <= 0) errors.Add("'frameBorderThickness' must be positive");
        if (settings.DefaultFrameWidth <= 0) errors.Add("'defaultFrameWidth' must be positive");
        if (settings.DefaultFrameHeight <= 0) errors.Add("'defaultFrameHeight' must be positive");
        if (string.IsNullOrWhiteSpace(settings.DefaultScreenshotFolder)) errors.Add("'defaultScreenshotFolder' is missing or empty");
        if (string.IsNullOrWhiteSpace(settings.DefaultScreenshotFilename)) errors.Add("'defaultScreenshotFilename' is missing or empty");
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid config: " + string.Join("\n", errors));
    }

    public static bool TryParseHexColor(string hex, out (byte R, byte G, byte B) color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex)) return false;
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        if (s.Length != 6) return false;
        if (!byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r)) return false;
        if (!byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)) return false;
        if (!byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b)) return false;
        color = (r, g, b);
        return true;
    }

    public static string ExpandEnvironmentVariables(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return Environment.ExpandEnvironmentVariables(path);
    }

    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CropStage";

    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        if (key == null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}

public sealed class SettingsData
{
    [JsonPropertyName("frameToggleShortcut")]
    public string FrameToggleShortcut { get; set; } = "";

    [JsonPropertyName("screenshotShortcut")]
    public string ScreenshotShortcut { get; set; } = "";

    [JsonPropertyName("areaSelectShortcut")]
    public string AreaSelectShortcut { get; set; } = "";

    [JsonPropertyName("frameBorderColor")]
    public string FrameBorderColor { get; set; } = "";

    [JsonPropertyName("frameBorderThickness")]
    public int FrameBorderThickness { get; set; }

    [JsonPropertyName("defaultFrameWidth")]
    public int DefaultFrameWidth { get; set; }

    [JsonPropertyName("defaultFrameHeight")]
    public int DefaultFrameHeight { get; set; }

    [JsonPropertyName("defaultScreenshotFolder")]
    public string DefaultScreenshotFolder { get; set; } = "";

    [JsonPropertyName("defaultScreenshotFilename")]
    public string DefaultScreenshotFilename { get; set; } = "";
}
