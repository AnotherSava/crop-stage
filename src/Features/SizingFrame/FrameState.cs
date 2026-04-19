using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CropStage.Features.SizingFrame;

public enum ClipboardMode
{
    Image,
    Path,
    None
}

public enum CrosshairMode
{
    None,
    FirstPoint,
    BothPoints
}

public sealed class FrameState
{
    private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "state.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private FrameStateData _data;

    public FrameState(AppConfig config) : this(config, StatePath) { }

    internal FrameState(AppConfig config, string path)
    {
        _path = path;
        _data = Load(config);
    }

    public int Width
    {
        get => _data.Width;
        set { if (_data.Width == value) return; _data.Width = value; Save(); }
    }
    public int Height
    {
        get => _data.Height;
        set { if (_data.Height == value) return; _data.Height = value; Save(); }
    }
    public string Folder
    {
        get => _data.Folder;
        set { if (_data.Folder == value) return; _data.Folder = value; Save(); }
    }
    public string Filename
    {
        get => _data.Filename;
        set { if (_data.Filename == value) return; _data.Filename = value; Save(); }
    }
    public bool HideWithEsc
    {
        get => _data.HideWithEsc;
        set { if (_data.HideWithEsc == value) return; _data.HideWithEsc = value; Save(); }
    }
    public bool Resizable
    {
        get => _data.Resizable;
        set { if (_data.Resizable == value) return; _data.Resizable = value; Save(); }
    }
    public int? Left
    {
        get => _data.Left;
        set { if (_data.Left == value) return; _data.Left = value; Save(); }
    }
    public int? Top
    {
        get => _data.Top;
        set { if (_data.Top == value) return; _data.Top = value; Save(); }
    }
    public bool StartWithWindowsInitialized
    {
        get => _data.StartWithWindowsInitialized;
        set { if (_data.StartWithWindowsInitialized == value) return; _data.StartWithWindowsInitialized = value; Save(); }
    }
    public ClipboardMode ClipboardMode
    {
        get => _data.ClipboardMode;
        set { if (_data.ClipboardMode == value) return; _data.ClipboardMode = value; Save(); }
    }
    public CrosshairMode CrosshairMode
    {
        get => _data.CrosshairMode;
        set { if (_data.CrosshairMode == value) return; _data.CrosshairMode = value; Save(); }
    }

    public string ResolvedFolder => AppConfig.ExpandEnvironmentVariables(_data.Folder);

    private FrameStateData Load(AppConfig config)
    {
        var defaults = new FrameStateData
        {
            Width = config.DefaultFrameWidth,
            Height = config.DefaultFrameHeight,
            Folder = config.DefaultScreenshotFolder,
            Filename = config.DefaultScreenshotFilename
        };

        if (!File.Exists(_path))
            return defaults;

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<FrameStateData>(json, JsonOptions);
            if (loaded == null) return defaults;
            if (loaded.Width <= 0) loaded.Width = defaults.Width;
            if (loaded.Height <= 0) loaded.Height = defaults.Height;
            if (string.IsNullOrWhiteSpace(loaded.Folder)) loaded.Folder = defaults.Folder;
            if (string.IsNullOrWhiteSpace(loaded.Filename)) loaded.Filename = defaults.Filename;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Logger.Warn($"Could not load state.json: {ex.Message} — using defaults");
            return defaults;
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn($"Could not save state.json: {ex.Message}");
        }
    }

    internal sealed class FrameStateData
    {
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("folder")] public string Folder { get; set; } = "";
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
        [JsonPropertyName("hideWithEsc")] public bool HideWithEsc { get; set; } = true;
        [JsonPropertyName("resizable")] public bool Resizable { get; set; } = true;
        [JsonPropertyName("left")] public int? Left { get; set; }
        [JsonPropertyName("top")] public int? Top { get; set; }
        [JsonPropertyName("startWithWindowsInitialized")] public bool StartWithWindowsInitialized { get; set; }
        [JsonPropertyName("clipboardMode")] public ClipboardMode ClipboardMode { get; set; } = ClipboardMode.Path;
        [JsonPropertyName("crosshairMode")] public CrosshairMode CrosshairMode { get; set; } = CrosshairMode.BothPoints;
    }
}
