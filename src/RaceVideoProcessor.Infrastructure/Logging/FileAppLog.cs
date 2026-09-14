using RaceVideoProcessor.Core.Interfaces;

namespace RaceVideoProcessor.Infrastructure.Logging;

public sealed class FileAppLog : IAppLog
{
    private readonly object _gate = new();
    public event EventHandler<string>? LineWritten;
    public string LogFilePath { get; }

    public FileAppLog(string logFilePath)
    {
        LogFilePath = logFilePath;
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARNING", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        // Never pass credentials or tokens here: every line is shown on the Logs page and kept on disk.
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {level,-7} {message}";
        lock (_gate)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        LineWritten?.Invoke(this, line);
    }
}
