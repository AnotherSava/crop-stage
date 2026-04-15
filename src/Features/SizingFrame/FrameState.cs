using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CropStage.Features.SizingFrame;

public sealed class FrameState
{
    private static readonly string StatePath = Path.Combine(AppContext.BaseDirectory, "state.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
    }
}
