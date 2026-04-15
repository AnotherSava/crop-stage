using System.IO;

namespace CropStage;

public static class Logger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;

    public static void Init()
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crop-stage.log");
            lock (_lock)
            {
                _writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
            }
        }
        catch
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Close()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
        }
    }
}
